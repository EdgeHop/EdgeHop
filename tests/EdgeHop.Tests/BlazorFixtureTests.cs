using EdgeHop.Core;
using EdgeHop.Roslyn;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// Phase 4 checkpoint — the regression anchor for the Razor component pass. Pointed at
/// <c>fixtures/BlazorFixture/BlazorFixture.sln</c> (4 components: Home, Multi, Child,
/// TypedList), the extractor must:
/// <list type="bullet">
/// <item><description>mark exactly the component types <c>IsComponent</c>,</description></item>
/// <item><description>capture <c>@page</c> routes in declaration order,</description></item>
/// <item><description>remap every generated-tree symbol's SourceDoc to the authored
/// <c>.razor</c> document (no <c>obj/</c> or <c>_razor.g.cs</c> path anywhere),</description></item>
/// <item><description>emit RENDERS component→child edges for source-declared components
/// only (framework components like <c>PageTitle</c> get none),</description></item>
/// <item><description>emit handler-binding CALLS (<c>BuildRenderTree → HandleClick</c>)
/// for both the HTML-element and component-parameter binding shapes, deduped,</description></item>
/// <item><description>and never emit the <c>__Blazor</c>/TypeInference plumbing.</description></item>
/// </list>
/// Exact totals live in <c>fixtures/BlazorFixture/EXPECTED-GRAPH.md</c>. Runs
/// MSBuildWorkspace in-process via <see cref="BlazorMsBuildFixture"/>; no Neo4j involved.
/// </summary>
[Collection(BlazorMsBuildTestCollection.Name)]
public sealed class BlazorFixtureTests
{
    private const string HomeBuildRenderTreeName = "void Home.BuildRenderTree(RenderTreeBuilder __builder)";

    private readonly BlazorMsBuildFixture _fx;

    public BlazorFixtureTests(BlazorMsBuildFixture fx) => _fx = fx;

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

        Assert.Single(_fx.Solution.Projects, p => p.Name == "BlazorFixture");
    }

    // ---------------------------------------------------------------------------
    // 2. Exact totals (EXPECTED-GRAPH.md)
    // ---------------------------------------------------------------------------

    [Fact]
    public void Graph_totals_match_expected_graph_contract()
    {
        Assert.Equal(19, _fx.Extraction.Nodes.Count);
        Assert.Equal(22, _fx.Extraction.Edges.Count);

        var byType = _fx.Extraction.Edges
            .GroupBy(e => e.Type)
            .ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(
            new Dictionary<string, int>
            {
                [EdgeTypes.Contains] = 18,
                [EdgeTypes.Calls] = 2,
                [EdgeTypes.Renders] = 2,
            },
            byType);
    }

    [Fact]
    public void Imports_class_is_kept_but_is_not_a_component()
    {
        // _Imports.razor compiles to a non-component class; it maps to a real authored
        // document, so it stays in the graph — but must never be marked as a component.
        var imports = GetNamedType("_Imports");
        Assert.False(imports.IsComponent);
        Assert.Equal("_Imports.razor", imports.SourceDoc);
    }

    // ---------------------------------------------------------------------------
    // 3. Component detection + routes
    // ---------------------------------------------------------------------------

    [Fact]
    public void Exactly_the_four_component_types_are_marked_iscomponent()
    {
        var componentNames = _fx.Extraction.Nodes
            .Where(n => n.IsComponent)
            .Select(n => (n.Kind, n.Name))
            .ToHashSet();

        var expected = new HashSet<(string, string)>
        {
            (SymbolKinds.NamedType, "Home"),
            (SymbolKinds.NamedType, "Multi"),
            (SymbolKinds.NamedType, "Child"),
            (SymbolKinds.NamedType, "TypedList<T>"),
        };

        Assert.Equal(expected, componentNames);
    }

    [Fact]
    public void Routes_capture_page_directives_in_declaration_order()
    {
        Assert.Equal(new[] { "/" }, GetNamedType("Home").Routes);
        Assert.Equal(new[] { "/multi", "/multi/{Id:int}" }, GetNamedType("Multi").Routes);
        Assert.Null(GetNamedType("Child").Routes);
        Assert.Null(GetNamedType("TypedList<T>").Routes);
    }

    [Fact]
    public void Non_component_nodes_have_no_routes_and_are_not_components()
    {
        foreach (var node in _fx.Extraction.Nodes.Where(n => !n.IsComponent))
        {
            Assert.Null(node.Routes);
        }
    }

    // ---------------------------------------------------------------------------
    // 3. SourceDoc remap
    // ---------------------------------------------------------------------------

    [Fact]
    public void No_node_points_at_a_generated_or_obj_document()
    {
        foreach (var node in _fx.Extraction.Nodes)
        {
            if (node.SourceDoc is null)
            {
                continue;
            }

            Assert.DoesNotContain("obj/", node.SourceDoc, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("_razor.g.cs", node.SourceDoc, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Component_and_code_block_symbols_map_to_the_authored_razor_document()
    {
        Assert.Equal("Pages/Home.razor", GetNamedType("Home").SourceDoc);
        Assert.Equal("Pages/Multi.razor", GetNamedType("Multi").SourceDoc);
        Assert.Equal("Shared/TypedList.razor", GetNamedType("TypedList<T>").SourceDoc);

        // @code members and the generated BuildRenderTree both live in the .razor doc.
        Assert.Equal("Pages/Home.razor", GetMethod("Task Home.HandleClick()").SourceDoc);
        Assert.Equal("Pages/Home.razor", GetMethod(HomeBuildRenderTreeName).SourceDoc);
    }

    [Fact]
    public void Manual_partial_component_picks_razor_doc_for_the_type_and_own_doc_for_members()
    {
        // "Shared/Child.razor" < "Shared/Child.razor.cs" ordinal → the type node lands
        // on the .razor document; members keep their declaring document.
        Assert.Equal("Shared/Child.razor", GetNamedType("Child").SourceDoc);
        Assert.Equal("Shared/Child.razor.cs", GetMethod("string Child.Label()").SourceDoc);
    }

    // ---------------------------------------------------------------------------
    // 4. RENDERS edges
    // ---------------------------------------------------------------------------

    [Fact]
    public void Renders_edges_are_exactly_home_to_child_and_home_to_typedlist()
    {
        var renders = _fx.Extraction.Edges
            .Where(e => e.Type == EdgeTypes.Renders)
            .Select(e => (From: NodeName(e.FromId), To: NodeName(e.ToId)))
            .ToHashSet();

        var expected = new HashSet<(string, string)>
        {
            ("Home", "Child"),
            ("Home", "TypedList<T>"), // inferred-generic tag → TypeInference helper → definition
        };

        Assert.Equal(expected, renders);
    }

    [Fact]
    public void Renders_edges_carry_the_razor_document_and_never_target_framework_components()
    {
        var renders = _fx.Extraction.Edges.Where(e => e.Type == EdgeTypes.Renders).ToList();
        Assert.All(renders, e => Assert.Equal("Pages/Home.razor", e.SourceDoc));

        // <PageTitle> is rendered by Home but is a framework component: it must not be
        // a node, so no edge can point at it (closure guarantee backstop).
        Assert.DoesNotContain(_fx.Extraction.Nodes, n => n.Name == "PageTitle");
    }

    // ---------------------------------------------------------------------------
    // 5. Handler-binding CALLS
    // ---------------------------------------------------------------------------

    [Fact]
    public void Handler_bindings_produce_one_calls_edge_from_buildrendertree_to_the_handler()
    {
        var buildRenderTreeId = GetMethod(HomeBuildRenderTreeName).Id;
        var handleClickId = GetMethod("Task Home.HandleClick()").Id;

        // Both binding shapes — <button @onclick="HandleClick"> and
        // <Child OnPing="HandleClick"> — collapse to ONE deduped edge.
        var edges = _fx.Extraction.Edges
            .Where(e => e.Type == EdgeTypes.Calls
                && e.FromId == buildRenderTreeId
                && e.ToId == handleClickId)
            .ToList();

        var single = Assert.Single(edges);
        Assert.Equal("Pages/Home.razor", single.SourceDoc);
    }

    [Fact]
    public void Home_buildrendertree_calls_only_the_handler()
    {
        // Outgoing CALLS from Home.BuildRenderTree: OpenComponent/OpenElement/etc. are
        // metadata (skipped), TypeInference.Create* is suppressed plumbing (edge dropped
        // by node closure), and the bind-free markup adds nothing — leaving exactly the
        // handler binding.
        var buildRenderTreeId = GetMethod(HomeBuildRenderTreeName).Id;
        var targets = _fx.Extraction.Edges
            .Where(e => e.Type == EdgeTypes.Calls && e.FromId == buildRenderTreeId)
            .Select(e => NodeName(e.ToId))
            .ToHashSet();

        Assert.Equal(new HashSet<string> { "Task Home.HandleClick()" }, targets);
    }

    [Fact]
    public void Markup_expression_invocations_still_flow_through_the_ordinary_calls_pass()
    {
        // <p>@Label()</p> in Child.razor is a real invocation in the generated tree:
        // the pre-existing CALLS pass must attribute it to Child.BuildRenderTree with
        // the remapped doc — proving both passes compose on the same trees.
        var edge = Assert.Single(_fx.Extraction.Edges, e =>
            e.Type == EdgeTypes.Calls
            && NodeName(e.FromId) == "void Child.BuildRenderTree(RenderTreeBuilder __builder)"
            && NodeName(e.ToId) == "string Child.Label()");

        Assert.Equal("Shared/Child.razor", edge.SourceDoc);
    }

    // ---------------------------------------------------------------------------
    // 6. Plumbing suppression + structural invariants
    // ---------------------------------------------------------------------------

    [Fact]
    public void No_blazor_typeinference_plumbing_is_emitted()
    {
        Assert.DoesNotContain(_fx.Extraction.Nodes,
            n => n.Id.Contains("__Blazor", StringComparison.Ordinal)
                 || n.Id.Contains("TypeInference", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_edge_connects_two_emitted_nodes()
    {
        var ids = _fx.Extraction.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var edge in _fx.Extraction.Edges)
        {
            Assert.Contains(edge.FromId, ids);
            Assert.Contains(edge.ToId, ids);
        }
    }

    [Fact]
    public async Task Extraction_is_deterministic()
    {
        var second = await SymbolGraphExtractor.ExtractAsync(_fx.Solution, BlazorMsBuildFixture.Branch);

        Assert.Equal(
            _fx.Extraction.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal),
            second.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal));
        Assert.Equal(
            _fx.Extraction.Edges.Select(e => (e.Type, e.FromId, e.ToId)).ToHashSet(),
            second.Edges.Select(e => (e.Type, e.FromId, e.ToId)).ToHashSet());
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private NodeRow GetNamedType(string name) =>
        Assert.Single(_fx.Extraction.Nodes, n => n.Kind == SymbolKinds.NamedType && n.Name == name);

    private NodeRow GetMethod(string name) =>
        Assert.Single(_fx.Extraction.Nodes, n => n.Kind == SymbolKinds.Method && n.Name == name);

    private string NodeName(string id) =>
        _fx.Extraction.Nodes.Single(n => n.Id == id).Name;
}
