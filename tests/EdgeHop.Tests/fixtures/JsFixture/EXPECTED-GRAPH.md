# JsFixture — expected graph (the bidirectional JS interop regression contract)

Audited from an in-process extraction census on 2026-07-18 and pinned by `JsFixtureTests`.
Driven through the **real** pipeline — the Roslyn extractor (C# call sites + `[JSInvokable]`
targets) and the oxc extractor (JS exports + `DotNet.invoke*` call sites), merged and matched by
the host's `IndexCommand.BuildDesiredGraph` in the default **precise** `EDGEHOP_JS_INTEROP`
mode. IDs are computed through `SymbolIdFormat` (C#) / the `js|` tier tag (JS) over the fixture —
never hard-coded in tests.

One Blazor Razor project (`Widget.razor`) with **no compile-time link** to its JavaScript — the
tiers meet only through interop, in both directions: C# calls JS via `IJSRuntime`/
`IJSObjectReference` (`JS_CALLS`), and JS calls C# via `DotNet.invoke*` into `[JSInvokable]`
methods (`JS_INVOKES`).

## Totals

**21 nodes / 25 edges** (CONTAINS 18, CALLS 2, JS_CALLS 2, JS_INVOKES 3) as of the pinned SDK.
The **JS side and the interop edges are the asserted contract**; the C# node/edge totals are
incidental Blazor Razor codegen and are documented, not pinned (they can shift by an SDK patch).

## JS nodes (6) — every one carries the mandatory `js|` tier tag

| Kind | Name | SourceDoc | notes |
|---|---|---|---|
| Namespace | `widget.js` | `wwwroot/js/widget.js` | the module container |
| Method | `getWidget` | `wwwroot/js/widget.js` | **exported** → `JS_CALLS` target |
| Method | `format` | `wwwroot/js/widget.js` | module-private helper — a node, but NOT an export (so an `InvokeAsync("format")` would get no edge) |
| Method | `wireCallbacks` | `wwwroot/js/widget.js` | **exported** → the `JS_INVOKES` **source** (calls `DotNet.invoke*`) |
| Namespace | `site.js` | `wwwroot/js/site.js` | the module container |
| Method | `showAlert` | `wwwroot/js/site.js` | **exported** → `JS_CALLS` target |

Explicitly absent (discovery skips them — proven by `showAlert` staying unambiguous):
- `wwwroot/js/vendor.min.js` — `*.min.js` is skipped; its duplicate `showAlert` export is never parsed.
- `wwwroot/lib/node_modules/dup/index.js` — `node_modules` is skipped wholesale.

## C# nodes (15) — standard Razor-component extraction (not pinned)

`JsFixture` (Namespace) → `Widget` component (NamedType, + the Razor-generated helper type),
its methods `OnAfterRenderAsync` / `Refresh` / `DisposeAsync` and the three `[JSInvokable]`
targets `AddNumbers` (static) / `Notify` / `Renamed` (+ generated members), fields
`_module` / `_result` / `_dynamicName`, and the `@inject IJSRuntime JS` property.

## Edges

### JS-internal (oxc): CONTAINS + CALLS
- CONTAINS: `widget.js`→{`getWidget`, `format`, `wireCallbacks`}, `site.js`→`showAlert`.
- CALLS: `getWidget` → `format` (a resolved same-module call).

### JS_CALLS (2) — C#→JS; sourceDoc `Widget.razor` (the C# calling document, not the JS file)

Both originate from `Widget.Refresh()`:

| Caller | Target | proves |
|---|---|---|
| `Task Widget.Refresh()` | `getWidget` (`widget.js`) | **module-correlated precise match**: `_module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/widget.js")` binds `_module`'s later `InvokeAsync("getWidget")` to `widget.js` (leaf-matched) |
| `Task Widget.Refresh()` | `showAlert` (`site.js`) | **global unique-name precise match**: `JS.InvokeVoidAsync("showAlert")` has no module, so it matches only because exactly one *discovered* export is named `showAlert` |

### JS_INVOKES (3) — JS→C#; sourceDoc `wwwroot/js/widget.js` (the JS calling document)

All originate from `wireCallbacks` (`widget.js`), targeting the C# `[JSInvokable]` methods:

| Caller | Target | proves |
|---|---|---|
| `wireCallbacks` (`widget.js`) | `int Widget.AddNumbers(int, int)` | **static precise match** on `(assembly "JsFixture", identifier "AddNumbers")` — `DotNet.invokeMethodAsync("JsFixture", "AddNumbers", …)` |
| `wireCallbacks` (`widget.js`) | `void Widget.Notify(string)` | **instance unique-identifier match**: `dotNetRef.invokeMethodAsync("Notify", …)`, no assembly knowable from JS |
| `wireCallbacks` (`widget.js`) | `void Widget.Renamed()` | **identifier override**: `[JSInvokable("CustomName")]` means JS's `invokeMethodAsync("CustomName")` matches the method named `Renamed` |

### The negative contract (anti-cases — each must produce NO interop edge)

C#→JS (`JS_CALLS`), calls in `Refresh()`:

| Call | why no edge |
|---|---|
| `_module.InvokeVoidAsync("noSuchFunction")` | no JS export of that name |
| `JS.InvokeVoidAsync(_dynamicName)` | identifier is a non-constant field — not statically knowable |
| `InvokeAsync(StateHasChanged)` | `ComponentBase.InvokeAsync` (the render dispatcher) is in `Microsoft.AspNetCore.Components`, not `Microsoft.JSInterop` — not JS interop at all |
| `"showAlert"` vs the skipped `vendor.min.js`/`node_modules` duplicates | if either were parsed, `showAlert` would be ambiguous and precise would (correctly) drop even the real edge — so the surviving edge proves the skips |

JS→C# (`JS_INVOKES`), calls in `wireCallbacks`:

| Call | why no edge |
|---|---|
| `DotNet.invokeMethodAsync("JsFixture", "NoSuchInvokable")` | no `[JSInvokable]` method carries that identifier |

## Modes

Precise (the default, above) emits **2** `JS_CALLS` + **3** `JS_INVOKES`.
`EDGEHOP_JS_INTEROP=broad` would additionally fan name-only calls out to every same-named
target; `=off` disables both passes entirely. Only precise is pinned here.
