// The Blazor JS module imported by Widget.razor as "./js/widget.js".

export function getWidget(id) {
    return format(id);
}

// A module-private helper: emitted as a JS node and a CALLS target, but NOT an interop
// export, so a C# InvokeAsync("format") would produce no edge.
function format(id) {
    return "widget-" + id;
}

// The JS→C# direction: this JS function invokes C# [JSInvokable] methods.
export function wireCallbacks(dotNetRef) {
    // Static: DotNet.invokeMethodAsync(assembly, identifier) => JS_INVOKES to Widget.AddNumbers.
    DotNet.invokeMethodAsync("JsFixture", "AddNumbers", 1, 2);
    // Instance: objRef.invokeMethodAsync(identifier) => JS_INVOKES to Widget.Notify.
    dotNetRef.invokeMethodAsync("Notify", "ready");
    // Instance with an [JSInvokable("CustomName")] override => JS_INVOKES to Widget.Renamed.
    dotNetRef.invokeMethodAsync("CustomName");
    // Anti-case: no [JSInvokable] method has this identifier => NO edge.
    DotNet.invokeMethodAsync("JsFixture", "NoSuchInvokable");
}
