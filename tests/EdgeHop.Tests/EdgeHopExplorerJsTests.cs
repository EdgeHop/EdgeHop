using EdgeHop.Core;
using EdgeHop.Oxc;
using EdgeHop.Roslyn;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// The pure JS/HTML sample anchor. <c>tests/samples/EdgeHopExplorer.Js</c> is a <b>bare
/// directory</b> — no <c>.sln</c>, no <c>.csproj</c> — indexed exactly like
/// <c>edgehop-extract index &lt;dir&gt;</c>: the Roslyn C# extractor no-ops (no solution to load)
/// and the oxc extractor graphs the tree. It exercises standalone <c>.js</c>/<c>.ts</c> modules,
/// inline <c>&lt;script&gt;</c> blocks in <c>.html</c> pages (each its own module), TypeScript type
/// constructs, and the skip list (<c>*.min.js</c> / <c>node_modules</c>). Exact expectations live in
/// <c>tests/samples/EdgeHopExplorer.Js/EXPECTED-GRAPH.md</c>.
/// </summary>
public sealed class EdgeHopExplorerJsTests : IAsyncLifetime
{
    private const string Branch = "main";

    private ExtractionResult _graph = null!;
    private ExtractionOutcome _roslynOutcome = null!;

    public async Task InitializeAsync()
    {
        var sampleDir = FixtureTestSupport.LocateSampleDirectory("EdgeHopExplorer.Js");

        // Directory mode: no solution file; the root IS the directory. Exactly the request the
        // indexer builds for `index <dir>`.
        var request = new ExtractionRequest(SolutionPath: null, Branch, sampleDir);

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
        Assert.Equal("roslyn (no solution)", _roslynOutcome.LoadDescription);
    }

    [Fact]
    public void Totals_match_the_pinned_contract()
    {
        Assert.Equal(25, _graph.Nodes.Count);
        Assert.Equal(26, _graph.Edges.Count);

        var byKind = _graph.Nodes.GroupBy(n => n.Kind).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(
            new Dictionary<string, int>
            {
                [SymbolKinds.Method] = 13,
                [SymbolKinds.Namespace] = 6,
                [SymbolKinds.NamedType] = 4,
                [SymbolKinds.Field] = 2,
            },
            byKind);

        var byType = _graph.Edges.GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(
            new Dictionary<string, int>
            {
                [EdgeTypes.Contains] = 19,
                [EdgeTypes.Calls] = 7,
            },
            byType);

        // A pure-JS tree with no C# tier ⇒ no interop edges are ever derived.
        Assert.DoesNotContain(_graph.Edges, e =>
            e.Type == EdgeTypes.JsCalls || e.Type == EdgeTypes.JsInvokes);
    }

    [Fact]
    public void Every_node_is_a_js_node_with_the_tier_tag()
    {
        Assert.All(_graph.Nodes, n => Assert.Contains("js|", n.Id, StringComparison.Ordinal));
    }

    [Fact]
    public void The_six_modules_are_the_three_standalone_files_and_three_inline_scripts()
    {
        var modules = _graph.Nodes
            .Where(n => n.Kind == SymbolKinds.Namespace)
            .Select(n => n.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "catalog.js", "render.js", "types.ts",   // standalone modules
                "index.html#0", "features.html#0", "about.html#0", // inline <script> blocks
            },
            modules);

        // Each inline module keeps the authored .html file as its sourceDoc.
        foreach (var (name, doc) in new[]
                 {
                     ("index.html#0", "index.html"),
                     ("features.html#0", "features.html"),
                     ("about.html#0", "about.html"),
                 })
        {
            Assert.Single(_graph.Nodes, n =>
                n.Kind == SymbolKinds.Namespace && n.Name == name && n.SourceDoc == doc);
        }
    }

    [Fact]
    public void Inline_script_members_are_extracted()
    {
        // The functions declared inside the inline <script> blocks become real Method nodes.
        foreach (var fn in new[] { "boot", "summarize", "track", "record", "greet", "message" })
        {
            Assert.Single(_graph.Nodes, n => n.Kind == SymbolKinds.Method && n.Name == fn);
        }
    }

    [Fact]
    public void Same_module_calls_are_exactly_the_seven_resolved_edges()
    {
        var calls = _graph.Edges
            .Where(e => e.Type == EdgeTypes.Calls)
            .Select(e => (From: NodeName(e.FromId), To: NodeName(e.ToId)))
            .ToHashSet();

        Assert.Equal(
            new HashSet<(string, string)>
            {
                ("describeFeature", "label"),   // catalog.js
                ("first", "describeFeature"),   // catalog.js — Catalog.first()
                ("renderAll", "renderItem"),    // render.js
                ("toFeature", "areaOf"),        // types.ts
                ("boot", "summarize"),          // index.html#0
                ("track", "record"),            // features.html#0
                ("greet", "message"),           // about.html#0
            },
            calls);
    }

    [Fact]
    public void Typescript_type_constructs_become_named_types()
    {
        // A `type` alias, an `interface`, and an `enum` in types.ts each become a NamedType node.
        Assert.Single(_graph.Nodes, n => n.Kind == SymbolKinds.NamedType && n.Name == "FeatureName");
        Assert.Single(_graph.Nodes, n => n.Kind == SymbolKinds.NamedType && n.Name == "Feature");
        Assert.Single(_graph.Nodes, n => n.Kind == SymbolKinds.NamedType && n.Name == "Area");

        // The JS class Catalog is a NamedType too.
        Assert.Single(_graph.Nodes, n => n.Kind == SymbolKinds.NamedType && n.Name == "Catalog");
    }

    [Fact]
    public void Skipped_files_never_produce_nodes()
    {
        // *.min.js and node_modules are skipped wholesale, so their duplicate describeFeature /
        // label / track exports are never parsed — the real ones stay single and unambiguous.
        Assert.Single(_graph.Nodes, n => n.Kind == SymbolKinds.Method && n.Name == "describeFeature");

        Assert.DoesNotContain(_graph.Nodes, n =>
            n.SourceDoc is { } doc &&
            (doc.EndsWith(".min.js", StringComparison.Ordinal)
             || doc.Contains("node_modules", StringComparison.Ordinal)));

        // leftPad lives only under node_modules → it must not be a node.
        Assert.DoesNotContain(_graph.Nodes, n => n.Name == "leftPad");
    }

    [Fact]
    public void Script_mentioned_inside_an_html_comment_is_skipped()
    {
        // about.html contains an HTML comment that mentions a literal
        // <script>function ghostFn(){}</script>. The comment-aware discovery must treat it as prose:
        // no phantom module and no ghostFn node — leaving exactly the six real modules.
        Assert.DoesNotContain(_graph.Nodes, n => n.Name == "ghostFn");
        Assert.Equal(6, _graph.Nodes.Count(n => n.Kind == SymbolKinds.Namespace));
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

    private string NodeName(string id) => _graph.Nodes.Single(n => n.Id == id).Name;
}
