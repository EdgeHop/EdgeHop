using EdgeHop.Core;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// Unit tests for <see cref="JsInteropMatcher"/> — the pure correlation of interop call sites
/// against targets into cross-tier edges, both directions: C#→JS <c>JS_CALLS</c>
/// (<see cref="JsInteropMatcher.Match"/>) and JS→C# <c>JS_INVOKES</c>
/// (<see cref="JsInteropMatcher.MatchDotNetInvokes"/>). Covers precise/broad/off, disambiguation,
/// and the anti-cases (ambiguity, missing endpoints, no target). No database or extractor: the
/// surface is built by hand.
/// </summary>
public sealed class JsInteropMatcherTests
{
    private const string Branch = "test-branch";
    private const string Caller = "Method:App.Widget.Refresh()";

    private static JsInteropExport Export(string name, string module) =>
        new(name, module, $"Method:js|{module}#{name}", module);

    private static CsJsInteropSite Site(string fn, string? leaf = null) =>
        new(Caller, fn, leaf, "App/Widget.razor.cs");

    /// <summary>A C#→JS surface (the other two lists empty).</summary>
    private static InteropSurface CsJs(IReadOnlyList<CsJsInteropSite> sites, IReadOnlyList<JsInteropExport> exports) =>
        new(sites, exports, [], []);

    private static IReadOnlySet<string> Nodes(params string[] ids) =>
        new HashSet<string>(ids, StringComparer.Ordinal);

    private static IReadOnlySet<string> NodesFor(CsJsInteropSite site, params JsInteropExport[] exports) =>
        new HashSet<string>(exports.Select(e => e.SymbolId).Append(site.CallerId), StringComparer.Ordinal);

    // ======================================================= C#→JS (JS_CALLS) ========
    // ---------------------------------------------------------------- precise --------

    [Fact]
    public void Precise_UniqueGlobalName_EmitsOneEdge()
    {
        var site = Site("getWidget");
        var export = Export("getWidget", "js/widget.js");

        var edges = JsInteropMatcher.Match(CsJs([site], [export]), NodesFor(site, export), Branch, JsInteropMode.Precise);

        var edge = Assert.Single(edges);
        Assert.Equal(EdgeTypes.JsCalls, edge.Type);
        Assert.Equal(Caller, edge.FromId);
        Assert.Equal(export.SymbolId, edge.ToId);
        Assert.Equal(Branch, edge.Branch);
        Assert.Equal("App/Widget.razor.cs", edge.SourceDoc);
    }

    [Fact]
    public void Precise_ModuleLeafDisambiguatesSameNameAcrossModules()
    {
        var site = Site("render", leaf: "widget.js");
        var widget = Export("render", "js/widget.js");
        var chart = Export("render", "js/chart.js");

        var edges = JsInteropMatcher.Match(
            CsJs([site], [widget, chart]), NodesFor(site, widget, chart), Branch, JsInteropMode.Precise);

        Assert.Equal(widget.SymbolId, Assert.Single(edges).ToId);
    }

    [Fact]
    public void Precise_AmbiguousGlobalName_EmitsNothing()
    {
        var site = Site("render");
        var a = Export("render", "js/widget.js");
        var b = Export("render", "js/chart.js");

        Assert.Empty(JsInteropMatcher.Match(
            CsJs([site], [a, b]), NodesFor(site, a, b), Branch, JsInteropMode.Precise));
    }

    [Fact]
    public void Precise_ModuleLeafMatchesNoExport_EmitsNothing()
    {
        var site = Site("getWidget", leaf: "other.js");
        var export = Export("getWidget", "js/widget.js");

        Assert.Empty(JsInteropMatcher.Match(
            CsJs([site], [export]), NodesFor(site, export), Branch, JsInteropMode.Precise));
    }

    [Fact]
    public void Precise_NoExportOfThatName_EmitsNothing()
    {
        var site = Site("doesNotExist");
        var export = Export("getWidget", "js/widget.js");

        Assert.Empty(JsInteropMatcher.Match(
            CsJs([site], [export]), NodesFor(site, export), Branch, JsInteropMode.Precise));
    }

    // ---------------------------------------------------------------- broad ----------

    [Fact]
    public void Broad_FansOutToEverySameNamedExport()
    {
        var site = Site("render");
        var a = Export("render", "js/widget.js");
        var b = Export("render", "js/chart.js");

        var edges = JsInteropMatcher.Match(CsJs([site], [a, b]), NodesFor(site, a, b), Branch, JsInteropMode.Broad);

        Assert.Equal(
            new HashSet<string> { a.SymbolId, b.SymbolId },
            edges.Select(e => e.ToId).ToHashSet());
    }

    // ---------------------------------------------------------------- guards ---------

    [Fact]
    public void Off_EmitsNothing()
    {
        var site = Site("getWidget");
        var export = Export("getWidget", "js/widget.js");
        Assert.Empty(JsInteropMatcher.Match(CsJs([site], [export]), NodesFor(site, export), Branch, JsInteropMode.Off));
    }

    [Fact]
    public void CallerNotEmitted_EmitsNothing()
    {
        var site = Site("getWidget");
        var export = Export("getWidget", "js/widget.js");
        Assert.Empty(JsInteropMatcher.Match(CsJs([site], [export]), Nodes(export.SymbolId), Branch, JsInteropMode.Precise));
    }

    [Fact]
    public void ExportSymbolNotEmitted_EmitsNothing()
    {
        var site = Site("getWidget");
        var export = Export("getWidget", "js/widget.js");
        Assert.Empty(JsInteropMatcher.Match(CsJs([site], [export]), Nodes(Caller), Branch, JsInteropMode.Precise));
    }

    [Fact]
    public void DuplicateSites_DedupeToOneEdge()
    {
        var site = Site("getWidget");
        var export = Export("getWidget", "js/widget.js");
        Assert.Single(JsInteropMatcher.Match(
            CsJs([site, site], [export]), NodesFor(site, export), Branch, JsInteropMode.Precise));
    }

    [Fact]
    public void EmptyEitherHalf_EmitsNothing()
    {
        var site = Site("getWidget");
        var export = Export("getWidget", "js/widget.js");
        var ids = NodesFor(site, export);
        Assert.Empty(JsInteropMatcher.Match(CsJs([site], []), ids, Branch, JsInteropMode.Precise));
        Assert.Empty(JsInteropMatcher.Match(CsJs([], [export]), ids, Branch, JsInteropMode.Precise));
    }

    // ====================================================== JS→C# (JS_INVOKES) =======

    private const string JsCaller = "Method:js|wwwroot/js/app.js#run";

    private static CsInvokableTarget Invokable(string identifier, string assembly, bool isStatic) =>
        new($"Method:{assembly}.T.{identifier}(){(isStatic ? "|s" : "|i")}", identifier, assembly, isStatic, "T.cs");

    private static JsDotNetCall StaticCall(string assembly, string identifier) =>
        new(JsCaller, assembly, identifier, IsStatic: true, "wwwroot/js/app.js");

    private static JsDotNetCall InstanceCall(string identifier) =>
        new(JsCaller, Assembly: null, identifier, IsStatic: false, "wwwroot/js/app.js");

    private static InteropSurface JsCs(IReadOnlyList<JsDotNetCall> calls, IReadOnlyList<CsInvokableTarget> targets) =>
        new([], [], calls, targets);

    private static IReadOnlySet<string> NodesFor(JsDotNetCall call, params CsInvokableTarget[] targets) =>
        new HashSet<string>(targets.Select(t => t.MethodId).Append(call.CallerId), StringComparer.Ordinal);

    [Fact]
    public void Invokes_Precise_StaticMatchesOnAssemblyAndIdentifier()
    {
        var call = StaticCall("MyApp", "AddNumbers");
        var target = Invokable("AddNumbers", "MyApp", isStatic: true);

        var edges = JsInteropMatcher.MatchDotNetInvokes(
            JsCs([call], [target]), NodesFor(call, target), Branch, JsInteropMode.Precise);

        var edge = Assert.Single(edges);
        Assert.Equal(EdgeTypes.JsInvokes, edge.Type);
        Assert.Equal(JsCaller, edge.FromId);
        Assert.Equal(target.MethodId, edge.ToId);
        Assert.Equal("wwwroot/js/app.js", edge.SourceDoc);
    }

    [Fact]
    public void Invokes_Precise_StaticWrongAssembly_EmitsNothing()
    {
        var call = StaticCall("OtherAsm", "AddNumbers");
        var target = Invokable("AddNumbers", "MyApp", isStatic: true);

        Assert.Empty(JsInteropMatcher.MatchDotNetInvokes(
            JsCs([call], [target]), NodesFor(call, target), Branch, JsInteropMode.Precise));
    }

    [Fact]
    public void Invokes_Precise_InstanceMatchesUniqueIdentifier()
    {
        var call = InstanceCall("Notify");
        var target = Invokable("Notify", "MyApp", isStatic: false);

        Assert.Equal(
            target.MethodId,
            Assert.Single(JsInteropMatcher.MatchDotNetInvokes(
                JsCs([call], [target]), NodesFor(call, target), Branch, JsInteropMode.Precise)).ToId);
    }

    [Fact]
    public void Invokes_Precise_InstanceAmbiguousIdentifier_EmitsNothing()
    {
        var call = InstanceCall("Notify");
        var a = Invokable("Notify", "MyApp", isStatic: false);
        var b = new CsInvokableTarget("Method:MyApp.Other.Notify()|i", "Notify", "MyApp", false, "Other.cs");

        Assert.Empty(JsInteropMatcher.MatchDotNetInvokes(
            JsCs([call], [a, b]), NodesFor(call, a, b), Branch, JsInteropMode.Precise));
    }

    [Fact]
    public void Invokes_Precise_StaticDoesNotMatchInstanceTargetOfSameName()
    {
        // A static call must key on (assembly, identifier) among STATIC targets only.
        var call = StaticCall("MyApp", "Notify");
        var instance = Invokable("Notify", "MyApp", isStatic: false);

        Assert.Empty(JsInteropMatcher.MatchDotNetInvokes(
            JsCs([call], [instance]), NodesFor(call, instance), Branch, JsInteropMode.Precise));
    }

    [Fact]
    public void Invokes_Broad_FansOutByIdentifierAcrossStaticAndInstance()
    {
        var call = InstanceCall("Notify");
        var s = Invokable("Notify", "MyApp", isStatic: true);
        var i = Invokable("Notify", "MyApp", isStatic: false);

        var edges = JsInteropMatcher.MatchDotNetInvokes(
            JsCs([call], [s, i]), NodesFor(call, s, i), Branch, JsInteropMode.Broad);

        Assert.Equal(
            new HashSet<string> { s.MethodId, i.MethodId },
            edges.Select(e => e.ToId).ToHashSet());
    }

    [Fact]
    public void Invokes_Off_EmitsNothing()
    {
        var call = StaticCall("MyApp", "AddNumbers");
        var target = Invokable("AddNumbers", "MyApp", isStatic: true);
        Assert.Empty(JsInteropMatcher.MatchDotNetInvokes(
            JsCs([call], [target]), NodesFor(call, target), Branch, JsInteropMode.Off));
    }

    [Fact]
    public void Invokes_MissingEndpoints_EmitNothing()
    {
        var call = StaticCall("MyApp", "AddNumbers");
        var target = Invokable("AddNumbers", "MyApp", isStatic: true);

        // Caller JS node absent.
        Assert.Empty(JsInteropMatcher.MatchDotNetInvokes(
            JsCs([call], [target]), Nodes(target.MethodId), Branch, JsInteropMode.Precise));
        // Target C# node absent.
        Assert.Empty(JsInteropMatcher.MatchDotNetInvokes(
            JsCs([call], [target]), Nodes(JsCaller), Branch, JsInteropMode.Precise));
    }

    // ---------------------------------------------------------------- mode parse -----

    [Theory]
    [InlineData(null, JsInteropMode.Precise)]
    [InlineData("", JsInteropMode.Precise)]
    [InlineData("precise", JsInteropMode.Precise)]
    [InlineData("PRECISE", JsInteropMode.Precise)]
    [InlineData("  broad ", JsInteropMode.Broad)]
    [InlineData("off", JsInteropMode.Off)]
    [InlineData("none", JsInteropMode.Off)]
    [InlineData("nonsense", JsInteropMode.Precise)]
    public void ResolveMode_MapsEnvValue(string? raw, JsInteropMode expected) =>
        Assert.Equal(expected, JsInteropMatcher.ResolveMode(raw));
}
