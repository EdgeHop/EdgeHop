using EdgeHop.Core;
using EdgeHop.Roslyn;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// Handoff 4 checkpoint — the regression anchor for the HTTP pass. Pointed at
/// <c>fixtures/HttpFixture/HttpFixture.sln</c> (HttpFixture.Web's <c>WidgetApiClient</c>
/// calling HttpFixture.Api's minimal-API registrations and one attribute-routed
/// controller, with NO project reference between the tiers), the extractor must:
/// <list type="bullet">
/// <item><description>emit HTTP_CALLS from each client method to the method registering
/// the verb+template-matched endpoint (registration method for minimal APIs, the action
/// itself for controllers),</description></item>
/// <item><description>match literal routes, interpolation holes against parameter
/// segments (incl. constraints like <c>{id:int}</c>), strip query strings, treat a
/// non-constant concat suffix as a query string, and compose <c>MapGroup</c>
/// prefixes,</description></item>
/// <item><description>emit NOTHING for verb mismatches or unregistered
/// routes,</description></item>
/// <item><description>and stamp verb-prefixed route templates onto registration-method
/// nodes in declaration order.</description></item>
/// </list>
/// Exact totals live in <c>fixtures/HttpFixture/EXPECTED-GRAPH.md</c>. Runs
/// MSBuildWorkspace in-process via <see cref="HttpMsBuildFixture"/>; no database.
/// </summary>
[Collection(HttpMsBuildTestCollection.Name)]
public sealed class HttpFixtureTests
{
    private const string MapWidgetEndpointsName =
        "void WidgetEndpoints.MapWidgetEndpoints(IEndpointRouteBuilder app)";

    private const string GetByIdName = "string GadgetsController.GetById(int id)";

    private readonly HttpMsBuildFixture _fx;

    public HttpFixtureTests(HttpMsBuildFixture fx) => _fx = fx;

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

        Assert.Single(_fx.Solution.Projects, p => p.Name == "HttpFixture.Api");
        Assert.Single(_fx.Solution.Projects, p => p.Name == "HttpFixture.Web");
    }

    // ---------------------------------------------------------------------------
    // 2. Exact totals (EXPECTED-GRAPH.md)
    // ---------------------------------------------------------------------------

    [Fact]
    public void Graph_totals_match_expected_graph_contract()
    {
        Assert.Equal(22, _fx.Extraction.Nodes.Count);
        Assert.Equal(32, _fx.Extraction.Edges.Count);

        var byType = _fx.Extraction.Edges
            .GroupBy(e => e.Type)
            .ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(
            new Dictionary<string, int>
            {
                [EdgeTypes.Contains] = 21,
                [EdgeTypes.Calls] = 5,
                [EdgeTypes.HttpCalls] = 6,
            },
            byType);
    }

    // ---------------------------------------------------------------------------
    // 3. HTTP_CALLS edges — exact set
    // ---------------------------------------------------------------------------

    [Fact]
    public void Http_calls_edges_are_exactly_the_six_matched_client_methods()
    {
        var httpCalls = _fx.Extraction.Edges
            .Where(e => e.Type == EdgeTypes.HttpCalls)
            .Select(e => (From: NodeName(e.FromId), To: NodeName(e.ToId)))
            .ToHashSet();

        var expected = new HashSet<(string, string)>
        {
            // literal route
            ("Task<List<string>?> WidgetApiClient.GetWidgetsAsync()", MapWidgetEndpointsName),
            // interpolation hole vs {id:int} constraint segment
            ("Task<string?> WidgetApiClient.GetWidgetAsync(int id)", MapWidgetEndpointsName),
            // POST verb
            ("Task WidgetApiClient.CreateWidgetAsync(string name)", MapWidgetEndpointsName),
            // MapGroup prefix + query-string strip
            ("Task WidgetApiClient.DeleteWidgetAsync(int id, bool force)", MapWidgetEndpointsName),
            // non-constant concat suffix assumed query string
            ("Task<List<string>?> WidgetApiClient.SearchWidgetsAsync(string query)", MapWidgetEndpointsName),
            // attribute-routed controller action
            ("Task<string> WidgetApiClient.GetGadgetAsync(int id)", GetByIdName),
        };

        Assert.Equal(expected, httpCalls);
    }

    [Fact]
    public void Verb_mismatch_and_unknown_route_produce_no_edges()
    {
        var rename = GetMethod("Task WidgetApiClient.RenameWidgetAsync(int id, string name)").Id;
        var unknown = GetMethod("Task<string?> WidgetApiClient.GetUnknownAsync()").Id;

        Assert.DoesNotContain(_fx.Extraction.Edges,
            e => e.Type == EdgeTypes.HttpCalls && (e.FromId == rename || e.FromId == unknown));
    }

    [Fact]
    public void Http_calls_edges_carry_the_calling_document()
    {
        var httpCalls = _fx.Extraction.Edges.Where(e => e.Type == EdgeTypes.HttpCalls).ToList();
        Assert.All(httpCalls, e =>
            Assert.Equal("HttpFixture.Web/WidgetApiClient.cs", e.SourceDoc));
    }

    // ---------------------------------------------------------------------------
    // 4. Endpoint routes stamping
    // ---------------------------------------------------------------------------

    [Fact]
    public void Registration_method_carries_verb_prefixed_routes_in_declaration_order()
    {
        Assert.Equal(
            new[]
            {
                "GET /widgets",
                "GET /widget/{id:int}",
                "POST /widget",
                "DELETE /admin/widget/{id}",
            },
            GetMethod(MapWidgetEndpointsName).Routes);
    }

    [Fact]
    public void Controller_action_carries_the_composed_route()
    {
        Assert.Equal(new[] { "GET /gadget/{id}" }, GetMethod(GetByIdName).Routes);
    }

    [Fact]
    public void Client_methods_carry_no_routes()
    {
        foreach (var node in _fx.Extraction.Nodes.Where(
            n => n.Name.Contains("WidgetApiClient.", StringComparison.Ordinal)))
        {
            Assert.Null(node.Routes);
        }
    }

    // ---------------------------------------------------------------------------
    // 5. Composition with the ordinary CALLS pass
    // ---------------------------------------------------------------------------

    [Fact]
    public void Endpoint_lambda_bodies_produce_calls_edges_from_the_registration_method()
    {
        // The HTTP_CALLS → CALLS chain: client → MapWidgetEndpoints → Store.* — this is
        // what makes get_callers(Store.Get) surface Web-tier callers at depth 2.
        var fromId = GetMethod(MapWidgetEndpointsName).Id;
        var targets = _fx.Extraction.Edges
            .Where(e => e.Type == EdgeTypes.Calls && e.FromId == fromId)
            .Select(e => NodeName(e.ToId))
            .ToHashSet();

        Assert.Equal(
            new HashSet<string>
            {
                "List<string> Store.All()",
                "string Store.Get(int id)",
                "string Store.Add(string name)",
                "bool Store.Remove(int id)",
            },
            targets);
    }

    // ---------------------------------------------------------------------------
    // 6. Structural invariants
    // ---------------------------------------------------------------------------

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
        var second = await SymbolGraphExtractor.ExtractAsync(_fx.Solution, HttpMsBuildFixture.Branch);

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

    private NodeRow GetMethod(string name) =>
        Assert.Single(_fx.Extraction.Nodes, n => n.Kind == SymbolKinds.Method && n.Name == name);

    private string NodeName(string id) =>
        _fx.Extraction.Nodes.Single(n => n.Id == id).Name;
}
