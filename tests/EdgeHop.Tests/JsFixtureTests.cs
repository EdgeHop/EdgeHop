using EdgeHop.Core;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// The regression anchor for cross-tier JS interop in BOTH directions — C#→JS (<c>JS_CALLS</c>)
/// and JS→C# (<c>JS_INVOKES</c>) — driven end-to-end through the real pipeline (Roslyn + oxc +
/// host merge/match; see <see cref="JsMsBuildFixture"/>) in the default
/// <see cref="JsInteropMode.Precise"/> mode. Exact expectations live in
/// <c>fixtures/JsFixture/EXPECTED-GRAPH.md</c>. Covers:
/// <list type="bullet">
/// <item><description>the module-correlated match (<c>_module.InvokeAsync("getWidget")</c> after
/// <c>import "./js/widget.js"</c> → <c>widget.js#getWidget</c>),</description></item>
/// <item><description>the global unique-name match (<c>JS.InvokeVoidAsync("showAlert")</c> →
/// <c>site.js#showAlert</c>),</description></item>
/// <item><description>and the anti-cases that must produce NO edge: a non-constant identifier,
/// an unexported name, the <c>ComponentBase.InvokeAsync(StateHasChanged)</c> dispatcher, and the
/// skipped <c>*.min.js</c> / <c>node_modules</c> duplicates (whose survival would make
/// <c>showAlert</c> ambiguous and drop its edge).</description></item>
/// </list>
/// </summary>
[Collection(JsMsBuildTestCollection.Name)]
public sealed class JsFixtureTests
{
    private readonly JsMsBuildFixture _fx;

    public JsFixtureTests(JsMsBuildFixture fx) => _fx = fx;

    // ---------------------------------------------------------------- JS side --------

    [Fact]
    public void Oxc_emits_exactly_the_discovered_js_symbols()
    {
        var jsNodes = _fx.Graph.Nodes.Where(n => n.Id.Contains("js|", StringComparison.Ordinal)).ToList();

        // widget.js (ns) + getWidget + format + wireCallbacks, site.js (ns) + showAlert = 6. The
        // *.min.js and node_modules duplicates are skipped, so no extra showAlert/getWidget nodes.
        Assert.Equal(6, jsNodes.Count);
        Assert.Single(jsNodes, n => n.Kind == SymbolKinds.Method && n.Name == "getWidget");
        Assert.Single(jsNodes, n => n.Kind == SymbolKinds.Method && n.Name == "format");
        Assert.Single(jsNodes, n => n.Kind == SymbolKinds.Method && n.Name == "wireCallbacks");
        Assert.Single(jsNodes, n => n.Kind == SymbolKinds.Method && n.Name == "showAlert");

        // Nothing was parsed out of the skipped files.
        Assert.DoesNotContain(_fx.Graph.Nodes, n =>
            n.SourceDoc is { } doc &&
            (doc.EndsWith(".min.js", StringComparison.Ordinal)
             || doc.Contains("node_modules", StringComparison.Ordinal)));
    }

    [Fact]
    public void Js_internal_calls_edge_is_present()
    {
        // getWidget() calls the module-private format(): a JS-internal CALLS edge.
        var getWidget = JsMethod("getWidget");
        var format = JsMethod("format");
        Assert.Contains(_fx.Graph.Edges, e =>
            e.Type == EdgeTypes.Calls && e.FromId == getWidget.Id && e.ToId == format.Id);
    }

    // ---------------------------------------------------------------- JS_CALLS -------

    [Fact]
    public void Js_calls_edges_are_exactly_the_two_precise_matches()
    {
        var jsCalls = _fx.Graph.Edges.Where(e => e.Type == EdgeTypes.JsCalls).ToList();

        // getWidget (module-correlated) and showAlert (global unique) — and nothing else.
        Assert.Equal(2, jsCalls.Count);
        Assert.Equal(
            new HashSet<string> { "getWidget", "showAlert" },
            jsCalls.Select(e => NodeName(e.ToId)).ToHashSet(StringComparer.Ordinal));

        // Both originate from the same authored C# caller, Widget.Refresh().
        var refresh = CsMethod("Refresh");
        Assert.All(jsCalls, e => Assert.Equal(refresh.Id, e.FromId));

        // The edge carries the C# calling document (the .razor), not the JS file.
        Assert.All(jsCalls, e => Assert.Equal("Widget.razor", e.SourceDoc));
    }

    // ---------------------------------------------------------------- JS_INVOKES ----

    [Fact]
    public void Js_invokes_edges_are_exactly_the_three_jsinvokable_targets()
    {
        var invokes = _fx.Graph.Edges.Where(e => e.Type == EdgeTypes.JsInvokes).ToList();

        // AddNumbers (static, assembly+identifier), Notify (instance identifier), and Renamed
        // (instance via the [JSInvokable("CustomName")] override) — and nothing else. The
        // "NoSuchInvokable" call has no [JSInvokable] target, so it produces no edge.
        Assert.Equal(3, invokes.Count);

        // All originate from the JS wireCallbacks function; all target C# methods (no js| tag).
        var wire = JsMethod("wireCallbacks");
        Assert.All(invokes, e => Assert.Equal(wire.Id, e.FromId));
        Assert.All(invokes, e => Assert.DoesNotContain("js|", e.ToId, StringComparison.Ordinal));

        var targetNames = invokes.Select(e => NodeName(e.ToId)).ToList();
        Assert.Contains(targetNames, n => n.Contains("AddNumbers", StringComparison.Ordinal));
        Assert.Contains(targetNames, n => n.Contains("Notify", StringComparison.Ordinal));
        Assert.Contains(targetNames, n => n.Contains("Renamed", StringComparison.Ordinal));

        // The edge carries the JS calling document.
        Assert.All(invokes, e => Assert.Equal("wwwroot/js/widget.js", e.SourceDoc));
    }

    [Fact]
    public void Anti_cases_produce_no_js_calls_edge()
    {
        var jsCalls = _fx.Graph.Edges.Where(e => e.Type == EdgeTypes.JsCalls).ToList();
        var targetNames = jsCalls.Select(e => NodeName(e.ToId)).ToHashSet(StringComparer.Ordinal);

        // No export named "noSuchFunction" exists → no edge.
        Assert.DoesNotContain("noSuchFunction", targetNames);
        // The non-constant identifier (_dynamicName) is not statically knowable → no edge.
        Assert.DoesNotContain("computedAtRuntime", targetNames);
        // ComponentBase.InvokeAsync(StateHasChanged) is not JS interop → never a target.
        Assert.DoesNotContain("StateHasChanged", targetNames);
    }

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

    // ---------------------------------------------------------------- helpers --------

    private NodeRow JsMethod(string name) =>
        Assert.Single(_fx.Graph.Nodes, n =>
            n.Kind == SymbolKinds.Method && n.Name == name
            && n.Id.Contains("js|", StringComparison.Ordinal));

    private NodeRow CsMethod(string nameFragment) =>
        Assert.Single(_fx.Graph.Nodes, n =>
            n.Kind == SymbolKinds.Method
            && !n.Id.Contains("js|", StringComparison.Ordinal)
            && n.Name.Contains($".{nameFragment}(", StringComparison.Ordinal));

    private string NodeName(string id) => _fx.Graph.Nodes.Single(n => n.Id == id).Name;
}
