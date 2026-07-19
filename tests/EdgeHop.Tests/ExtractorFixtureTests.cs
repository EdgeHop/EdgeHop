using EdgeHop.Core;
using EdgeHop.Roslyn;
using Microsoft.CodeAnalysis;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// Phase 2 checkpoint (README step 4) — the regression anchor for the C# extractor.
/// Pointed at <c>fixtures/TinyFixture/TinyFixture.sln</c>, the extractor must produce
/// exactly the graph enumerated in <c>fixtures/TinyFixture/EXPECTED-GRAPH.md</c>:
/// <b>19 nodes / 28 edges</b> (CONTAINS 18, CALLS 4, IMPLEMENTS 1, INHERITS 1,
/// OVERRIDES 1, REFERENCES 3). Expected IDs are computed through the Phase-1-tested
/// <see cref="SymbolIdFormat"/> over the fixture compilation — never hard-coded — per
/// EXPECTED-GRAPH.md. Runs MSBuildWorkspace in-process via <see cref="MsBuildFixture"/>;
/// no Neo4j involved.
/// </summary>
[Collection(MsBuildTestCollection.Name)]
public sealed class ExtractorFixtureTests
{
    private readonly MsBuildFixture _fx;

    public ExtractorFixtureTests(MsBuildFixture fx) => _fx = fx;

    // ---------------------------------------------------------------------------
    // 1. Workspace load
    // ---------------------------------------------------------------------------

    [Fact]
    public void Workspace_loads_with_zero_failure_diagnostics()
    {
        var failures = _fx.LoadResult.FailureDiagnostics;
        Assert.True(
            failures.Count == 0,
            $"Expected zero WorkspaceFailed Failure diagnostics, got {failures.Count}:"
            + Environment.NewLine + string.Join(Environment.NewLine, failures));

        // Sanity: the one fixture project actually loaded.
        Assert.Single(_fx.Solution.Projects, p => p.Name == "TinyFixture");
    }

    // ---------------------------------------------------------------------------
    // 2. Exact node assertions — 19 nodes as (Kind, Name) pairs
    //    Name is symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
    //    per the extraction contract (members include return/field type, containing
    //    type, and parameter types and names).
    // ---------------------------------------------------------------------------

    private static readonly IReadOnlySet<(string Kind, string Name)> ExpectedKindNamePairs =
        new HashSet<(string, string)>
        {
            (SymbolKinds.Namespace, "TinyFixture"),
            (SymbolKinds.NamedType, "IGreeter"),
            (SymbolKinds.NamedType, "Greeter"),
            (SymbolKinds.NamedType, "LoudGreeter"),
            (SymbolKinds.NamedType, "Caller"),
            (SymbolKinds.NamedType, "App"),
            (SymbolKinds.NamedType, "PartialThing"),
            (SymbolKinds.NamedType, "Decorator"),
            (SymbolKinds.Method, "string IGreeter.Greet(string name)"),
            (SymbolKinds.Method, "string Greeter.Greet(string name)"),
            (SymbolKinds.Method, "string LoudGreeter.Greet(string name)"),
            (SymbolKinds.Method, "string Caller.CallGreet()"),
            (SymbolKinds.Method, "string App.Run()"),
            (SymbolKinds.Method, "int PartialThing.PartOneValue()"),
            (SymbolKinds.Method, "int PartialThing.PartTwoValue()"),
            // The PRIMARY constructor is a source-written constructor: it gets a node.
            (SymbolKinds.Method, "Decorator.Decorator(Greeter inner)"),
            (SymbolKinds.Method, "string Decorator.Decorate()"),
            (SymbolKinds.Field, "Greeter Caller._greeter"),
            (SymbolKinds.Property, "Greeter Decorator.Inner"),
        };

    [Fact]
    public void Nodes_are_exactly_the_nineteen_expected_kind_name_pairs()
    {
        var nodes = _fx.Extraction.Nodes;
        Assert.Equal(19, nodes.Count);

        var actual = nodes.Select(n => (n.Kind, n.Name)).ToHashSet();
        Assert.Equal(19, actual.Count); // no two fixture nodes share (Kind, Name)

        AssertSetEqual(ExpectedKindNamePairs, actual, "Node (Kind, Name) set");
    }

    [Fact]
    public async Task Node_ids_match_symbolidformat_over_the_fixture_compilation()
    {
        var ids = await GetExpectedIdsAsync();
        var actual = _fx.Extraction.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);

        AssertSetEqual(ids.AllNodeIds(), actual, "Node ID set");

        // Contract: Node.Kind must equal the ID's kind prefix.
        foreach (var node in _fx.Extraction.Nodes)
        {
            Assert.True(
                node.Id.StartsWith(node.Kind + ":", StringComparison.Ordinal),
                $"Node Kind '{node.Kind}' is not the prefix of its ID '{node.Id}'.");
        }
    }

    [Fact]
    public void PartialThing_yields_exactly_one_namedtype_node()
    {
        var matches = _fx.Extraction.Nodes
            .Where(n => n.Kind == SymbolKinds.NamedType && n.Name == "PartialThing")
            .ToList();

        Assert.True(matches.Count == 1,
            $"Expected exactly ONE PartialThing NamedType node (partial type identity), found {matches.Count}.");

        // Two declaring documents — assert only that one of them was chosen
        // (the contract picks the ordinal-alphabetically-first, but the exact pick
        // is deliberately not pinned here per EXPECTED-GRAPH.md).
        var doc = matches[0].SourceDoc;
        Assert.True(
            doc is "PartialThing.Part1.cs" or "PartialThing.Part2.cs",
            $"PartialThing SourceDoc must be one of its two declaring documents, got '{doc}'.");
    }

    // ---------------------------------------------------------------------------
    // Negative node assertions — no metadata, implicit-ctor, or accessor nodes
    // ---------------------------------------------------------------------------

    [Fact]
    public void No_nodes_for_metadata_implicit_ctor_or_accessor_symbols()
    {
        var nodes = _fx.Extraction.Nodes;

        // No metadata/framework symbols (string, object, System.*) ever become nodes.
        foreach (var node in nodes)
        {
            Assert.DoesNotContain("System.", node.Id, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(nodes, n => n.Name is "string" or "object" or "String" or "Object");

        // No implicit default-constructor nodes: a ctor ID would render "Type.Type(...)".
        // (Decorator is deliberately absent from this list — its PRIMARY constructor is
        // source-written and expected as a node; see the primary-constructor test.)
        foreach (var typeName in new[] { "IGreeter", "Greeter", "LoudGreeter", "Caller", "App", "PartialThing" })
        {
            Assert.DoesNotContain(nodes, n => n.Id.Contains($"{typeName}.{typeName}(", StringComparison.Ordinal));
        }

        // Accessor/backing-field exclusion, asserted NON-vacuously: Decorator.Inner is an
        // auto-property, so a regressed MethodKind filter would leak get_Inner (rendered
        // "…Decorator.Inner.get") and a regressed implicit-symbol filter would leak the
        // compiler-generated backing field.
        Assert.DoesNotContain(nodes, n => n.Id.Contains("Inner.get", StringComparison.Ordinal));
        Assert.DoesNotContain(nodes, n => n.Id.Contains("Inner.set", StringComparison.Ordinal));
        Assert.DoesNotContain(nodes, n => n.Id.Contains("k__BackingField", StringComparison.Ordinal));

        // No accessor or other compiler-generated member nodes: the kind census is
        // exactly 1 Namespace, 7 NamedType, 9 Method, 1 Field, 1 Property and nothing else.
        var byKind = nodes.GroupBy(n => n.Kind).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        Assert.Equal(5, byKind.Count);
        Assert.Equal(1, byKind[SymbolKinds.Namespace]);
        Assert.Equal(7, byKind[SymbolKinds.NamedType]);
        Assert.Equal(9, byKind[SymbolKinds.Method]);
        Assert.Equal(1, byKind[SymbolKinds.Field]);
        Assert.Equal(1, byKind[SymbolKinds.Property]);
    }

    // ---------------------------------------------------------------------------
    // 3. Exact edge assertions — 28 edges
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Edges_are_exactly_the_twenty_eight_expected_triples()
    {
        var ids = await GetExpectedIdsAsync();
        var edges = _fx.Extraction.Edges;

        Assert.Equal(28, edges.Count);

        var actual = edges.Select(e => (e.FromId, e.Type, e.ToId)).ToHashSet();
        Assert.Equal(28, actual.Count); // deduped by (FromId, Type, ToId)

        AssertSetEqual(ExpectedEdgeTriples(ids), actual, "Edge (FromId, Type, ToId) set");
    }

    [Fact]
    public async Task Contains_edges_are_exactly_the_eighteen_expected()
    {
        var ids = await GetExpectedIdsAsync();
        var contains = _fx.Extraction.Edges.Where(e => e.Type == EdgeTypes.Contains).ToList();

        Assert.Equal(18, contains.Count);

        var expected = new HashSet<(string, string, string)>
        {
            // Namespace -> type (7)
            (ids.Ns, EdgeTypes.Contains, ids.IGreeter),
            (ids.Ns, EdgeTypes.Contains, ids.Greeter),
            (ids.Ns, EdgeTypes.Contains, ids.LoudGreeter),
            (ids.Ns, EdgeTypes.Contains, ids.Caller),
            (ids.Ns, EdgeTypes.Contains, ids.App),
            (ids.Ns, EdgeTypes.Contains, ids.PartialThing),
            (ids.Ns, EdgeTypes.Contains, ids.Decorator),
            // Type -> member (11)
            (ids.IGreeter, EdgeTypes.Contains, ids.IGreeterGreet),
            (ids.Greeter, EdgeTypes.Contains, ids.GreeterGreet),
            (ids.LoudGreeter, EdgeTypes.Contains, ids.LoudGreeterGreet),
            (ids.Caller, EdgeTypes.Contains, ids.CallGreet),
            (ids.Caller, EdgeTypes.Contains, ids.GreeterField),
            (ids.App, EdgeTypes.Contains, ids.Run),
            (ids.PartialThing, EdgeTypes.Contains, ids.PartOneValue),
            (ids.PartialThing, EdgeTypes.Contains, ids.PartTwoValue),
            (ids.Decorator, EdgeTypes.Contains, ids.DecoratorCtor),
            (ids.Decorator, EdgeTypes.Contains, ids.DecoratorInner),
            (ids.Decorator, EdgeTypes.Contains, ids.Decorate),
        };

        AssertSetEqual(expected, contains.Select(e => (e.FromId, e.Type, e.ToId)).ToHashSet(), "CONTAINS edge set");
    }

    [Fact]
    public async Task Calls_edges_are_exactly_the_four_expected()
    {
        var ids = await GetExpectedIdsAsync();
        var calls = _fx.Extraction.Edges.Where(e => e.Type == EdgeTypes.Calls).ToList();

        Assert.Equal(4, calls.Count);

        // base.Greet(name) — the chained .ToUpper() targets string.ToUpper: framework, skipped.
        AssertSingleEdge(calls, EdgeTypes.Calls, ids.LoudGreeterGreet, ids.GreeterGreet, "LoudGreeter.cs");
        // _greeter.Greet("x") — static receiver type Greeter resolves to the virtual slot.
        AssertSingleEdge(calls, EdgeTypes.Calls, ids.CallGreet, ids.GreeterGreet, "Caller.cs");
        // new Caller().CallGreet() — exactly ONE edge; the object creation is not an invocation.
        AssertSingleEdge(calls, EdgeTypes.Calls, ids.Run, ids.CallGreet, "App.cs");
        // Inner.Greet("deco") — the property access is not an invocation; the call is
        // attributed to Decorate (and, critically, NOT also to the primary constructor).
        AssertSingleEdge(calls, EdgeTypes.Calls, ids.Decorate, ids.GreeterGreet, "Sub/Decorator.cs");
    }

    [Fact]
    public async Task Primary_constructor_gets_a_node_but_no_calls_from_member_bodies()
    {
        var ids = await GetExpectedIdsAsync();

        // A primary constructor is a source-written constructor: it must be emitted.
        var node = GetNode(ids.DecoratorCtor);
        Assert.Equal(SymbolKinds.Method, node.Kind);
        Assert.Equal("Sub/Decorator.cs", node.SourceDoc);

        // Its declaring syntax is the WHOLE ClassDeclarationSyntax, so a naive descendant
        // walk would attribute Decorate's Inner.Greet(...) invocation to it. Nothing in
        // the constructor declaration itself (no base initializer, no parameter defaults)
        // invokes anything: the primary ctor must have ZERO outgoing CALLS edges.
        Assert.DoesNotContain(
            _fx.Extraction.Edges,
            e => e.Type == EdgeTypes.Calls && e.FromId == ids.DecoratorCtor);
    }

    [Fact]
    public async Task Implements_edge_is_exactly_greeter_to_igreeter()
    {
        var ids = await GetExpectedIdsAsync();
        var implements = _fx.Extraction.Edges.Where(e => e.Type == EdgeTypes.Implements).ToList();

        // LoudGreeter implements IGreeter only transitively — direct-interface list only.
        Assert.Single(implements);
        AssertSingleEdge(implements, EdgeTypes.Implements, ids.Greeter, ids.IGreeter, "Greeter.cs");
    }

    [Fact]
    public async Task Inherits_edge_is_exactly_loudgreeter_to_greeter()
    {
        var ids = await GetExpectedIdsAsync();
        var inherits = _fx.Extraction.Edges.Where(e => e.Type == EdgeTypes.Inherits).ToList();

        // All other classes inherit System.Object — metadata base, skipped.
        Assert.Single(inherits);
        AssertSingleEdge(inherits, EdgeTypes.Inherits, ids.LoudGreeter, ids.Greeter, "LoudGreeter.cs");
    }

    [Fact]
    public async Task Overrides_edge_is_exactly_loudgreeter_greet_to_greeter_greet()
    {
        var ids = await GetExpectedIdsAsync();
        var overrides = _fx.Extraction.Edges.Where(e => e.Type == EdgeTypes.Overrides).ToList();

        // Greeter.Greet implements IGreeter.Greet but does NOT override it (OverriddenMethod is null).
        Assert.Single(overrides);
        AssertSingleEdge(overrides, EdgeTypes.Overrides, ids.LoudGreeterGreet, ids.GreeterGreet, "LoudGreeter.cs");
    }

    [Fact]
    public async Task References_edges_are_exactly_the_three_expected()
    {
        var ids = await GetExpectedIdsAsync();
        var references = _fx.Extraction.Edges.Where(e => e.Type == EdgeTypes.References).ToList();

        // Every other parameter/return/field/property type is string or int — framework, skipped.
        Assert.Equal(3, references.Count);

        // Field type.
        AssertSingleEdge(references, EdgeTypes.References, ids.GreeterField, ids.Greeter, "Caller.cs");
        // Primary-constructor parameter type (Greeter inner).
        AssertSingleEdge(references, EdgeTypes.References, ids.DecoratorCtor, ids.Greeter, "Sub/Decorator.cs");
        // Property type — carried by the Property node itself, never by an accessor.
        AssertSingleEdge(references, EdgeTypes.References, ids.DecoratorInner, ids.Greeter, "Sub/Decorator.cs");
    }

    // ---------------------------------------------------------------------------
    // 4. Negative edge assertions — the graph is closed over emitted nodes
    // ---------------------------------------------------------------------------

    [Fact]
    public void Edge_endpoints_are_closed_over_emitted_nodes_and_never_metadata()
    {
        var nodeIds = _fx.Extraction.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var edge in _fx.Extraction.Edges)
        {
            // Closure: every endpoint must be an emitted source-declared node. This is
            // the strongest metadata guard — a framework target (string.ToUpper, an
            // implicit ctor, System.Object) can never be in the node set.
            // (Note: a plain "string " substring check would false-positive, because
            // method IDs legitimately start with their return type, e.g.
            // "Method:string TinyFixture.App.Run()".)
            Assert.True(nodeIds.Contains(edge.FromId),
                $"{edge.Type} edge FromId is not an emitted node: '{edge.FromId}'.");
            Assert.True(nodeIds.Contains(edge.ToId),
                $"{edge.Type} edge ToId is not an emitted node: '{edge.ToId}'.");

            Assert.DoesNotContain("System.", edge.ToId, StringComparison.Ordinal);
            Assert.DoesNotContain("ToUpper", edge.ToId, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------------------
    // 5. Branch + SourceDoc conventions
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Rows_carry_branch_main_and_solution_relative_forward_slash_sourcedocs()
    {
        var ids = await GetExpectedIdsAsync();
        var nodes = _fx.Extraction.Nodes;
        var edges = _fx.Extraction.Edges;

        Assert.All(nodes, n => Assert.Equal(MsBuildFixture.Branch, n.Branch));
        Assert.All(edges, e => Assert.Equal(MsBuildFixture.Branch, e.Branch));

        // No duplicate rows on the composite keys.
        Assert.Equal(nodes.Count, nodes.Select(n => (n.Branch, n.Id)).Distinct().Count());
        Assert.Equal(edges.Count, edges.Select(e => (e.Branch, e.FromId, e.ToId, e.Type)).Distinct().Count());

        // Every non-null SourceDoc is a relative, forward-slash .cs path.
        foreach (var doc in nodes.Select(n => n.SourceDoc).Concat(edges.Select(e => e.SourceDoc)))
        {
            if (doc is null)
            {
                continue;
            }

            Assert.DoesNotContain("\\", doc, StringComparison.Ordinal);
            Assert.EndsWith(".cs", doc, StringComparison.Ordinal);
            Assert.False(Path.IsPathRooted(doc), $"SourceDoc must be solution-relative, got '{doc}'.");
        }

        // Non-vacuity guard for the convention checks above: Decorator lives in a
        // SUBFOLDER, so at least one SourceDoc must contain a forward-slash separator.
        // Without this, every doc would be a bare file name and a backslash (or
        // wrong-base) regression in ToRelativeDoc could never fail this test.
        Assert.Contains(nodes, n => n.SourceDoc is not null && n.SourceDoc.Contains('/'));

        // Namespace node: SourceDoc is null per contract.
        Assert.Null(GetNode(ids.Ns).SourceDoc);

        // Most sources sit beside the .sln (bare file names); Decorator.cs sits under
        // Sub/ to pin the solution-relative forward-slash convention. Assert the full
        // mapping for every single-declaration node (PartialThing is covered separately).
        var expectedDocs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ids.IGreeter] = "IGreeter.cs",
            [ids.Greeter] = "Greeter.cs",
            [ids.LoudGreeter] = "LoudGreeter.cs",
            [ids.Caller] = "Caller.cs",
            [ids.App] = "App.cs",
            [ids.Decorator] = "Sub/Decorator.cs",
            [ids.IGreeterGreet] = "IGreeter.cs",
            [ids.GreeterGreet] = "Greeter.cs",
            [ids.LoudGreeterGreet] = "LoudGreeter.cs",
            [ids.CallGreet] = "Caller.cs",
            [ids.GreeterField] = "Caller.cs",
            [ids.Run] = "App.cs",
            [ids.PartOneValue] = "PartialThing.Part1.cs",
            [ids.PartTwoValue] = "PartialThing.Part2.cs",
            [ids.DecoratorCtor] = "Sub/Decorator.cs",
            [ids.DecoratorInner] = "Sub/Decorator.cs",
            [ids.Decorate] = "Sub/Decorator.cs",
        };
        foreach (var (id, expectedDoc) in expectedDocs)
        {
            Assert.Equal(expectedDoc, GetNode(id).SourceDoc);
        }

        // Assembly on every type/member node is the fixture assembly.
        foreach (var node in nodes.Where(n => n.Kind != SymbolKinds.Namespace))
        {
            Assert.Equal("TinyFixture", node.Assembly);
        }
    }

    // ---------------------------------------------------------------------------
    // 6. Determinism
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Extraction_is_deterministic_for_the_same_solution()
    {
        var first = _fx.Extraction; // produced once during fixture initialization
        var second = await SymbolGraphExtractor.ExtractAsync(_fx.Solution, MsBuildFixture.Branch);

        static List<string> NodeIds(ExtractionResult r) =>
            r.Nodes.Select(n => n.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();

        static List<(string From, string Type, string To)> EdgeTriples(ExtractionResult r) =>
            r.Edges.Select(e => (e.FromId, e.Type, e.ToId))
                .OrderBy(t => t.Item1, StringComparer.Ordinal)
                .ThenBy(t => t.Item2, StringComparer.Ordinal)
                .ThenBy(t => t.Item3, StringComparer.Ordinal)
                .ToList();

        Assert.Equal(NodeIds(first), NodeIds(second));
        Assert.Equal(EdgeTriples(first), EdgeTriples(second));
    }

    // ---------------------------------------------------------------------------
    // Expected-ID plumbing — IDs are computed through the Phase-1-tested
    // SymbolIdFormat over the fixture compilation, never hard-coded.
    // ---------------------------------------------------------------------------

    private sealed record ExpectedIds(
        string Ns,
        string IGreeter,
        string Greeter,
        string LoudGreeter,
        string Caller,
        string App,
        string PartialThing,
        string Decorator,
        string IGreeterGreet,
        string GreeterGreet,
        string LoudGreeterGreet,
        string CallGreet,
        string Run,
        string PartOneValue,
        string PartTwoValue,
        string DecoratorCtor,
        string DecoratorInner,
        string Decorate,
        string GreeterField)
    {
        public IReadOnlySet<string> AllNodeIds() => new HashSet<string>(StringComparer.Ordinal)
        {
            Ns,
            IGreeter, Greeter, LoudGreeter, Caller, App, PartialThing, Decorator,
            IGreeterGreet, GreeterGreet, LoudGreeterGreet, CallGreet, Run,
            PartOneValue, PartTwoValue,
            DecoratorCtor, DecoratorInner, Decorate,
            GreeterField,
        };
    }

    private static IReadOnlySet<(string From, string Type, string To)> ExpectedEdgeTriples(ExpectedIds ids) =>
        new HashSet<(string, string, string)>
        {
            // CONTAINS (18)
            (ids.Ns, EdgeTypes.Contains, ids.IGreeter),
            (ids.Ns, EdgeTypes.Contains, ids.Greeter),
            (ids.Ns, EdgeTypes.Contains, ids.LoudGreeter),
            (ids.Ns, EdgeTypes.Contains, ids.Caller),
            (ids.Ns, EdgeTypes.Contains, ids.App),
            (ids.Ns, EdgeTypes.Contains, ids.PartialThing),
            (ids.Ns, EdgeTypes.Contains, ids.Decorator),
            (ids.IGreeter, EdgeTypes.Contains, ids.IGreeterGreet),
            (ids.Greeter, EdgeTypes.Contains, ids.GreeterGreet),
            (ids.LoudGreeter, EdgeTypes.Contains, ids.LoudGreeterGreet),
            (ids.Caller, EdgeTypes.Contains, ids.CallGreet),
            (ids.Caller, EdgeTypes.Contains, ids.GreeterField),
            (ids.App, EdgeTypes.Contains, ids.Run),
            (ids.PartialThing, EdgeTypes.Contains, ids.PartOneValue),
            (ids.PartialThing, EdgeTypes.Contains, ids.PartTwoValue),
            (ids.Decorator, EdgeTypes.Contains, ids.DecoratorCtor),
            (ids.Decorator, EdgeTypes.Contains, ids.DecoratorInner),
            (ids.Decorator, EdgeTypes.Contains, ids.Decorate),
            // CALLS (4)
            (ids.LoudGreeterGreet, EdgeTypes.Calls, ids.GreeterGreet),
            (ids.CallGreet, EdgeTypes.Calls, ids.GreeterGreet),
            (ids.Run, EdgeTypes.Calls, ids.CallGreet),
            (ids.Decorate, EdgeTypes.Calls, ids.GreeterGreet),
            // IMPLEMENTS (1), INHERITS (1), OVERRIDES (1), REFERENCES (3)
            (ids.Greeter, EdgeTypes.Implements, ids.IGreeter),
            (ids.LoudGreeter, EdgeTypes.Inherits, ids.Greeter),
            (ids.LoudGreeterGreet, EdgeTypes.Overrides, ids.GreeterGreet),
            (ids.GreeterField, EdgeTypes.References, ids.Greeter),
            (ids.DecoratorCtor, EdgeTypes.References, ids.Greeter),
            (ids.DecoratorInner, EdgeTypes.References, ids.Greeter),
        };

    private async Task<ExpectedIds> GetExpectedIdsAsync()
    {
        var project = _fx.Solution.Projects.Single(p => p.Name == "TinyFixture");
        var compilation = await project.GetCompilationAsync();
        Assert.NotNull(compilation);

        INamedTypeSymbol Type(string metadataName)
        {
            var type = compilation!.GetTypeByMetadataName(metadataName);
            Assert.True(type is not null, $"Type '{metadataName}' not found in the fixture compilation.");
            return type!;
        }

        static IMethodSymbol Method(INamedTypeSymbol type, string name) =>
            type.GetMembers(name).OfType<IMethodSymbol>().Single();

        var iGreeter = Type("TinyFixture.IGreeter");
        var greeter = Type("TinyFixture.Greeter");
        var loudGreeter = Type("TinyFixture.LoudGreeter");
        var caller = Type("TinyFixture.Caller");
        var app = Type("TinyFixture.App");
        var partialThing = Type("TinyFixture.PartialThing");
        var decorator = Type("TinyFixture.Decorator");
        var greeterField = caller.GetMembers("_greeter").OfType<IFieldSymbol>().Single();

        // The primary constructor is Decorator's ONLY instance constructor (a class with
        // a primary constructor gets no implicit parameterless one).
        var decoratorCtor = decorator.InstanceConstructors.Single();
        var decoratorInner = decorator.GetMembers("Inner").OfType<IPropertySymbol>().Single();

        return new ExpectedIds(
            Ns: SymbolIdFormat.GetId(greeter.ContainingNamespace),
            IGreeter: SymbolIdFormat.GetId(iGreeter),
            Greeter: SymbolIdFormat.GetId(greeter),
            LoudGreeter: SymbolIdFormat.GetId(loudGreeter),
            Caller: SymbolIdFormat.GetId(caller),
            App: SymbolIdFormat.GetId(app),
            PartialThing: SymbolIdFormat.GetId(partialThing),
            Decorator: SymbolIdFormat.GetId(decorator),
            IGreeterGreet: SymbolIdFormat.GetId(Method(iGreeter, "Greet")),
            GreeterGreet: SymbolIdFormat.GetId(Method(greeter, "Greet")),
            LoudGreeterGreet: SymbolIdFormat.GetId(Method(loudGreeter, "Greet")),
            CallGreet: SymbolIdFormat.GetId(Method(caller, "CallGreet")),
            Run: SymbolIdFormat.GetId(Method(app, "Run")),
            PartOneValue: SymbolIdFormat.GetId(Method(partialThing, "PartOneValue")),
            PartTwoValue: SymbolIdFormat.GetId(Method(partialThing, "PartTwoValue")),
            DecoratorCtor: SymbolIdFormat.GetId(decoratorCtor),
            DecoratorInner: SymbolIdFormat.GetId(decoratorInner),
            Decorate: SymbolIdFormat.GetId(Method(decorator, "Decorate")),
            GreeterField: SymbolIdFormat.GetId(greeterField));
    }

    // ---------------------------------------------------------------------------
    // Assertion helpers
    // ---------------------------------------------------------------------------

    private NodeRow GetNode(string id)
    {
        var matches = _fx.Extraction.Nodes.Where(n => n.Id == id).ToList();
        Assert.True(matches.Count == 1, $"Expected exactly one node with ID '{id}', found {matches.Count}.");
        return matches[0];
    }

    private static void AssertSingleEdge(
        IReadOnlyList<EdgeRow> edgesOfType,
        string type,
        string fromId,
        string toId,
        string expectedSourceDoc)
    {
        var matches = edgesOfType.Where(e => e.FromId == fromId && e.ToId == toId).ToList();
        Assert.True(
            matches.Count == 1,
            $"Expected exactly one {type} edge"
            + Environment.NewLine + $"  from: {fromId}"
            + Environment.NewLine + $"  to:   {toId}"
            + Environment.NewLine + $"but found {matches.Count}. All {type} edges:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, edgesOfType.Select(e => $"  {e.FromId} -> {e.ToId} ({e.SourceDoc})")));

        // Edge.SourceDoc = the source-side declaration's document (for CALLS, the
        // document containing the invocation).
        Assert.Equal(expectedSourceDoc, matches[0].SourceDoc);
    }

    private static void AssertSetEqual<T>(IReadOnlySet<T> expected, IReadOnlySet<T> actual, string what)
    {
        var missing = expected.Except(actual).ToList();
        var unexpected = actual.Except(expected).ToList();
        Assert.True(
            missing.Count == 0 && unexpected.Count == 0,
            $"{what} mismatch."
            + (missing.Count > 0
                ? Environment.NewLine + $"Missing ({missing.Count}):" + Environment.NewLine + "  "
                    + string.Join(Environment.NewLine + "  ", missing)
                : string.Empty)
            + (unexpected.Count > 0
                ? Environment.NewLine + $"Unexpected ({unexpected.Count}):" + Environment.NewLine + "  "
                    + string.Join(Environment.NewLine + "  ", unexpected)
                : string.Empty));
    }
}
