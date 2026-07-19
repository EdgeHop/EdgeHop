# BlazorFixture — expected graph (the Phase 4 regression contract)

Audited from a `edgehop-extract --dry-run` census on 2026-07-17 and pinned by
`BlazorFixtureTests`. IDs are always computed through `SymbolIdFormat` over the fixture
compilation — never hard-coded in tests.

## Totals

**19 nodes / 22 edges** (CONTAINS 18, CALLS 2, RENDERS 2, REFERENCES 0).

REFERENCES is legitimately zero: every member type in the fixture is a framework or
special type (`EventCallback`, `List<T>`, `int`, `Task`), and type parameters never
produce REFERENCES edges.

## Nodes (Kind | Name | SourceDoc)

| Kind | Name | SourceDoc | notes |
|---|---|---|---|
| Namespace | `BlazorFixture` | null | |
| Namespace | `Pages` | null | |
| Namespace | `Shared` | null | |
| NamedType | `Home` | `Pages/Home.razor` | `IsComponent`, `Routes = ["/"]` |
| NamedType | `Multi` | `Pages/Multi.razor` | `IsComponent`, `Routes = ["/multi", "/multi/{Id:int}"]` (declaration order) |
| NamedType | `Child` | `Shared/Child.razor` | `IsComponent`; manual partial — type doc is the `.razor` half (`"Child.razor" < "Child.razor.cs"` ordinal) |
| NamedType | `TypedList<T>` | `Shared/TypedList.razor` | `IsComponent` (generic, `@typeparam T`) |
| NamedType | `_Imports` | `_Imports.razor` | NOT a component — the generator's class for the imports file. Kept: it maps to a real authored document. |
| Method | `void Home.BuildRenderTree(RenderTreeBuilder __builder)` | `Pages/Home.razor` | generated body, remapped doc |
| Method | `Task Home.HandleClick()` | `Pages/Home.razor` | `@code` member |
| Method | `void Multi.BuildRenderTree(RenderTreeBuilder __builder)` | `Pages/Multi.razor` | |
| Method | `void Child.BuildRenderTree(RenderTreeBuilder __builder)` | `Shared/Child.razor` | |
| Method | `string Child.Label()` | `Shared/Child.razor.cs` | member keeps its own declaring doc |
| Method | `void TypedList<T>.BuildRenderTree(RenderTreeBuilder __builder)` | `Shared/TypedList.razor` | |
| Method | `void _Imports.Execute()` | `_Imports.razor` | |
| Field | `List<int> Home._items` | `Pages/Home.razor` | |
| Property | `EventCallback Child.OnPing` | `Shared/Child.razor` | |
| Property | `int Multi.Id` | `Pages/Multi.razor` | |
| Property | `List<T>? TypedList<T>.Items` | `Shared/TypedList.razor` | |

Explicitly absent:
- **No `__Blazor.*` / `TypeInference` plumbing** (namespace suppressed).
- **No `PageTitle`** (framework component → metadata → never a node).
- **No `Microsoft.CodeAnalysis.EmbeddedAttribute`** (SDK-injected build artifact under
  `obj/` → excluded by the authored-tree rule).
- **No node with an `obj/` or `_razor.g.cs` SourceDoc anywhere.**

## Edges

### CONTAINS (18)
- ns→ns (2): `BlazorFixture`→`Pages`, `BlazorFixture`→`Shared`
- ns→type (5): `Pages`→`Home`, `Pages`→`Multi`, `Shared`→`Child`, `Shared`→`TypedList<T>`, `BlazorFixture`→`_Imports`
- type→member (11): Home→{BuildRenderTree, HandleClick, `_items`}, Multi→{BuildRenderTree, Id}, Child→{BuildRenderTree, OnPing, Label}, TypedList→{BuildRenderTree, Items}, `_Imports`→{Execute}

### RENDERS (2) — sourceDoc `Pages/Home.razor`
- `Home` → `Child` (plain component tag → `OpenComponent<Child>`)
- `Home` → `TypedList<T>` (inferred-generic tag → `TypeInference.CreateTypedList_0` helper body → `OpenComponent<TypedList<T>>`, collapsed to the definition)

### CALLS (2)
- `Home.BuildRenderTree` → `Home.HandleClick`, sourceDoc `Pages/Home.razor` — the
  handler-binding edge; BOTH binding shapes (`<button @onclick="HandleClick">` element
  attribute AND `<Child OnPing="HandleClick"/>` component parameter) collapse to this
  ONE deduped edge.
- `Child.BuildRenderTree` → `Child.Label`, sourceDoc `Shared/Child.razor` — the markup
  expression `@Label()` is an ordinary invocation in the generated tree, captured by the
  pre-existing CALLS pass (proves both passes compose).
