using EdgeHop.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// Phase 1 checkpoint tests for <see cref="SymbolIdFormat"/>. These run entirely on
/// hand-built compilations (<see cref="CSharpCompilation.Create"/> over small source
/// snippets) — no Neo4j, no MSBuild, no file system beyond metadata references.
/// </summary>
public class SymbolIdFormatTests
{
    // ----------------------------------------------------------------------------
    // Compilation plumbing
    // ----------------------------------------------------------------------------

    private static readonly IReadOnlyList<MetadataReference> References = BuildReferences();

    private static IReadOnlyList<MetadataReference> BuildReferences()
    {
        // De-dupe by path: on .NET Core typeof(object) and typeof(List<>) both live in
        // System.Private.CoreLib, and passing duplicate references to the compilation
        // is at best wasteful.
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            typeof(object).Assembly.Location,
            typeof(List<>).Assembly.Location,
        };

        // Add the reference facades from the trusted platform assemblies so that
        // simple snippets referencing System.Runtime / System.Collections types
        // compile without missing-reference errors.
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (name is "System.Runtime" or "System.Collections" or "netstandard")
                {
                    paths.Add(path);
                }
            }
        }

        return paths
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
    }

    private static CSharpCompilation CreateCompilation(params string[] sources)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName: "SymbolIdFormatTestAssembly",
            syntaxTrees: sources.Select(s => CSharpSyntaxTree.ParseText(s)),
            references: References,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Test compilation must be error-free but had: " + string.Join("; ", errors));

        return compilation;
    }

    private static INamedTypeSymbol RequireType(Compilation compilation, string metadataName)
    {
        var symbol = compilation.GetTypeByMetadataName(metadataName);
        Assert.NotNull(symbol);
        return symbol!;
    }

    private static T RequireSingleMember<T>(INamespaceOrTypeSymbol type, string name)
        where T : class, ISymbol
    {
        return Assert.IsAssignableFrom<T>(type.GetMembers(name).Single());
    }

    // ----------------------------------------------------------------------------
    // 1. Overloads: Foo(int) vs Foo(string) must produce different IDs.
    // ----------------------------------------------------------------------------

    [Fact]
    public void Overloads_ProduceDifferentIds()
    {
        const string source = """
            namespace N
            {
                public class C
                {
                    public void Foo(int x) { }
                    public void Foo(string s) { }
                }
            }
            """;
        var compilation = CreateCompilation(source);
        var type = RequireType(compilation, "N.C");

        var overloads = type.GetMembers("Foo").OfType<IMethodSymbol>().ToList();
        Assert.Equal(2, overloads.Count);

        var ids = overloads.Select(SymbolIdFormat.GetId).ToList();
        Assert.NotEqual(ids[0], ids[1]);

        // The parameter types are what disambiguate the overloads.
        Assert.All(ids, id => Assert.StartsWith("Method:", id, StringComparison.Ordinal));

        // Exact-string pins: the rendered form is the LOCKED stable-ID format. Any
        // accidental change to SymbolIdFormat.Format (e.g. dropping the return type)
        // would silently re-key every member node in an existing graph, so it must
        // fail loudly here rather than merely keeping the ids distinct.
        Assert.Contains("Method:void N.C.Foo(int)", ids);
        Assert.Contains("Method:void N.C.Foo(string)", ids);
    }

    [Fact]
    public void RefAndOutOverloads_ProduceDifferentIds()
    {
        // C# permits overloading on ref-kind alone. Disambiguating these relies solely
        // on SymbolDisplayParameterOptions.IncludeParamsRefOut in the format — if that
        // flag regressed, ByValue(int)/ByValue(ref int) would both render as "(int)"
        // and two distinct methods would collapse onto one graph node.
        const string source = """
            namespace N
            {
                public class C
                {
                    public void ByValue(int x) { }
                    public void ByValue(ref int x) { }
                    public void WithOut(int x) { }
                    public void WithOut(out int x) { x = 0; }
                }
            }
            """;
        var compilation = CreateCompilation(source);
        var type = RequireType(compilation, "N.C");

        var byValueIds = type.GetMembers("ByValue").OfType<IMethodSymbol>()
            .Select(SymbolIdFormat.GetId).ToList();
        Assert.Equal(2, byValueIds.Count);
        Assert.NotEqual(byValueIds[0], byValueIds[1]);
        Assert.Contains("Method:void N.C.ByValue(int)", byValueIds);
        Assert.Contains("Method:void N.C.ByValue(ref int)", byValueIds);

        var withOutIds = type.GetMembers("WithOut").OfType<IMethodSymbol>()
            .Select(SymbolIdFormat.GetId).ToList();
        Assert.Equal(2, withOutIds.Count);
        Assert.NotEqual(withOutIds[0], withOutIds[1]);
        Assert.Contains("Method:void N.C.WithOut(int)", withOutIds);
        Assert.Contains("Method:void N.C.WithOut(out int)", withOutIds);
    }

    // ----------------------------------------------------------------------------
    // 2. Generic collapse: constructed List<int> / List<string> → the one List<T> ID.
    // ----------------------------------------------------------------------------

    [Fact]
    public void ConstructedGenericTypes_CollapseToDefinitionId()
    {
        var compilation = CreateCompilation("namespace N { public class Placeholder { } }");

        var listDefinition = RequireType(compilation, "System.Collections.Generic.List`1");
        var intType = compilation.GetSpecialType(SpecialType.System_Int32);
        var stringType = compilation.GetSpecialType(SpecialType.System_String);

        var listOfInt = listDefinition.Construct(intType);
        var listOfString = listDefinition.Construct(stringType);

        // Sanity: Construct really produced distinct constructed symbols.
        Assert.False(SymbolEqualityComparer.Default.Equals(listDefinition, listOfInt));
        Assert.False(SymbolEqualityComparer.Default.Equals(listOfInt, listOfString));

        var definitionId = SymbolIdFormat.GetId(listDefinition);
        Assert.StartsWith("NamedType:", definitionId, StringComparison.Ordinal);
        Assert.Equal(definitionId, SymbolIdFormat.GetId(listOfInt));
        Assert.Equal(definitionId, SymbolIdFormat.GetId(listOfString));
    }

    [Fact]
    public void GenericFieldUsages_FieldTypesCollapseToDefinitionId()
    {
        const string source = """
            using System.Collections.Generic;

            namespace N
            {
                public class C
                {
                    public List<int> Ints = new List<int>();
                    public List<string> Strings = new List<string>();
                }
            }
            """;
        var compilation = CreateCompilation(source);
        var type = RequireType(compilation, "N.C");

        var intsField = RequireSingleMember<IFieldSymbol>(type, "Ints");
        var stringsField = RequireSingleMember<IFieldSymbol>(type, "Strings");

        var listDefinition = RequireType(compilation, "System.Collections.Generic.List`1");
        var definitionId = SymbolIdFormat.GetId(listDefinition);

        // Both usages point at the single List<T> definition node.
        Assert.Equal(definitionId, SymbolIdFormat.GetId(intsField.Type));
        Assert.Equal(definitionId, SymbolIdFormat.GetId(stringsField.Type));

        // The two field declarations themselves remain distinct symbols/IDs.
        Assert.NotEqual(SymbolIdFormat.GetId(intsField), SymbolIdFormat.GetId(stringsField));
    }

    // ----------------------------------------------------------------------------
    // 3. Constructed method on a generic type → same ID as its definition method.
    // ----------------------------------------------------------------------------

    [Fact]
    public void MethodOnConstructedGenericType_MatchesDefinitionMethodId()
    {
        const string source = """
            namespace N
            {
                public class Box<T>
                {
                    public T Get() => default!;
                }
            }
            """;
        var compilation = CreateCompilation(source);

        var boxDefinition = RequireType(compilation, "N.Box`1");
        var definitionGet = RequireSingleMember<IMethodSymbol>(boxDefinition, "Get");

        var intType = compilation.GetSpecialType(SpecialType.System_Int32);
        var boxOfInt = boxDefinition.Construct(intType);
        var constructedGet = RequireSingleMember<IMethodSymbol>(boxOfInt, "Get");

        // Sanity: Box<int>.Get() is a different symbol than Box<T>.Get()...
        Assert.False(SymbolEqualityComparer.Default.Equals(definitionGet, constructedGet));
        // ...but OriginalDefinition normalization collapses it onto the definition ID.
        Assert.Equal(SymbolIdFormat.GetId(definitionGet), SymbolIdFormat.GetId(constructedGet));
    }

    [Fact]
    public void ConstructedGenericMethod_MatchesDefinitionMethodId()
    {
        const string source = """
            namespace N
            {
                public class C
                {
                    public T Identity<T>(T value) => value;
                }
            }
            """;
        var compilation = CreateCompilation(source);
        var type = RequireType(compilation, "N.C");

        var definition = RequireSingleMember<IMethodSymbol>(type, "Identity");
        var intType = compilation.GetSpecialType(SpecialType.System_Int32);
        var constructed = definition.Construct(intType);

        Assert.False(SymbolEqualityComparer.Default.Equals(definition, constructed));
        Assert.Equal(SymbolIdFormat.GetId(definition), SymbolIdFormat.GetId(constructed));
    }

    // ----------------------------------------------------------------------------
    // 4. Partial class across two syntax trees: one symbol, one ID; both
    //    declarations resolve to equal IDs.
    // ----------------------------------------------------------------------------

    [Fact]
    public void PartialClass_TwoDeclarations_YieldOneSymbolAndOneId()
    {
        const string part1 = "namespace N { public partial class P { public void A() { } } }";
        const string part2 = "namespace N { public partial class P { public void B() { } } }";
        var compilation = CreateCompilation(part1, part2);

        var type = RequireType(compilation, "N.P");
        Assert.Equal(2, type.DeclaringSyntaxReferences.Length);

        // Resolve the declared symbol independently from each declaration site.
        var idsFromDeclarations = new List<string>();
        foreach (var declarationReference in type.DeclaringSyntaxReferences)
        {
            var declaration = Assert.IsAssignableFrom<ClassDeclarationSyntax>(
                declarationReference.GetSyntax());
            var model = compilation.GetSemanticModel(declaration.SyntaxTree);
            var declaredSymbol = model.GetDeclaredSymbol(declaration);
            Assert.NotNull(declaredSymbol);
            idsFromDeclarations.Add(SymbolIdFormat.GetId(declaredSymbol!));
        }

        Assert.Equal(2, idsFromDeclarations.Count);
        Assert.Equal(idsFromDeclarations[0], idsFromDeclarations[1]);
        Assert.Equal(SymbolIdFormat.GetId(type), idsFromDeclarations[0]);
        Assert.Equal("NamedType:N.P", idsFromDeclarations[0]);
    }

    [Fact]
    public void PartialMethod_DefinitionAndImplementationParts_YieldOneId()
    {
        // Unlike partial TYPES (one ISymbol for all declarations), a partial METHOD's
        // definition part and implementation part are two DISTINCT IMethodSymbol
        // instances, and OriginalDefinition does NOT unify them — each part is its own
        // original definition. The stable IDs coincide only because both parts render
        // to the same display string under the locked format, so prove that explicitly.
        const string definitionSource =
            "namespace N { public partial class P { public partial void M(int x); } }";
        const string implementationSource =
            "namespace N { public partial class P { public partial void M(int x) { } } }";
        var compilation = CreateCompilation(definitionSource, implementationSource);

        var type = RequireType(compilation, "N.P");
        var member = RequireSingleMember<IMethodSymbol>(type, "M");

        // Resolve both parts explicitly, regardless of which part GetMembers surfaced.
        var definitionPart = member.PartialDefinitionPart ?? member;
        var implementationPart = member.PartialImplementationPart ?? member;
        Assert.Null(definitionPart.PartialDefinitionPart);       // really the definition part
        Assert.Null(implementationPart.PartialImplementationPart); // really the implementation part

        // Sanity: two distinct symbols, NOT collapsed by OriginalDefinition.
        Assert.False(SymbolEqualityComparer.Default.Equals(definitionPart, implementationPart));
        Assert.False(SymbolEqualityComparer.Default.Equals(
            definitionPart.OriginalDefinition, implementationPart.OriginalDefinition));

        // The stable IDs must nevertheless coincide: one graph node per partial method.
        Assert.Equal(SymbolIdFormat.GetId(definitionPart), SymbolIdFormat.GetId(implementationPart));
        Assert.Equal("Method:void N.P.M(int)", SymbolIdFormat.GetId(definitionPart));
    }

    // ----------------------------------------------------------------------------
    // 5. Nested types fully qualify with containing type and namespace.
    // ----------------------------------------------------------------------------

    [Fact]
    public void NestedType_FullyQualifiesWithNamespaceAndContainingType()
    {
        const string source = """
            namespace N1.N2
            {
                public class Outer
                {
                    public class Inner
                    {
                        public void M() { }
                    }
                }
            }
            """;
        var compilation = CreateCompilation(source);

        var inner = RequireType(compilation, "N1.N2.Outer+Inner");
        Assert.Equal("NamedType:N1.N2.Outer.Inner", SymbolIdFormat.GetId(inner));

        // A member of the nested type also carries the full qualification chain.
        var method = RequireSingleMember<IMethodSymbol>(inner, "M");
        var methodId = SymbolIdFormat.GetId(method);
        Assert.StartsWith("Method:", methodId, StringComparison.Ordinal);
        Assert.Contains("N1.N2.Outer.Inner.M(", methodId, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------
    // 6. Kind prefixes: Method:/Property:/NamedType:/Field:, and cross-kind
    //    collision prevention when display strings coincide.
    // ----------------------------------------------------------------------------

    [Fact]
    public void KindPrefixes_AreEmittedPerSymbolKind()
    {
        const string source = """
            namespace N
            {
                public class C
                {
                    public int Value { get; set; }
                    public int Count() => 0;
                    public int Total = 0;
                }
            }
            """;
        var compilation = CreateCompilation(source);
        var type = RequireType(compilation, "N.C");

        var typeId = SymbolIdFormat.GetId(type);
        var propertyId = SymbolIdFormat.GetId(RequireSingleMember<IPropertySymbol>(type, "Value"));
        var methodId = SymbolIdFormat.GetId(RequireSingleMember<IMethodSymbol>(type, "Count"));
        var fieldId = SymbolIdFormat.GetId(RequireSingleMember<IFieldSymbol>(type, "Total"));

        Assert.StartsWith("NamedType:", typeId, StringComparison.Ordinal);
        Assert.StartsWith("Property:", propertyId, StringComparison.Ordinal);
        Assert.StartsWith("Method:", methodId, StringComparison.Ordinal);
        Assert.StartsWith("Field:", fieldId, StringComparison.Ordinal);

        Assert.Equal(4, new[] { typeId, propertyId, methodId, fieldId }.Distinct().Count());

        // Exact-string pins per kind: lock the full rendering, not just the prefix,
        // so Format drift (e.g. losing SymbolDisplayMemberOptions.IncludeType) fails
        // here instead of silently re-keying every member node in an existing graph.
        Assert.Equal("NamedType:N.C", typeId);
        Assert.Equal("Property:int N.C.Value", propertyId);
        Assert.Equal("Method:int N.C.Count()", methodId);
        Assert.Equal("Field:int N.C.Total", fieldId);
    }

    [Fact]
    public void MemberIds_ExactRendering_IsPinned_ForFormatSensitiveShapes()
    {
        // The determinism test only compares two in-process compilations against each
        // other, so it moves WITH the format and cannot detect drift. These pins are
        // the absolute anchor: they exercise the format options that no relative
        // assertion elsewhere would catch if they regressed —
        //   - memberOptions.IncludeType            → return type in the rendering
        //   - genericsOptions.IncludeTypeParameters → "<T>" on the generic method
        //   - genericsOptions.IncludeTypeConstraints→ "where T : class"
        //   - miscellaneousOptions.ExpandNullable   → Nullable<T> instead of T?
        //   - miscellaneousOptions.UseSpecialTypes  → "int", not "Int32"
        // If any pin below fails, the stable-ID format changed: existing graphs would
        // be orphaned on the next extractor run. Treat that as a breaking change.
        const string source = """
            namespace N
            {
                public class C
                {
                    public int? MaybeCount(int? x) => x;
                    public T First<T>(T item) where T : class => item;
                }
            }
            """;
        var compilation = CreateCompilation(source);
        var type = RequireType(compilation, "N.C");

        var maybeCount = RequireSingleMember<IMethodSymbol>(type, "MaybeCount");
        var first = RequireSingleMember<IMethodSymbol>(type, "First");

        Assert.Equal(
            "Method:System.Nullable<int> N.C.MaybeCount(System.Nullable<int>)",
            SymbolIdFormat.GetId(maybeCount));
        Assert.Equal(
            "Method:T N.C.First<T>(T) where T : class",
            SymbolIdFormat.GetId(first));
    }

    [Fact]
    public void KindPrefix_PreventsCrossKindCollision_WhenDisplayStringsCoincide()
    {
        // A field "int N.C.X" and a property "int N.C.X" (in separate compilations —
        // they could not coexist in one type) render to the SAME display string under
        // the ID format. Only the kind prefix keeps their IDs from colliding.
        var fieldCompilation = CreateCompilation(
            "namespace N { public class C { public int X = 0; } }");
        var propertyCompilation = CreateCompilation(
            "namespace N { public class C { public int X { get; set; } } }");

        var field = RequireSingleMember<IFieldSymbol>(
            RequireType(fieldCompilation, "N.C"), "X");
        var property = RequireSingleMember<IPropertySymbol>(
            RequireType(propertyCompilation, "N.C"), "X");

        Assert.Equal(
            field.ToDisplayString(SymbolIdFormat.Format),
            property.ToDisplayString(SymbolIdFormat.Format));

        Assert.NotEqual(SymbolIdFormat.GetId(field), SymbolIdFormat.GetId(property));
        Assert.StartsWith("Field:", SymbolIdFormat.GetId(field), StringComparison.Ordinal);
        Assert.StartsWith("Property:", SymbolIdFormat.GetId(property), StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------
    // 7. Determinism: identical source compiled twice, independently, yields
    //    string-identical IDs for every corresponding symbol.
    // ----------------------------------------------------------------------------

    [Fact]
    public void Ids_AreDeterministic_AcrossSeparatelyCreatedIdenticalCompilations()
    {
        const string source = """
            using System.Collections.Generic;

            namespace N1.N2
            {
                public class Outer
                {
                    public class Inner
                    {
                        public List<int> Items = new List<int>();
                        public void Foo(int x) { }
                        public void Foo(string s) { }
                        public T Identity<T>(T value) => value;
                    }
                }
            }
            """;

        static string[] CollectIds(string src)
        {
            var compilation = CreateCompilation(src);
            var inner = RequireType(compilation, "N1.N2.Outer+Inner");
            var listOfInt = RequireType(compilation, "System.Collections.Generic.List`1")
                .Construct(compilation.GetSpecialType(SpecialType.System_Int32));

            var symbols = new List<ISymbol> { inner, listOfInt };
            symbols.AddRange(
                inner.GetMembers()
                    .Where(m => m is not IMethodSymbol { MethodKind: MethodKind.Constructor })
                    .OrderBy(m => m.ToDisplayString(SymbolIdFormat.Format), StringComparer.Ordinal));

            return symbols.Select(SymbolIdFormat.GetId).ToArray();
        }

        var first = CollectIds(source);
        var second = CollectIds(source);

        Assert.NotEmpty(first);
        Assert.Equal(first, second);

        // And calling GetId twice on the very same symbol instance is stable too.
        var compilation = CreateCompilation(source);
        var type = RequireType(compilation, "N1.N2.Outer+Inner");
        Assert.Equal(SymbolIdFormat.GetId(type), SymbolIdFormat.GetId(type));
    }
}
