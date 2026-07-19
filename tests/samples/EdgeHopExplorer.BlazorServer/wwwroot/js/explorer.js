// explorer.js — standalone ES module for the EdgeHop Explorer (Blazor Server sample).
//
// What EdgeHop's oxc extractor captures here (every id carries the `js|` tier tag so it can
// never collide with a C# symbol):
//   * the module itself                       -> a Namespace node ("explorer.js")
//   * each `function` / class method          -> a Method node
//   * each module-scope `const` / class field -> a Field node
//   * a class declaration                     -> a NamedType node
//   * a bare-identifier call to a same-file function -> a CALLS edge
//   * `DotNet.invokeMethod*` calls            -> JS_INVOKES edges into C# [JSInvokable] methods
//
// Authoring note: an arrow function assigned to `const` becomes a Field node and is invisible to
// CALLS resolution, so the callable helpers below are deliberately function *declarations*.

export const EXPLORER_VERSION = "1.0.0";

// Exported -> a JS_CALLS target (the C# component invokes "highlight"); also a CALLS *source*.
export function highlight(id) {
    return decorate("feature-" + id);
}

// Module-private helper: a Method node, but NOT exported — so an InvokeAsync("decorate") from C#
// would find no matching export and (correctly) produce no JS_CALLS edge.
function decorate(label) {
    return "«" + label + "»";
}

// Exported. The C# component hands this its DotNetObjectReference and then this calls back into C#
// — the JS->C# half of the handshake. Both DotNet.invoke* forms become JS_INVOKES edges (precise).
export function wireInterop(dotNetRef) {
    // Static form: (assembly, identifier). The assembly literal MUST equal this project's assembly
    // name for the precise match to resolve to the static [JSInvokable] Ping.
    DotNet.invokeMethodAsync("EdgeHopExplorer.BlazorServer", "Ping", 1);
    // Instance form: identifier only -> matches the solution-unique instance [JSInvokable] OnJsEvent.
    dotNetRef.invokeMethodAsync("OnJsEvent", "explorer ready");
}

// A class -> a NamedType node; `count` is a Field node; `run` is a Method node and a CALLS source.
export class Explorer {
    count = 0;

    run() {
        return highlight(this.count);
    }
}
