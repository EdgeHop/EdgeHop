# HttpFixture — expected graph (the Handoff 4 HTTP-pass regression contract)

Audited from an in-process extraction census on 2026-07-18 and pinned by
`HttpFixtureTests`. IDs are always computed through `SymbolIdFormat` over the fixture
compilation — never hard-coded in tests.

Two projects with **no ProjectReference between them** — the tiers meet only over HTTP,
which is exactly the boundary the HTTP pass bridges.

## Totals

**22 nodes / 32 edges** (CONTAINS 21, CALLS 5, HTTP_CALLS 6, REFERENCES 0).

REFERENCES is legitimately zero: every member type is a framework or special type
(`HttpClient`, `List<string>`, `Task<T>`, primitives), and the tiers share no authored
types (no ProjectReference).

## Nodes (Kind | Name | SourceDoc)

| Kind | Name | SourceDoc | notes |
|---|---|---|---|
| Namespace | `HttpFixture` | null | shared root of both project namespaces — ONE node |
| Namespace | `Api` | null | |
| Namespace | `Web` | null | |
| NamedType | `WidgetEndpoints` | `HttpFixture.Api/WidgetEndpoints.cs` | |
| NamedType | `Store` | `HttpFixture.Api/Store.cs` | |
| NamedType | `GadgetsController` | `HttpFixture.Api/GadgetsController.cs` | no explicit ctor → no ctor node |
| NamedType | `WidgetApiClient` | `HttpFixture.Web/WidgetApiClient.cs` | |
| Method | `void WidgetEndpoints.MapWidgetEndpoints(IEndpointRouteBuilder app)` | `HttpFixture.Api/WidgetEndpoints.cs` | `Routes = ["GET /widgets", "GET /widget/{id:int}", "POST /widget", "DELETE /admin/widget/{id}"]` (declaration order, verb-prefixed) |
| Method | `List<string> Store.All()` | `HttpFixture.Api/Store.cs` | |
| Method | `string Store.Get(int id)` | `HttpFixture.Api/Store.cs` | |
| Method | `string Store.Add(string name)` | `HttpFixture.Api/Store.cs` | |
| Method | `bool Store.Remove(int id)` | `HttpFixture.Api/Store.cs` | |
| Method | `string GadgetsController.GetById(int id)` | `HttpFixture.Api/GadgetsController.cs` | `Routes = ["GET /gadget/{id}"]` — class `[Route("gadget")]` composed with `[HttpGet("{id}")]` |
| Method | `WidgetApiClient.WidgetApiClient(HttpClient http)` | `HttpFixture.Web/WidgetApiClient.cs` | primary constructor |
| Method | `Task<List<string>?> WidgetApiClient.GetWidgetsAsync()` | `HttpFixture.Web/WidgetApiClient.cs` | |
| Method | `Task<string?> WidgetApiClient.GetWidgetAsync(int id)` | `HttpFixture.Web/WidgetApiClient.cs` | |
| Method | `Task WidgetApiClient.CreateWidgetAsync(string name)` | `HttpFixture.Web/WidgetApiClient.cs` | |
| Method | `Task WidgetApiClient.DeleteWidgetAsync(int id, bool force)` | `HttpFixture.Web/WidgetApiClient.cs` | |
| Method | `Task<List<string>?> WidgetApiClient.SearchWidgetsAsync(string query)` | `HttpFixture.Web/WidgetApiClient.cs` | |
| Method | `Task<string> WidgetApiClient.GetGadgetAsync(int id)` | `HttpFixture.Web/WidgetApiClient.cs` | |
| Method | `Task WidgetApiClient.RenameWidgetAsync(int id, string name)` | `HttpFixture.Web/WidgetApiClient.cs` | verb-mismatch control — NO HTTP_CALLS edge |
| Method | `Task<string?> WidgetApiClient.GetUnknownAsync()` | `HttpFixture.Web/WidgetApiClient.cs` | unknown-route control — NO HTTP_CALLS edge |

Explicitly absent:
- **No endpoint-lambda nodes** (lambdas are not emittable symbols; HTTP_CALLS anchors on
  the registration method instead).
- **No `Program`/entry-point noise** (both projects are classlibs by design).
- **No REFERENCES edges** and **no cross-tier CALLS/CONTAINS** of any kind — HTTP_CALLS
  is the only edge type crossing the project boundary.

## Edges

### CONTAINS (21)
- ns→ns (2): `HttpFixture`→`Api`, `HttpFixture`→`Web`
- ns→type (4): `Api`→`WidgetEndpoints`, `Api`→`Store`, `Api`→`GadgetsController`,
  `Web`→`WidgetApiClient`
- type→member (15): WidgetEndpoints→{MapWidgetEndpoints}, Store→{All, Get, Add, Remove},
  GadgetsController→{GetById}, WidgetApiClient→{.ctor + 8 client methods}

### CALLS (5)
Lambda bodies attribute to the containing method (the pre-existing CALLS rule):
- `MapWidgetEndpoints` → `Store.All` / `Store.Get` / `Store.Add` / `Store.Remove`
  (4, sourceDoc `HttpFixture.Api/WidgetEndpoints.cs`)
- `GadgetsController.GetById` → `Store.Get` (1, sourceDoc `HttpFixture.Api/GadgetsController.cs`)

### HTTP_CALLS (6) — sourceDoc always `HttpFixture.Web/WidgetApiClient.cs` (the calling document)
| Caller | Target | proves |
|---|---|---|
| `GetWidgetsAsync` | `MapWidgetEndpoints` | literal route (`"/widgets"`) |
| `GetWidgetAsync` | `MapWidgetEndpoints` | interpolation hole matches `{id:int}` constraint segment |
| `CreateWidgetAsync` | `MapWidgetEndpoints` | verb discrimination (POST) |
| `DeleteWidgetAsync` | `MapWidgetEndpoints` | `MapGroup("/admin")` prefix composition + `?force=…` query strip |
| `SearchWidgetsAsync` | `MapWidgetEndpoints` | non-constant concat suffix (`"/widgets" + query`) assumed query string |
| `GetGadgetAsync` | `GadgetsController.GetById` | attribute-routed controller action as the anchor |

Deliberate NON-matches (the negative contract):
- `RenameWidgetAsync` (`PUT $"/widget/{id}"`): no PUT endpoint registered → no edge.
- `GetUnknownAsync` (`GET "/nope"`): no matching route → no edge.
