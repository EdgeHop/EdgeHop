using EdgeHop.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EdgeHop.Roslyn;

/// <summary>
/// Walks every C# project of a solution and produces the phase-2 code graph
/// (README "EdgeHop.Roslyn — extractor behavior"). Derives all edges directly from each
/// declaration's semantic model; never uses solution-wide
/// <c>SymbolFinder.FindReferencesAsync</c> (explicitly out of scope for this handoff).
/// </summary>
/// <remarks>
/// Extraction rules (README is the arbiter; the TinyFixture EXPECTED-GRAPH.md encodes them):
/// <list type="bullet">
/// <item><description>
/// <b>Nodes only for authored, non-implicit symbols</b> (see
/// <see cref="ExtractionContext.IsAuthoredSymbol"/>): declared in source, not
/// compiler-generated, and not declared solely in SDK-injected build artifacts under
/// <c>obj</c>/<c>bin</c>. Kinds emitted:
/// Namespace (global namespace skipped), NamedType, Method, Property, Field, Event.
/// Property/event accessors and compiler-generated members (including implicit default
/// constructors and backing fields) are never emitted. No nodes are ever created for
/// metadata/framework symbols. Explicit constructors — including primary constructors
/// (records and C# 12 classes/structs), which Roslyn reports as source-declared and
/// non-implicit — do get Method nodes; their CALLS edges come only from the code that
/// executes as part of the constructor declaration itself (see
/// <see cref="EnumerateDeclaredInvocations"/>).
/// </description></item>
/// <item><description>
/// <b>Identity:</b> <c>Node.Id</c> comes from <see cref="SymbolIdFormat.GetId"/>, which
/// normalizes via <see cref="ISymbol.OriginalDefinition"/>; <c>Node.Kind</c> equals the
/// ID's <c>{Kind}:</c> prefix by construction. Every edge endpoint is normalized via
/// <c>OriginalDefinition</c> before its ID is computed.
/// </description></item>
/// <item><description>
/// <b>Edges to metadata/framework targets are skipped.</b> Each candidate target passes
/// the source-declared test up front, and the final result additionally drops any edge
/// whose endpoint is not an emitted node (covering e.g. local functions and symbols from
/// projects that failed to load).
/// </description></item>
/// <item><description>
/// <b>SourceDoc convention:</b> document paths are relative to the solution directory
/// with backslashes normalized to <c>/</c>. Multi-declaration symbols (partial types)
/// use the alphabetically-first (ordinal) declaring document. Namespace nodes have
/// <c>SourceDoc = null</c>, and CONTAINS edges emitted from a namespace carry
/// <c>SourceDoc = null</c> for the same reason (no single declaring document).
/// Symbols declared in Razor-generated trees (<c>*_razor.g.cs</c>) are remapped to the
/// authored <c>.razor</c> document (see <see cref="RazorGeneratedDocs"/>).
/// </description></item>
/// <item><description>
/// <b>Razor components:</b> after the symbol walk, <see cref="RazorComponentPass"/>
/// derives RENDERS and handler-binding CALLS edges from the generated component code;
/// component NamedTypes carry <c>IsComponent = true</c> and their <c>@page</c> route
/// templates. The compiler-plumbing <c>__Blazor</c> namespace (TypeInference helpers)
/// is never emitted.
/// </description></item>
/// </list>
/// </remarks>
public static class SymbolGraphExtractor
{
    /// <summary>
    /// Extracts all nodes and edges from <paramref name="solution"/>, stamping every row
    /// with <paramref name="branch"/>.
    /// </summary>
    /// <param name="solution">The loaded solution (see <see cref="WorkspaceLoader"/>).</param>
    /// <param name="branch">Branch stamped into every row (<c>"main"</c> in this handoff).</param>
    /// <param name="log">Optional progress/skip-reason sink.</param>
    public static async Task<ExtractionResult> ExtractAsync(
        Solution solution,
        string branch,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        var ctx = new ExtractionContext(branch, GetSolutionDirectory(solution));
        var httpPass = new HttpEdgePass();
        var jsInteropPass = new JsInteropPass();

        foreach (var project in solution.Projects)
        {
            if (project.Language != LanguageNames.CSharp)
            {
                log?.Invoke($"Skipping non-C# project '{project.Name}' ({project.Language}).");
                continue;
            }

            var compilation = await project.GetCompilationAsync().ConfigureAwait(false);
            if (compilation is null)
            {
                log?.Invoke($"Project '{project.Name}' produced no compilation; skipping.");
                continue;
            }

            log?.Invoke($"Extracting project '{project.Name}'…");

            // Walk the SOURCE assembly's global namespace (not the compilation's merged
            // one), so each project contributes only its own declared symbols. Symbols
            // shared across TFMs / referenced projects dedupe on their stable ID.
            WalkNamespace(compilation.Assembly.GlobalNamespace, compilation, ctx);

            // Razor phase: RENDERS + handler-binding CALLS edges from the generated
            // component code. Self-no-ops for projects without Razor trees.
            RazorComponentPass.Run(compilation, ctx, log);

            // HTTP phase, collect half: endpoint registrations and HttpClient call
            // sites. Matching is deferred to after the loop — callers and endpoints
            // live in different projects (Web→ApiService).
            httpPass.Collect(compilation, ctx, log);

            // JS-interop phase, C# half: collect IJSRuntime/IJSObjectReference call
            // sites (nodes for this project are already emitted, so callers bind). The
            // JS half comes from the oxc extractor; the host derives JS_CALLS after both.
            jsInteropPass.Collect(compilation, ctx, log);
        }

        // HTTP phase, emit half: cross-project verb+template matching → HTTP_CALLS
        // edges, plus routes stamped onto endpoint registration-method nodes.
        httpPass.Emit(ctx, log);

        // Final arbiter: an edge may only connect two emitted nodes. Candidates already
        // passed the source-declared test on their target, but this also drops targets
        // that are never emitted as nodes (local functions, accessors, symbols from
        // projects whose compilation was unavailable).
        var nodes = (IReadOnlyList<NodeRow>)ctx.Nodes.Values.ToList();
        var edges = (IReadOnlyList<EdgeRow>)ctx.Edges
            .Where(e => ctx.Nodes.ContainsKey(e.FromId) && ctx.Nodes.ContainsKey(e.ToId))
            .ToList();

        // JS-interop C# sides: only rows bound to an emitted node travel on (a call inside a
        // property accessor has no method node to attribute to). The JS halves are contributed by
        // the oxc extractor and matched in the host.
        var interopSites = ctx.InteropSites
            .Where(s => ctx.Nodes.ContainsKey(s.CallerId))
            .ToList();
        var invokableTargets = ctx.InvokableTargets
            .Where(t => ctx.Nodes.ContainsKey(t.MethodId))
            .ToList();
        var interop = interopSites.Count > 0 || invokableTargets.Count > 0
            ? new InteropSurface(interopSites, [], [], invokableTargets)
            : null;

        log?.Invoke($"Extraction complete: {nodes.Count} nodes, {edges.Count} edges.");
        return new ExtractionResult(nodes, edges, interop);
    }

    /// <summary>
    /// Emits the namespace node (unless it is the global namespace or not source-declared),
    /// recurses into child namespaces and types, and adds namespace-side CONTAINS edges.
    /// Returns the namespace node ID, or null when no node was emitted.
    /// </summary>
    private static string? WalkNamespace(
        INamespaceSymbol ns,
        Compilation compilation,
        ExtractionContext ctx)
    {
        string? nsId = null;
        if (!ns.IsGlobalNamespace && ctx.IsAuthoredSymbol(ns))
        {
            // Namespaces span documents: SourceDoc = null per the node contract.
            nsId = ctx.AddNode(ns, sourceDoc: null);
        }

        foreach (var child in ns.GetNamespaceMembers())
        {
            // The Razor generator's TypeInference helpers live under a top-level
            // `__Blazor` namespace — compiler plumbing, never part of the graph.
            // (RazorComponentPass reads their bodies syntactically, so nothing is lost.)
            if (ns.IsGlobalNamespace && child.Name == "__Blazor")
            {
                continue;
            }

            var childId = WalkNamespace(child, compilation, ctx);
            if (nsId is not null && childId is not null)
            {
                ctx.AddEdge(nsId, childId, EdgeTypes.Contains, sourceDoc: null);
            }
        }

        foreach (var type in ns.GetTypeMembers())
        {
            var typeId = WalkType(type, compilation, ctx);
            if (nsId is not null && typeId is not null)
            {
                ctx.AddEdge(nsId, typeId, EdgeTypes.Contains, sourceDoc: null);
            }
        }

        return nsId;
    }

    /// <summary>
    /// Emits the type node plus its IMPLEMENTS / INHERITS edges, then walks its members
    /// (nested types, methods, fields, properties, events) emitting member nodes and
    /// CONTAINS / OVERRIDES / REFERENCES / CALLS edges. Returns the type node ID, or null
    /// when the type is not source-declared.
    /// </summary>
    private static string? WalkType(
        INamedTypeSymbol type,
        Compilation compilation,
        ExtractionContext ctx)
    {
        if (!ctx.IsAuthoredSymbol(type))
        {
            return null;
        }

        // Partial types: one symbol, one ID, one node; SourceDoc is the
        // alphabetically-first declaring document (ordinal). For a component with a
        // manual partial class this deterministically picks the .razor doc
        // ("X.razor" < "X.razor.cs" ordinal).
        var typeDoc = GetDeclarationDoc(type, ctx);
        var isComponent = RazorComponentPass.IsComponentType(type);
        var typeId = ctx.AddNode(type, typeDoc, isComponent, isComponent ? GetRoutes(type) : null);

        // IMPLEMENTS: DIRECT interface list only (type.Interfaces) — transitively
        // implemented interfaces get no edge. Metadata interfaces are skipped.
        foreach (var iface in type.Interfaces)
        {
            AddTypeTargetEdge(typeId, iface, EdgeTypes.Implements, typeDoc, ctx);
        }

        // INHERITS: direct base type; metadata bases (System.Object, System.Enum, …) are
        // skipped by the source-declared test inside AddTypeTargetEdge.
        if (type.BaseType is { } baseType)
        {
            AddTypeTargetEdge(typeId, baseType, EdgeTypes.Inherits, typeDoc, ctx);
        }

        foreach (var member in type.GetMembers())
        {
            switch (member)
            {
                case INamedTypeSymbol nested:
                {
                    var nestedId = WalkType(nested, compilation, ctx);
                    if (nestedId is not null)
                    {
                        ctx.AddEdge(typeId, nestedId, EdgeTypes.Contains, typeDoc);
                    }

                    break;
                }

                case IMethodSymbol method when IsEmittableMethod(method, ctx):
                {
                    var methodDoc = GetDeclarationDoc(method, ctx);
                    var methodId = ctx.AddNode(method, methodDoc);
                    ctx.AddEdge(typeId, methodId, EdgeTypes.Contains, typeDoc);

                    // OVERRIDES: override → overridden, from IMethodSymbol.OverriddenMethod
                    // only. Interface implementations have OverriddenMethod == null, so
                    // they correctly produce no edge.
                    if (method.OverriddenMethod is { } overridden)
                    {
                        var target = (IMethodSymbol)overridden.OriginalDefinition;
                        if (ctx.IsAuthoredSymbol(target))
                        {
                            ctx.AddEdge(methodId, SymbolIdFormat.GetId(target), EdgeTypes.Overrides, methodDoc);
                        }
                    }

                    // REFERENCES: parameter types + return type.
                    AddReferenceEdges(methodId, EnumerateSignatureTypes(method), methodDoc, ctx);

                    // CALLS: invocations inside this method's declared bodies.
                    AddCallEdges(methodId, method, compilation, ctx);
                    break;
                }

                case IFieldSymbol field when ctx.IsAuthoredSymbol(field):
                {
                    var fieldDoc = GetDeclarationDoc(field, ctx);
                    var fieldId = ctx.AddNode(field, fieldDoc);
                    ctx.AddEdge(typeId, fieldId, EdgeTypes.Contains, typeDoc);

                    // REFERENCES: the field type.
                    AddReferenceEdges(fieldId, new[] { field.Type }, fieldDoc, ctx);
                    break;
                }

                case IPropertySymbol property when ctx.IsAuthoredSymbol(property):
                {
                    var propertyDoc = GetDeclarationDoc(property, ctx);
                    var propertyId = ctx.AddNode(property, propertyDoc);
                    ctx.AddEdge(typeId, propertyId, EdgeTypes.Contains, typeDoc);

                    // REFERENCES: the property type.
                    AddReferenceEdges(propertyId, new[] { property.Type }, propertyDoc, ctx);
                    break;
                }

                case IEventSymbol @event when ctx.IsAuthoredSymbol(@event):
                {
                    var eventDoc = GetDeclarationDoc(@event, ctx);
                    var eventId = ctx.AddNode(@event, eventDoc);
                    ctx.AddEdge(typeId, eventId, EdgeTypes.Contains, typeDoc);

                    // REFERENCES: the event (delegate) type.
                    AddReferenceEdges(eventId, new[] { @event.Type }, eventDoc, ctx);
                    break;
                }
            }
        }

        return typeId;
    }

    /// <summary>
    /// Route templates from the type's <c>[Route]</c> attributes (the compiled form of
    /// <c>@page</c> directives), in declaration order; null when there are none.
    /// </summary>
    private static IReadOnlyList<string>? GetRoutes(INamedTypeSymbol type)
    {
        List<string>? routes = null;
        foreach (var attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass?.OriginalDefinition.ToDisplayString()
                    == "Microsoft.AspNetCore.Components.RouteAttribute"
                && attribute.ConstructorArguments.Length > 0
                && attribute.ConstructorArguments[0].Value is string template)
            {
                (routes ??= new List<string>()).Add(template);
            }
        }

        return routes;
    }

    /// <summary>
    /// Method nodes are emitted for authored methods (see
    /// <see cref="ExtractionContext.IsAuthoredSymbol"/>) that are not property/event
    /// accessors (<see cref="MethodKind.PropertyGet"/>, <see cref="MethodKind.PropertySet"/>,
    /// <see cref="MethodKind.EventAdd"/>, <see cref="MethodKind.EventRemove"/>,
    /// <see cref="MethodKind.EventRaise"/>). Explicit (source-written) constructors qualify;
    /// implicit default constructors are already excluded by the authored test.
    /// </summary>
    private static bool IsEmittableMethod(IMethodSymbol method, ExtractionContext ctx) =>
        ctx.IsAuthoredSymbol(method) &&
        method.MethodKind is not (MethodKind.PropertyGet or MethodKind.PropertySet
            or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise);

    /// <summary>Parameter types plus the return type (when not void) of a method.</summary>
    private static IEnumerable<ITypeSymbol> EnumerateSignatureTypes(IMethodSymbol method)
    {
        foreach (var parameter in method.Parameters)
        {
            yield return parameter.Type;
        }

        if (!method.ReturnsVoid)
        {
            yield return method.ReturnType;
        }
    }

    /// <summary>
    /// Emits one REFERENCES edge per distinct authored named type reachable from
    /// <paramref name="candidateTypes"/>: arrays are unwrapped to their element type,
    /// pointers to their pointee, <c>Nullable&lt;T&gt;</c> unwraps naturally (the
    /// definition is metadata, its type argument is walked), and generic type ARGUMENTS
    /// are walked recursively. Each candidate is normalized via
    /// <see cref="ISymbol.OriginalDefinition"/>; type parameters and non-authored
    /// types are skipped.
    /// </summary>
    private static void AddReferenceEdges(
        string fromId,
        IEnumerable<ITypeSymbol> candidateTypes,
        string? sourceDoc,
        ExtractionContext ctx)
    {
        var targets = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var candidate in candidateTypes)
        {
            CollectAuthoredNamedTypes(candidate, targets, ctx);
        }

        foreach (var target in targets)
        {
            ctx.AddEdge(fromId, SymbolIdFormat.GetId(target), EdgeTypes.References, sourceDoc);
        }
    }

    /// <summary>Recursive worker for <see cref="AddReferenceEdges"/>; see its docs.</summary>
    private static void CollectAuthoredNamedTypes(
        ITypeSymbol type,
        HashSet<INamedTypeSymbol> sink,
        ExtractionContext ctx)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                CollectAuthoredNamedTypes(array.ElementType, sink, ctx);
                break;

            case IPointerTypeSymbol pointer:
                CollectAuthoredNamedTypes(pointer.PointedAtType, sink, ctx);
                break;

            case ITypeParameterSymbol:
                // Type parameters never produce REFERENCES edges.
                break;

            case INamedTypeSymbol named:
            {
                var definition = (INamedTypeSymbol)named.OriginalDefinition;
                if (ctx.IsAuthoredSymbol(definition))
                {
                    sink.Add(definition);
                }

                // Walk generic type ARGUMENTS recursively: List<Order> references Order.
                foreach (var argument in named.TypeArguments)
                {
                    CollectAuthoredNamedTypes(argument, sink, ctx);
                }

                break;
            }

            default:
                // dynamic, function pointers, error types: outside the phase 1–3 graph.
                break;
        }
    }

    /// <summary>
    /// Emits CALLS edges for every <see cref="InvocationExpressionSyntax"/> found in the
    /// method's declared bodies, resolved through the declaring document's semantic model.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>
    /// Only <c>GetSymbolInfo(...).Symbol</c> is honored — a null or non-method symbol is
    /// skipped and <c>CandidateSymbols</c> are deliberately never used (an ambiguous call
    /// is not a resolved call).
    /// </description></item>
    /// <item><description>
    /// Object creations (<c>new X()</c>) are not invocations and produce no edge.
    /// </description></item>
    /// <item><description>
    /// Reduced extension-method calls (<c>x.Ext()</c>) are un-reduced via
    /// <see cref="IMethodSymbol.ReducedFrom"/> before <c>OriginalDefinition</c>
    /// normalization, so the edge lands on the declared static method node rather than
    /// being dropped for an ID mismatch.
    /// </description></item>
    /// <item><description>
    /// For partial methods the body lives on <see cref="IMethodSymbol.PartialImplementationPart"/>;
    /// both declaration sets are walked so bodies are never missed (both parts share one
    /// stable ID, so the caller side is unaffected).
    /// </description></item>
    /// <item><description>
    /// Invocations inside lambdas/anonymous functions declared within the body are
    /// attributed to the containing method.
    /// </description></item>
    /// <item><description>
    /// <b>Primary constructors</b> (records and C# 12 classes/structs): the constructor's
    /// declaring syntax is the <em>entire</em> type declaration, so the walk is restricted
    /// via <see cref="EnumerateDeclaredInvocations"/> to the regions that actually execute
    /// as part of the primary constructor — a naive descendant walk would falsely attribute
    /// every invocation in every member body to the constructor.
    /// </description></item>
    /// <item><description>
    /// Edge <c>SourceDoc</c> is the document containing the invocation.
    /// </description></item>
    /// </list>
    /// </remarks>
    private static void AddCallEdges(
        string methodId,
        IMethodSymbol method,
        Compilation compilation,
        ExtractionContext ctx)
    {
        var parts = method.PartialImplementationPart is { } implementation
                    && !SymbolEqualityComparer.Default.Equals(implementation, method)
            ? new[] { method, implementation }
            : new[] { method };

        foreach (var part in parts)
        {
            foreach (var declaration in part.DeclaringSyntaxReferences)
            {
                var syntax = declaration.GetSyntax();
                var tree = declaration.SyntaxTree;
                var semanticModel = compilation.GetSemanticModel(tree);
                var callDoc = ctx.GetDocForTree(tree);

                foreach (var invocation in EnumerateDeclaredInvocations(syntax))
                {
                    if (semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol callee)
                    {
                        continue; // unresolved or non-method target: no edge.
                    }

                    var target = (IMethodSymbol)(callee.ReducedFrom ?? callee).OriginalDefinition;
                    if (!ctx.IsAuthoredSymbol(target))
                    {
                        continue; // metadata/framework/build-artifact target: edge skipped.
                    }

                    ctx.AddEdge(methodId, SymbolIdFormat.GetId(target), EdgeTypes.Calls, callDoc);
                }
            }
        }
    }

    /// <summary>
    /// The invocation expressions belonging to ONE declaration of a method symbol. For an
    /// ordinary method/constructor/accessor the declaring syntax is the member declaration
    /// itself and every descendant invocation belongs to it (including the
    /// <c>: base(...)</c>/<c>: this(...)</c> initializer of an explicit constructor). For a
    /// <b>primary constructor</b> — <c>record Person(string Name)</c> or C# 12
    /// <c>class Service(IDep dep)</c> — the constructor's declaring syntax is the whole
    /// <see cref="TypeDeclarationSyntax"/>, whose descendants include every member body and
    /// nested type; walking them all would fabricate CALLS edges from the constructor to
    /// every method invoked anywhere in the type. Only two syntax regions execute as part
    /// of the primary constructor's own declaration, so the walk is restricted to them:
    /// <list type="bullet">
    /// <item><description>the parameter list (default values), and</description></item>
    /// <item><description>the base-initializer argument list
    /// (<see cref="PrimaryConstructorBaseTypeSyntax"/>, e.g. <c>: Base(Helper.Make())</c>).
    /// </description></item>
    /// </list>
    /// Member (field/property) initializers are deliberately excluded for parity with
    /// ordinary constructors, whose declaring syntax never includes them either.
    /// </summary>
    private static IEnumerable<InvocationExpressionSyntax> EnumerateDeclaredInvocations(
        SyntaxNode declarationSyntax)
    {
        if (declarationSyntax is not TypeDeclarationSyntax typeDeclaration)
        {
            return declarationSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>();
        }

        var parameterInvocations = typeDeclaration.ParameterList is { } parameterList
            ? parameterList.DescendantNodes().OfType<InvocationExpressionSyntax>()
            : Enumerable.Empty<InvocationExpressionSyntax>();

        var baseInitializerArguments = typeDeclaration.BaseList?.Types
            .OfType<PrimaryConstructorBaseTypeSyntax>()
            .FirstOrDefault()?.ArgumentList;
        var baseInvocations = baseInitializerArguments is not null
            ? baseInitializerArguments.DescendantNodes().OfType<InvocationExpressionSyntax>()
            : Enumerable.Empty<InvocationExpressionSyntax>();

        return parameterInvocations.Concat(baseInvocations);
    }

    /// <summary>
    /// Adds a type-targeted edge (IMPLEMENTS / INHERITS) after normalizing the target via
    /// <see cref="ISymbol.OriginalDefinition"/>; non-authored targets are skipped.
    /// </summary>
    private static void AddTypeTargetEdge(
        string fromId,
        INamedTypeSymbol target,
        string edgeType,
        string? sourceDoc,
        ExtractionContext ctx)
    {
        var definition = (INamedTypeSymbol)target.OriginalDefinition;
        if (!ctx.IsAuthoredSymbol(definition))
        {
            return;
        }

        ctx.AddEdge(fromId, SymbolIdFormat.GetId(definition), edgeType, sourceDoc);
    }

    /// <summary>
    /// The declaring document for a symbol, per the SourceDoc convention: relative to the
    /// solution directory, forward slashes, alphabetically-first (ordinal) declaring
    /// document for multi-declaration symbols (partial types). Razor-generated trees are
    /// remapped to their authored <c>.razor</c> document by
    /// <see cref="ExtractionContext.GetDocForTree"/>.
    /// </summary>
    private static string? GetDeclarationDoc(ISymbol symbol, ExtractionContext ctx)
    {
        string? best = null;
        foreach (var location in symbol.Locations)
        {
            if (!location.IsInSource
                || location.SourceTree is not { } tree
                || !ctx.IsAuthoredTree(tree))
            {
                continue; // build-artifact declarations never win the doc pick.
            }

            var doc = ctx.GetDocForTree(tree);
            if (doc is null)
            {
                continue;
            }

            if (best is null || string.CompareOrdinal(doc, best) < 0)
            {
                best = doc;
            }
        }

        return best;
    }

    private static string? GetSolutionDirectory(Solution solution) =>
        string.IsNullOrEmpty(solution.FilePath)
            ? null
            : Path.GetDirectoryName(Path.GetFullPath(solution.FilePath));
}
