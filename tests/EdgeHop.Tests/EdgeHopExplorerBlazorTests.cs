using EdgeHop.Core;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// The comprehensive regression anchor: the <c>EdgeHopExplorer.BlazorServer</c> sample exercises
/// <b>every</b> node kind and <b>every</b> edge type from one Blazor Server tier, driven end-to-end
/// through the real Roslyn + oxc + host pipeline (see <see cref="SampleBlazorMsBuildFixture"/>) in
/// the default precise interop mode. Exact expectations live in
/// <c>tests/samples/EdgeHopExplorer.BlazorServer/EXPECTED-GRAPH.md</c>. Where a count is dominated
/// by Razor codegen (CONTAINS, NamedType) it is noted there as SDK-sensitive; the interop, HTTP,
/// hierarchy, and component edges are the semantic contract.
/// </summary>
[Collection(SampleBlazorMsBuildTestCollection.Name)]
public sealed class EdgeHopExplorerBlazorTests
{
    private readonly SampleBlazorMsBuildFixture _fx;

    public EdgeHopExplorerBlazorTests(SampleBlazorMsBuildFixture fx) => _fx = fx;

    // ------------------------------------------------------------- totals / census ----

    [Fact]
    public void Graph_totals_match_the_pinned_contract()
    {
        Assert.Equal(122, _fx.Graph.Nodes.Count);
        Assert.Equal(179, _fx.Graph.Edges.Count);

        var byKind = _fx.Graph.Nodes.GroupBy(n => n.Kind).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(
            new Dictionary<string, int>
            {
                [SymbolKinds.Method] = 48,
                [SymbolKinds.NamedType] = 28,
                [SymbolKinds.Property] = 21,
                [SymbolKinds.Field] = 15,
                [SymbolKinds.Namespace] = 9,
                [SymbolKinds.Event] = 1,
            },
            byKind);

        var byType = _fx.Graph.Edges.GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(
            new Dictionary<string, int>
            {
                [EdgeTypes.Contains] = 118,
                [EdgeTypes.References] = 25,
                [EdgeTypes.Calls] = 16,
                [EdgeTypes.Renders] = 4,
                [EdgeTypes.HttpCalls] = 3,
                [EdgeTypes.Implements] = 3,
                [EdgeTypes.Inherits] = 3,
                [EdgeTypes.Overrides] = 3,
                [EdgeTypes.JsCalls] = 2,
                [EdgeTypes.JsInvokes] = 2,
            },
            byType);
    }

    [Fact]
    public void All_six_node_kinds_are_exercised_including_event()
    {
        // The Event kind is the one no older fixture covered — FeatureCatalog.Registered supplies it.
        foreach (var kind in new[]
                 {
                     SymbolKinds.Namespace, SymbolKinds.NamedType, SymbolKinds.Method,
                     SymbolKinds.Property, SymbolKinds.Field, SymbolKinds.Event,
                 })
        {
            Assert.Contains(_fx.Graph.Nodes, n => n.Kind == kind);
        }

        var evt = Assert.Single(_fx.Graph.Nodes, n => n.Kind == SymbolKinds.Event);
        Assert.Contains("Registered", evt.Name, StringComparison.Ordinal);
        Assert.Equal("Domain/FeatureCatalog.cs", evt.SourceDoc);
    }

    [Fact]
    public void All_ten_edge_types_are_present()
    {
        var present = _fx.Graph.Edges.Select(e => e.Type).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            new HashSet<string>
            {
                EdgeTypes.Contains, EdgeTypes.Calls, EdgeTypes.Implements, EdgeTypes.Inherits,
                EdgeTypes.References, EdgeTypes.Overrides, EdgeTypes.Renders, EdgeTypes.HttpCalls,
                EdgeTypes.JsCalls, EdgeTypes.JsInvokes,
            },
            present);
    }

    // ------------------------------------------------------------- type hierarchy -----

    [Fact]
    public void Implements_edges_capture_the_direct_interface_lists()
    {
        var implements = EdgeSet(EdgeTypes.Implements);

        // Direct interfaces only: FeatureBase->IFeature, NodeKindsFeature->ISearchable (its second,
        // directly-listed interface), FeatureCatalog->IFeatureCatalog. The IFeature reached only
        // through FeatureBase does NOT produce a second edge from the concrete features.
        Assert.Equal(
            new HashSet<(string, string)>
            {
                ("FeatureBase", "IFeature"),
                ("NodeKindsFeature", "ISearchable"),
                ("FeatureCatalog", "IFeatureCatalog"),
            },
            implements);
    }

    [Fact]
    public void Inherits_edges_all_point_at_the_abstract_base()
    {
        var inherits = EdgeSet(EdgeTypes.Inherits);
        Assert.Equal(
            new HashSet<(string, string)>
            {
                ("NodeKindsFeature", "FeatureBase"),
                ("EdgeTypesFeature", "FeatureBase"),
                ("QueryToolsFeature", "FeatureBase"),
            },
            inherits);
    }

    [Fact]
    public void Overrides_edges_all_target_the_virtual_describe()
    {
        var overrides = _fx.Graph.Edges.Where(e => e.Type == EdgeTypes.Overrides).ToList();
        Assert.Equal(3, overrides.Count);
        Assert.All(overrides, e => Assert.Equal("string FeatureBase.Describe()", NodeName(e.ToId)));
    }

    // ------------------------------------------------------------- components ----------

    [Fact]
    public void Exactly_the_seven_source_components_are_flagged()
    {
        var components = _fx.Graph.Nodes
            .Where(n => n.IsComponent)
            .Select(n => n.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "App", "Routes", "Home", "FeatureCard", "TypedList<T>", "About", "Features",
            },
            components);
    }

    [Fact]
    public void Routes_capture_page_directives_in_declaration_order()
    {
        Assert.Equal(new[] { "/" }, GetNamedType("Home").Routes);
        Assert.Equal(new[] { "/features" }, GetNamedType("Features").Routes);
        Assert.Equal(new[] { "/about", "/info" }, GetNamedType("About").Routes); // multi-route page
        Assert.Null(GetNamedType("FeatureCard").Routes);                          // routeless child
    }

    [Fact]
    public void Renders_edges_are_the_expected_component_graph()
    {
        var renders = EdgeSet(EdgeTypes.Renders);
        Assert.Equal(
            new HashSet<(string, string)>
            {
                ("App", "Routes"),
                ("Home", "FeatureCard"),
                ("Home", "TypedList<T>"),      // inferred-generic tag via the TypeInference helper
                ("Features", "TypedList<T>"),
            },
            renders);
    }

    // ------------------------------------------------------------- HTTP_CALLS ----------

    [Fact]
    public void Http_calls_link_every_client_method_to_the_one_endpoint_anchor()
    {
        var httpCalls = _fx.Graph.Edges.Where(e => e.Type == EdgeTypes.HttpCalls).ToList();
        Assert.Equal(3, httpCalls.Count);

        // All three verb-named HttpClient calls resolve to the single registration method.
        var endpoint = GetMethod(n => n.Name.Contains("MapFeatureEndpoints", StringComparison.Ordinal));
        Assert.All(httpCalls, e => Assert.Equal(endpoint.Id, e.ToId));

        var callers = httpCalls.Select(e => NodeName(e.FromId)).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(callers, n => n.Contains("GetAllAsync", StringComparison.Ordinal));
        Assert.Contains(callers, n => n.Contains("GetAsync", StringComparison.Ordinal));
        Assert.Contains(callers, n => n.Contains("SearchAsync", StringComparison.Ordinal));

        // The endpoint method carries its verb-prefixed routes, in declaration order.
        Assert.Equal(
            new[] { "GET /api/features/all", "GET /api/features/{name}", "POST /api/features/search" },
            endpoint.Routes);
    }

    // ------------------------------------------------------------- JS extraction -------

    [Fact]
    public void Oxc_extracts_the_standalone_module_and_the_inline_script()
    {
        var jsNodes = _fx.Graph.Nodes.Where(IsJs).ToList();
        Assert.NotEmpty(jsNodes);
        Assert.All(jsNodes, n => Assert.Contains("js|", n.Id, StringComparison.Ordinal));

        // The standalone module: explorer.js with its function/class/field members.
        Assert.Single(jsNodes, n => n.Kind == SymbolKinds.Namespace && n.Name == "explorer.js");
        Assert.Single(jsNodes, n => n.Kind == SymbolKinds.Method && n.Name == "highlight");
        Assert.Single(jsNodes, n => n.Kind == SymbolKinds.NamedType && n.Name == "Explorer");

        // The inline <script> in the host document is discovered as its OWN module (App.razor#N).
        Assert.Contains(jsNodes, n =>
            n.Kind == SymbolKinds.Namespace
            && n.SourceDoc is { } doc && doc.Contains("App.razor", StringComparison.Ordinal));
    }

    [Fact]
    public void Js_internal_calls_edge_is_resolved_within_explorer_js()
    {
        // highlight() calls the module-private decorate(): a binding-resolved JS-internal CALLS edge.
        var highlight = JsMethod("highlight");
        var decorate = JsMethod("decorate");
        Assert.Contains(_fx.Graph.Edges, e =>
            e.Type == EdgeTypes.Calls && e.FromId == highlight.Id && e.ToId == decorate.Id);
    }

    // ------------------------------------------------------------- JS_CALLS (C#->JS) ---

    [Fact]
    public void Js_calls_are_the_two_precise_module_correlated_matches()
    {
        var jsCalls = _fx.Graph.Edges.Where(e => e.Type == EdgeTypes.JsCalls).ToList();
        Assert.Equal(2, jsCalls.Count);

        // Both originate in Home.Refresh() and target exports of the imported explorer.js module.
        var refresh = GetMethod(n => n.Name == "Task Home.Refresh()");
        Assert.All(jsCalls, e => Assert.Equal(refresh.Id, e.FromId));
        Assert.Equal(
            new HashSet<string> { "highlight", "wireInterop" },
            jsCalls.Select(e => NodeName(e.ToId)).ToHashSet(StringComparer.Ordinal));

        // Edge carries the C# calling document (the .razor), not the JS file.
        Assert.All(jsCalls, e => Assert.Equal("Components/Pages/Home.razor", e.SourceDoc));
    }

    // ------------------------------------------------------------- JS_INVOKES (JS->C#) -

    [Fact]
    public void Js_invokes_are_the_two_callbacks_into_the_component()
    {
        var invokes = _fx.Graph.Edges.Where(e => e.Type == EdgeTypes.JsInvokes).ToList();
        Assert.Equal(2, invokes.Count);

        // Both originate in explorer.js#wireInterop and target the component's [JSInvokable] methods:
        // the static Ping (assembly + identifier) and the instance OnJsEvent (unique identifier).
        var wire = JsMethod("wireInterop");
        Assert.All(invokes, e => Assert.Equal(wire.Id, e.FromId));
        Assert.All(invokes, e => Assert.DoesNotContain("js|", e.ToId, StringComparison.Ordinal));

        var targets = invokes.Select(e => NodeName(e.ToId)).ToList();
        Assert.Contains(targets, n => n.Contains("Ping", StringComparison.Ordinal));
        Assert.Contains(targets, n => n.Contains("OnJsEvent", StringComparison.Ordinal));

        // Edge carries the JS calling document, not the C# file.
        Assert.All(invokes, e => Assert.Equal("wwwroot/js/explorer.js", e.SourceDoc));
    }

    // ------------------------------------------------------------- reflection gap ------

    [Fact]
    public void Reflection_based_loader_contributes_no_calls_edges()
    {
        // ReflectionFeatureLoader.DiscoverAll dispatches entirely through reflection APIs
        // (Assembly.GetTypes / Activator.CreateInstance — all framework methods), so it must have
        // ZERO outgoing CALLS edges. EdgeHop records compile-time structure, not reflective
        // dispatch: this is the sample's demonstration of what a code graph cannot see.
        var discover = Assert.Single(_fx.Graph.Nodes, n =>
            n.Kind == SymbolKinds.Method && n.Name.Contains("DiscoverAll", StringComparison.Ordinal));
        Assert.DoesNotContain(_fx.Graph.Edges, e => e.Type == EdgeTypes.Calls && e.FromId == discover.Id);
    }

    // ------------------------------------------------------------- invariants ----------

    [Fact]
    public void Every_edge_connects_two_emitted_nodes()
    {
        var ids = _fx.Graph.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var edge in _fx.Graph.Edges)
        {
            Assert.Contains(edge.FromId, ids);
            Assert.Contains(edge.ToId, ids);
        }
    }

    [Fact]
    public void No_node_points_at_a_generated_or_obj_document()
    {
        foreach (var node in _fx.Graph.Nodes.Where(n => n.SourceDoc is not null))
        {
            Assert.DoesNotContain("obj/", node.SourceDoc!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("_razor.g.cs", node.SourceDoc!, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ------------------------------------------------------------- helpers -------------

    private HashSet<(string From, string To)> EdgeSet(string type) =>
        _fx.Graph.Edges
            .Where(e => e.Type == type)
            .Select(e => (NodeName(e.FromId), NodeName(e.ToId)))
            .ToHashSet();

    private static bool IsJs(NodeRow n) => n.Id.Contains("js|", StringComparison.Ordinal);

    private NodeRow JsMethod(string name) =>
        Assert.Single(_fx.Graph.Nodes, n => n.Kind == SymbolKinds.Method && n.Name == name && IsJs(n));

    private NodeRow GetNamedType(string name) =>
        Assert.Single(_fx.Graph.Nodes, n => n.Kind == SymbolKinds.NamedType && n.Name == name);

    private NodeRow GetMethod(Func<NodeRow, bool> predicate) =>
        Assert.Single(_fx.Graph.Nodes, n => n.Kind == SymbolKinds.Method && predicate(n));

    private string NodeName(string id) => _fx.Graph.Nodes.Single(n => n.Id == id).Name;
}
