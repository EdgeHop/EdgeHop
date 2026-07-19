namespace EdgeHop.Core;

/// <summary>
/// One extractor's contribution to cross-tier <c>JS_CALLS</c> matching: the C# call sites that
/// invoke JavaScript (Roslyn, from <c>IJSRuntime</c>/<c>IJSObjectReference</c> interop) and/or
/// the JS symbols callable from C# (oxc, the module-scoped exports). The two halves come from
/// different extractors, so — exactly like the HTTP pass — matching is deferred until every
/// extractor has run: the host concatenates each extractor's surface, then
/// <see cref="JsInteropMatcher"/> correlates C# sites against JS exports and emits the edges.
/// Both lists are empty for an extractor that contributes neither half.
/// </summary>
/// <param name="CsCallSites">C# → JS interop call sites (Roslyn fills this; oxc leaves it empty).</param>
/// <param name="JsExports">JS symbols exported for interop (oxc fills this; Roslyn leaves it empty).</param>
/// <param name="JsDotNetCalls">JS → C# interop call sites — <c>DotNet.invokeMethod[Async]</c> /
/// <c>objRef.invokeMethod[Async]</c> (oxc fills this; Roslyn leaves it empty).</param>
/// <param name="CsInvokableTargets">C# <c>[JSInvokable]</c> methods callable from JS (Roslyn
/// fills this; oxc leaves it empty).</param>
public sealed record InteropSurface(
    IReadOnlyList<CsJsInteropSite> CsCallSites,
    IReadOnlyList<JsInteropExport> JsExports,
    IReadOnlyList<JsDotNetCall> JsDotNetCalls,
    IReadOnlyList<CsInvokableTarget> CsInvokableTargets)
{
    /// <summary>An empty surface — an extractor that contributes no interop data returns this.</summary>
    public static InteropSurface Empty { get; } = new([], [], [], []);
}

/// <summary>
/// A C# JS-interop call site: a resolved <c>InvokeAsync</c>/<c>InvokeVoidAsync</c> with a
/// constant identifier, bound to its enclosing emitted C# method (the edge's source). Only
/// call sites whose identifier is a compile-time-constant string become sites — a non-literal
/// identifier (<c>InvokeAsync(nameof(X))</c>, a variable) is not statically knowable and is
/// dropped at collection, never producing a JS_CALLS edge.
/// </summary>
/// <param name="CallerId">Stable node id of the C# method containing the call (mandatory —
/// no site without a bound emitted caller, mirroring the HTTP pass).</param>
/// <param name="FunctionName">The constant identifier passed to the interop call — the JS
/// symbol name to match against an export.</param>
/// <param name="ModuleLeaf">Leaf file name (e.g. <c>widget.js</c>) of the JS module the call's
/// receiver was imported from (<c>JS.InvokeAsync&lt;IJSObjectReference&gt;("import", "./widget.js")</c>),
/// resolved by same-type receiver correlation; <c>null</c> when the receiver is the runtime
/// itself (a global <c>JS.InvokeVoidAsync("fn")</c>) or correlation failed. Precise matching
/// uses it to disambiguate same-named exports across modules.</param>
/// <param name="SourceDoc">Document containing the call (stamped onto the emitted edge).</param>
public sealed record CsJsInteropSite(
    string CallerId,
    string FunctionName,
    string? ModuleLeaf,
    string? SourceDoc);

/// <summary>
/// A JS symbol callable from C# JS-interop: a module-scoped exported function/const the oxc
/// extractor emitted a node for. The edge's target.
/// </summary>
/// <param name="Name">Exported symbol name (matched against a call site's identifier).</param>
/// <param name="ModuleId">The JS module id the export lives in (its leaf disambiguates precise
/// matches against a call site's <see cref="CsJsInteropSite.ModuleLeaf"/>).</param>
/// <param name="SymbolId">Stable node id of the exported JS symbol (the edge target; carries the
/// <c>js|</c> tier tag).</param>
/// <param name="SourceDoc">The JS document the symbol is declared in.</param>
public sealed record JsInteropExport(
    string Name,
    string ModuleId,
    string SymbolId,
    string? SourceDoc);

/// <summary>
/// A JS → C# interop call site: a resolved <c>DotNet.invokeMethod[Async]("Assembly",
/// "Identifier", …)</c> (static) or <c>objRef.invokeMethod[Async]("Identifier", …)</c>
/// (instance) whose identifier — and, for static, assembly — is a string literal, bound to its
/// enclosing JS function (the edge's source). Only literal-argument sites become calls; a
/// computed identifier is not statically knowable and is dropped at collection.
/// </summary>
/// <param name="CallerId">Node id of the JS function containing the call (the edge source;
/// carries the <c>js|</c> tier tag).</param>
/// <param name="Assembly">The .NET assembly name literal for a static call
/// (<c>DotNet.invokeMethodAsync</c>'s first argument); <c>null</c> for an instance call, whose
/// receiving object's type JS cannot know.</param>
/// <param name="Identifier">The <c>[JSInvokable]</c> identifier being invoked — the method's
/// export name (matched against a target's identifier).</param>
/// <param name="IsStatic"><c>true</c> for <c>DotNet.invokeMethod[Async]</c> (static), <c>false</c>
/// for an object-reference <c>invokeMethod[Async]</c> (instance).</param>
/// <param name="SourceDoc">The JS document containing the call (stamped onto the emitted edge).</param>
public sealed record JsDotNetCall(
    string CallerId,
    string? Assembly,
    string Identifier,
    bool IsStatic,
    string? SourceDoc);

/// <summary>
/// A C# <c>[JSInvokable]</c> method callable from JavaScript: the JS→C# edge's target.
/// </summary>
/// <param name="MethodId">Stable node id of the C# method (the edge target).</param>
/// <param name="Identifier">The invokable identifier — the <c>[JSInvokable("name")]</c> argument
/// when present, else the method name (this is exactly what JS passes).</param>
/// <param name="Assembly">The method's containing assembly name. For a static invokable method
/// this plus <see cref="Identifier"/> is Blazor's unique key; unused for instance matching.</param>
/// <param name="IsStatic"><c>true</c> for a <c>static</c> <c>[JSInvokable]</c> method (reached via
/// <c>DotNet.invokeMethod[Async]</c>), <c>false</c> for an instance one (reached via an object
/// reference).</param>
/// <param name="SourceDoc">The document the method is declared in.</param>
public sealed record CsInvokableTarget(
    string MethodId,
    string Identifier,
    string Assembly,
    bool IsStatic,
    string? SourceDoc);
