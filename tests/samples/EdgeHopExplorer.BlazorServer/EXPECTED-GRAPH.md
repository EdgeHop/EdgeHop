# EdgeHopExplorer.BlazorServer — expected graph (the comprehensive regression contract)

Audited from an in-process extraction census on 2026-07-18 and pinned by
`EdgeHopExplorerBlazorTests`. Driven through the **real** pipeline — the Roslyn C#/Razor
extractor (symbol walk + Razor component pass + HTTP pass + JS-interop pass) and the oxc JS/TS
extractor — merged and matched by the host's `IndexCommand.BuildDesiredGraph` in the default
**precise** `EDGEHOP_JS_INTEROP` mode. IDs are computed through `SymbolIdFormat` (C#) / the `js|`
tier tag (JS) over the sample, never hard-coded in tests.

This is the **comprehensive** anchor: unlike the deliberately-tiny fixtures under
`EdgeHop.Tests/fixtures`, this runnable sample exercises **every node kind and every edge type**
from a single Blazor Server tier.

## Totals

**122 nodes / 179 edges.**

| Node kind | count | | Edge type | count |
|---|---|---|---|---|
| Method    | 48 | | CONTAINS   | 118 |
| NamedType | 28 | | REFERENCES | 25 |
| Property  | 21 | | CALLS      | 16 |
| Field     | 15 | | RENDERS    | 4 |
| Namespace | 9  | | HTTP_CALLS | 3 |
| **Event** | **1** | | IMPLEMENTS | 3 |
|           |    | | INHERITS   | 3 |
|           |    | | OVERRIDES  | 3 |
|           |    | | JS_CALLS   | 2 |
|           |    | | JS_INVOKES | 2 |

> **What is pinned, and what is SDK-sensitive.** The **semantic contract** — the interop edges
> (JS_CALLS / JS_INVOKES), HTTP_CALLS, the type-hierarchy edges (IMPLEMENTS / INHERITS / OVERRIDES),
> RENDERS, the six node kinds (the `Event` node in particular), and the `routes` / `isComponent`
> properties — is the intent this sample exists to protect. The **bulk counts** (`CONTAINS`,
> `NamedType`, `Method`, `Property`) additionally include Razor codegen: each `.razor` compiles to a
> generated component class with a `BuildRenderTree` method, and inferred-generic tags add a
> `TypeInference` helper type. Those counts are deterministic for a fixed SDK (pinned here at
> **.NET 10.0.302**) but a Razor/SDK-codegen change can shift them — and per the CLAUDE.md rule a
> shift is a deliberate semantics change to re-audit and re-pin here, never to paper over.

## The six node kinds

| Kind | representative source |
|---|---|
| Namespace | `EdgeHopExplorer.BlazorServer.Domain` (and each JS module — `explorer.js`, the inline `App.razor#N` script) |
| NamedType | interfaces (`IFeature`, `ISearchable`, `IFeatureCatalog`), the abstract `FeatureBase`, the three concrete features, the generic `Registry<T>`, records (`FeatureInfo`, `SearchRequest`), the `FeatureRegisteredHandler` delegate, the `FeatureArea` enum, the JS class `Explorer`, and every Razor component |
| Method | constructors, `Describe` / `Prefix`, endpoint + API-client methods, `[JSInvokable]` `Ping` / `OnJsEvent`, JS functions |
| Property | `IFeature.Name` / `Area`, `[Parameter]` component properties, the record's explicit `Summary`, injected properties |
| Field | `enum` members, static readonly arrays, private fields, the JS `const` / class field |
| **Event** | `FeatureCatalog.Registered` — the node kind none of the older fixtures exercised |

## The ten edge types (the semantic contract)

### Type hierarchy — IMPLEMENTS (3) / INHERITS (3) / OVERRIDES (3)
- IMPLEMENTS: `FeatureBase → IFeature`, `NodeKindsFeature → ISearchable` (its second, directly-listed
  interface — the `IFeature` reached only through the base class produces no second edge),
  `FeatureCatalog → IFeatureCatalog`.
- INHERITS: `NodeKindsFeature`, `EdgeTypesFeature`, `QueryToolsFeature` → `FeatureBase`.
- OVERRIDES: each concrete feature's `Describe()` → `FeatureBase.Describe()`.

### RENDERS (4) — component → source-declared child
`App → Routes`, `Home → FeatureCard`, `Home → TypedList<T>`, `Features → TypedList<T>`. The
`TypedList<T>` edges come from inferred-generic `<TypedList Items="…"/>` tags resolved through the
Razor `TypeInference` helper. Framework components (`Router`, `HeadOutlet`, …) get no edge, and
routable pages are reached by the Router via `routes`, never by a RENDERS edge into a page.

### Routes / isComponent
Exactly seven source components are flagged `isComponent`: `App`, `Routes`, `Home`, `FeatureCard`,
`TypedList<T>`, `About`, `Features`. Page routes: `Home` = `["/"]`, `Features` = `["/features"]`,
`About` = `["/about", "/info"]` (a multi-route page). The `FeatureEndpoints.MapFeatureEndpoints`
method carries the verb-prefixed HTTP routes `["GET /api/features/all", "GET /api/features/{name}",
"POST /api/features/search"]`.

### HTTP_CALLS (3) — Web → API, matched by verb + route shape
All three verb-named `HttpClient` calls in `FeatureApiClient` (`GetAllAsync`, `GetAsync`,
`SearchAsync`) resolve to the single `MapFeatureEndpoints` registration method — the cross-tier
boundary a compile-time edge cannot cross (both tiers happen to live in one project here). Because
`get_callers` walks HTTP_CALLS with CALLS, `Features.OnInitializedAsync → GetAllAsync → (HTTP_CALLS)
→ MapFeatureEndpoints` is one traversal.

### JS_CALLS (2) — C# → JS
Both originate in `Home.Refresh()`: a **module-correlated** match to `explorer.js#highlight` and to
`explorer.js#wireInterop` (the `_module` receiver was bound by `import("./js/explorer.js")`). The
edge's `sourceDoc` is the C# calling document, `Components/Pages/Home.razor`.

### JS_INVOKES (2) — JS → C#
Both originate in `explorer.js#wireInterop`, calling back into the component: the **static**
`DotNet.invokeMethodAsync("EdgeHopExplorer.BlazorServer", "Ping", …)` → `[JSInvokable] Home.Ping`
(matched by assembly + identifier) and the **instance** `dotNetRef.invokeMethodAsync("OnJsEvent", …)`
→ `[JSInvokable] Home.OnJsEvent` (matched by unique identifier). The edge's `sourceDoc` is the JS
calling document, `wwwroot/js/explorer.js`.

### CALLS (16) / CONTAINS (118) / REFERENCES (25)
Ordinary resolved calls (e.g. each override → `FeatureBase.Prefix`, the constructor → `Register` →
`Registry<T>.Add`, Razor handler bindings, `@Feature.Describe()` in markup), containment, and type
references (method/field/property/event signatures naming an authored type; generic arguments are
walked, so `List<FeatureInfo>` references `FeatureInfo`; each enum member references its own enum).

## What is deliberately INVISIBLE
- `ReflectionFeatureLoader.DiscoverAll` has **zero** outgoing CALLS: reflective dispatch
  (`Activator.CreateInstance`, `Assembly.GetTypes`) resolves to framework methods, which are never
  nodes — a live demonstration that the graph captures compile-time structure, not runtime
  reflection.
- The external `<script src="…">` tags in `App.razor` (blazor.web.js, and the `src`-referenced
  explorer.js) are skipped by oxc; `explorer.js` is discovered and parsed as its own module instead.
- No node points at a generated `*_razor.g.cs` or `obj/` document — every Razor symbol is remapped
  to its authored `.razor` file.
