using EdgeHop.Core;
using EdgeHop.Oxc;
using EdgeHop.Roslyn;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// The regression anchor for indexing a BARE DIRECTORY — a pure JS/TS project with no
/// <c>.sln</c> and no <c>.csproj</c> — via <c>edgehop-extract index &lt;dir&gt;</c>. Runs the
/// REAL production extractors through the REAL host merge path
/// (<see cref="IndexCommand.BuildDesiredGraph"/>): the Roslyn C# extractor no-ops (no solution
/// to load) and the oxc extractor graphs the folder. Exact expectations live in
/// <c>fixtures/JsFolderFixture/EXPECTED-GRAPH.md</c> (5 nodes / 4 edges). No MSBuild, no database.
/// </summary>
public sealed class JsFolderFixtureTests : IAsyncLifetime
{
    private const string Branch = "main";

    private ExtractionResult _graph = null!;
    private ExtractionOutcome _roslynOutcome = null!;

    public async Task InitializeAsync()
    {
        var fixtureDir = FixtureTestSupport.LocateFixtureDirectory("JsFolderFixture");

        // Directory mode: no solution file; the root IS the directory. This is exactly the
        // request edgehop-extract builds for `index <dir>`.
        var request = new ExtractionRequest(SolutionPath: null, Branch, fixtureDir);

        await using var roslyn = new RoslynExtractor();
        await using var oxc = new OxcExtractor();
        _roslynOutcome = await roslyn.ExtractAsync(request);
        var oxcOutcome = await oxc.ExtractAsync(request);

        var failures = _roslynOutcome.FailureDiagnostics
            .Concat(oxcOutcome.FailureDiagnostics)
            .ToList();
        Assert.True(failures.Count == 0, "Unexpected Failure diagnostics: " + string.Join("; ", failures));

        _graph = IndexCommand.BuildDesiredGraph(
            [_roslynOutcome, oxcOutcome], Branch, JsInteropMode.Precise);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Roslyn_contributes_nothing_for_a_directory_target()
    {
        // No .sln → the C# extractor returns an empty result and never touches MSBuild.
        Assert.Empty(_roslynOutcome.Result.Nodes);
        Assert.Empty(_roslynOutcome.Result.Edges);
        Assert.Empty(_roslynOutcome.FailureDiagnostics);
        Assert.Equal("roslyn (no solution)", _roslynOutcome.LoadDescription);
    }

    [Fact]
    public void Totals_match_the_pinned_contract()
    {
        // 5 nodes / 4 edges (CONTAINS 3, CALLS 1) — the whole graph is the contract here.
        Assert.Equal(5, _graph.Nodes.Count);
        Assert.Equal(4, _graph.Edges.Count);
        Assert.Equal(3, _graph.Edges.Count(e => e.Type == EdgeTypes.Contains));
        Assert.Equal(1, _graph.Edges.Count(e => e.Type == EdgeTypes.Calls));

        // No C# tier ⇒ no interop edges derived by the host.
        Assert.DoesNotContain(_graph.Edges, e =>
            e.Type == EdgeTypes.JsCalls || e.Type == EdgeTypes.JsInvokes);
    }

    [Fact]
    public void Every_node_is_a_js_node_with_the_tier_tag()
    {
        Assert.All(_graph.Nodes, n => Assert.Contains("js|", n.Id, StringComparison.Ordinal));
        Assert.Equal(2, _graph.Nodes.Count(n => n.Kind == SymbolKinds.Namespace));
        Assert.Equal(3, _graph.Nodes.Count(n => n.Kind == SymbolKinds.Method));
    }

    [Fact]
    public void Discovered_symbols_are_exactly_the_two_real_modules()
    {
        Assert.Single(_graph.Nodes, n => n.Kind == SymbolKinds.Method && n.Name == "greet");
        Assert.Single(_graph.Nodes, n => n.Kind == SymbolKinds.Method && n.Name == "decorate");
        Assert.Single(_graph.Nodes, n => n.Kind == SymbolKinds.Method && n.Name == "shout");
        Assert.Single(_graph.Nodes, n => n.Kind == SymbolKinds.Namespace && n.Name == "app.js");
        Assert.Single(_graph.Nodes, n => n.Kind == SymbolKinds.Namespace && n.Name == "util.js");

        // The *.min.js and node_modules duplicates are skipped, so greet/shout stay unambiguous
        // and nothing was parsed out of those files.
        Assert.DoesNotContain(_graph.Nodes, n =>
            n.SourceDoc is { } doc &&
            (doc.EndsWith(".min.js", StringComparison.Ordinal)
             || doc.Contains("node_modules", StringComparison.Ordinal)));
    }

    [Fact]
    public void Same_module_calls_edge_is_present()
    {
        // greet() calls the module-private decorate(): a JS-internal CALLS edge.
        var greet = JsMethod("greet");
        var decorate = JsMethod("decorate");
        Assert.Contains(_graph.Edges, e =>
            e.Type == EdgeTypes.Calls && e.FromId == greet.Id && e.ToId == decorate.Id);
    }

    [Fact]
    public void Every_edge_connects_two_emitted_nodes()
    {
        var ids = _graph.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var edge in _graph.Edges)
        {
            Assert.Contains(edge.FromId, ids);
            Assert.Contains(edge.ToId, ids);
        }
    }

    private NodeRow JsMethod(string name) =>
        Assert.Single(_graph.Nodes, n => n.Kind == SymbolKinds.Method && n.Name == name);
}
