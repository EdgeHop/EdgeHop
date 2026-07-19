# TinyFixture — expected graph (Phase 2 assertion contract)

This document exactly enumerates the `:Symbol` nodes and edges the extractor must
produce when pointed at `TinyFixture.sln` (README Phase 2, step 4). The Phase 2
assertion test encodes this contract verbatim: **19 nodes, 28 edges**, broken down
below. Every node/edge is written with `branch = "main"`.

> **Revision note (Phase 2 test author):** reconciled to the extraction contract —
> the `name` column now uses the prescribed
> `SymbolDisplayFormat.MinimallyQualifiedFormat` rendering (previously it listed the
> simple metadata names), and the `sourceDoc` notes are pinned to the contract:
> namespace nodes carry `sourceDoc = null`, partial types use the
> alphabetically-first (ordinal) declaring document, and paths are relative to the
> solution directory with forward slashes.

> **Revision note (Phase 2 fixer):** the fixture gained `Sub/Decorator.cs` (15 → 19
> nodes, 21 → 28 edges) to close three verified coverage gaps:
> 1. it lives in a **subfolder**, so the solution-relative + forward-slash `sourceDoc`
>    convention is asserted non-vacuously (`Sub/Decorator.cs`, previously every doc was
>    a bare file name and a backslash regression could not fail any test);
> 2. it declares a **C# 12 primary constructor** — the regression anchor for the
>    extractor rule that a primary constructor's CALLS walk is restricted to its own
>    declaration regions (a naive walk of the declaring `ClassDeclarationSyntax`
>    fabricates CALLS edges from the ctor to every method invoked in the type);
> 3. it declares an **auto-property**, so the accessor/backing-field exclusion and the
>    Property node + property-type REFERENCES paths are actually exercised.

## Extraction rules applied (from README)

1. **Source-declared symbols only.** Nodes are emitted only for symbols declared in
   TinyFixture source. No nodes for `object`, `string`, `int`, or any other
   metadata/framework symbol.
2. **Implicit/compiler-generated members are NOT emitted.** The default constructors of
   `Greeter`, `LoudGreeter`, `Caller`, `App`, and `PartialThing` are compiler-generated
   (`IMethodSymbol.IsImplicitlyDeclared == true`) and are not source-declared symbols.
   Only source-declared symbols appear in the graph. The ONE constructor node in this
   fixture is `Decorator`'s **primary constructor** (`class Decorator(Greeter inner)`),
   which Roslyn reports as source-declared and non-implicit — it is a source-written
   constructor and gets a Method node. Likewise `Decorator.Inner` produces exactly one
   `Property` node: its `get` accessor (`MethodKind.PropertyGet`) and its
   compiler-generated backing field are **never** emitted.
3. **Edges to metadata/framework targets are SKIPPED.** Examples in this fixture that
   must NOT produce edges:
   - `INHERITS` from `Greeter`, `Caller`, `App`, `PartialThing` to `System.Object`
     (framework base type — skipped).
   - `REFERENCES` to `string` / `int` from any parameter, return, or field type — skipped.
   - `CALLS` to `string.ToUpper()` in `LoudGreeter.Greet` — framework target, skipped.
4. **`CALLS` comes from `InvocationExpressionSyntax` only.** Object-creation expressions
   are not invocations: `new Greeter()` (in `Caller`'s field initializer) and
   `new Caller()` (in `App.Run`) resolve to *constructor* symbols — here the implicit
   default constructors, which are not emitted as nodes (rule 2) — and produce **no**
   `CALLS` edge. In particular, `new Caller().CallGreet()` yields exactly ONE `CALLS`
   edge (`App.Run -> Caller.CallGreet`), not two. The string concatenation
   `"Hello, " + name` in `Greeter.Greet` is a binary operator, not an invocation — no edge.
5. **`IMPLEMENTS` comes from `INamedTypeSymbol.Interfaces`** (interfaces named directly
   in the type's base list). `LoudGreeter` implements `IGreeter` only *transitively*
   through `Greeter`, so `LoudGreeter` has **no** `IMPLEMENTS` edge. Method-level
   interface implementation (`Greeter.Greet` implementing `IGreeter.Greet`) produces
   **no** edge in Phase 1–3 (the edge set has no method-level implements; `OVERRIDES`
   applies only to `override` members, and `IMethodSymbol.OverriddenMethod` is null for
   an interface implementation).
6. **`OriginalDefinition` normalization** applies to every emitted symbol/edge endpoint.
   The fixture has no generics, so this is trivially satisfied (identity mapping).
7. **Partial types produce ONE node.** `PartialThing` is declared in
   `PartialThing.Part1.cs` and `PartialThing.Part2.cs`, but Roslyn yields a single
   `INamedTypeSymbol`, so `SymbolIdFormat.GetId` yields a single stable ID and the
   upsert produces exactly one `:Symbol` node. This is the regression anchor for
   single-node identity of partial types.
8. **A primary constructor's CALLS walk is restricted to its own declaration.** The
   declaring syntax of `Decorator`'s primary constructor is the whole
   `ClassDeclarationSyntax`, so only the parameter list (default values) and a
   base-initializer argument list (`: Base(...)`) may contribute CALLS edges from the
   constructor — member bodies never do. Here neither region invokes anything, so the
   primary constructor has **zero** outgoing CALLS edges; in particular, the
   `Inner.Greet("deco")` invocation in `Decorate` is attributed to
   `Decorator.Decorate` ONLY. (Records flow through the same
   `TypeDeclarationSyntax` code path.)

## Expected `:Symbol` nodes — 19 total

By kind: 1 `Namespace`, 7 `NamedType`, 9 `Method`, 1 `Field`, 1 `Property`. Exactly
one constructor node (`Decorator`'s source-written primary constructor — every other
constructor in the fixture is compiler-generated and excluded), exactly one property
node (`Decorator.Inner` — zero accessor nodes, zero backing-field nodes), zero events.

The authoritative match key for the assertion test is the stable `id` produced by
`SymbolIdFormat.GetId(symbol)` (Phase 1 — do not hard-code ID strings here; compute
them through the tested formatter). `name` is
`symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)` per the
extraction contract — for members that rendering includes the return/field type, the
containing type, and parameter types *and names*, e.g.
`string Greeter.Greet(string name)`. The name column below uses exactly that
rendering (overloads disambiguate via `id`, not `name`).

| #  | kind      | name           | identity (qualified signature)                          | declared in |
|----|-----------|----------------|---------------------------------------------------------|-------------|
| 1  | Namespace | TinyFixture    | `TinyFixture`                                           | (all files) |
| 2  | NamedType | IGreeter       | `TinyFixture.IGreeter` (interface)                      | IGreeter.cs |
| 3  | NamedType | Greeter        | `TinyFixture.Greeter` (class)                           | Greeter.cs  |
| 4  | NamedType | LoudGreeter    | `TinyFixture.LoudGreeter` (class)                       | LoudGreeter.cs |
| 5  | NamedType | Caller         | `TinyFixture.Caller` (class)                            | Caller.cs   |
| 6  | NamedType | App            | `TinyFixture.App` (class)                               | App.cs      |
| 7  | NamedType | PartialThing   | `TinyFixture.PartialThing` (partial class — ONE node)   | PartialThing.Part1.cs + PartialThing.Part2.cs |
| 8  | Method    | string IGreeter.Greet(string name)    | `string TinyFixture.IGreeter.Greet(string)`             | IGreeter.cs |
| 9  | Method    | string Greeter.Greet(string name)     | `string TinyFixture.Greeter.Greet(string)` (virtual)    | Greeter.cs  |
| 10 | Method    | string LoudGreeter.Greet(string name) | `string TinyFixture.LoudGreeter.Greet(string)` (override) | LoudGreeter.cs |
| 11 | Method    | string Caller.CallGreet()             | `string TinyFixture.Caller.CallGreet()`                 | Caller.cs   |
| 12 | Field     | Greeter Caller._greeter               | `TinyFixture.Greeter TinyFixture.Caller._greeter`       | Caller.cs   |
| 13 | Method    | string App.Run()                      | `string TinyFixture.App.Run()`                          | App.cs      |
| 14 | Method    | int PartialThing.PartOneValue()       | `int TinyFixture.PartialThing.PartOneValue()`           | PartialThing.Part1.cs |
| 15 | Method    | int PartialThing.PartTwoValue()       | `int TinyFixture.PartialThing.PartTwoValue()`           | PartialThing.Part2.cs |
| 16 | NamedType | Decorator                             | `TinyFixture.Decorator` (class, C# 12 primary ctor)     | Sub/Decorator.cs |
| 17 | Method    | Decorator.Decorator(Greeter inner)    | `TinyFixture.Decorator.Decorator(TinyFixture.Greeter)` (primary ctor) | Sub/Decorator.cs |
| 18 | Property  | Greeter Decorator.Inner               | `TinyFixture.Greeter TinyFixture.Decorator.Inner`       | Sub/Decorator.cs |
| 19 | Method    | string Decorator.Decorate()           | `string TinyFixture.Decorator.Decorate()`               | Sub/Decorator.cs |

Note on `sourceDoc`: paths are relative to the solution directory with forward
slashes. Most sources sit beside `TinyFixture.sln`, so their docs are bare file names
(e.g. `Greeter.cs`); `Decorator.cs` deliberately lives in the `Sub/` subfolder so its
doc — `Sub/Decorator.cs` — makes the relative/forward-slash convention assertions
non-vacuous. For every node except #1 and #7 it is the single declaring document. Per
the extraction contract the namespace node (#1) has `sourceDoc = null`, and the
partial type (#7) uses the alphabetically-first (ordinal) declaring document — the
assertion test asserts null for #1 and, for #7, only that the value is one of the two
declaring documents (the exact pick is not pinned).

## Expected edges — 28 total

| type       | count |
|------------|-------|
| CONTAINS   | 18    |
| CALLS      | 4     |
| IMPLEMENTS | 1     |
| INHERITS   | 1     |
| OVERRIDES  | 1     |
| REFERENCES | 3     |

### CONTAINS (18)

Namespace → type (7):

1. `TinyFixture` -> CONTAINS -> `IGreeter`
2. `TinyFixture` -> CONTAINS -> `Greeter`
3. `TinyFixture` -> CONTAINS -> `LoudGreeter`
4. `TinyFixture` -> CONTAINS -> `Caller`
5. `TinyFixture` -> CONTAINS -> `App`
6. `TinyFixture` -> CONTAINS -> `PartialThing`
7. `TinyFixture` -> CONTAINS -> `Decorator`

Type → member (11):

8.  `IGreeter` -> CONTAINS -> `IGreeter.Greet(string)`
9.  `Greeter` -> CONTAINS -> `Greeter.Greet(string)`
10. `LoudGreeter` -> CONTAINS -> `LoudGreeter.Greet(string)`
11. `Caller` -> CONTAINS -> `Caller.CallGreet()`
12. `Caller` -> CONTAINS -> `Caller._greeter`
13. `App` -> CONTAINS -> `App.Run()`
14. `PartialThing` -> CONTAINS -> `PartialThing.PartOneValue()`
15. `PartialThing` -> CONTAINS -> `PartialThing.PartTwoValue()`
16. `Decorator` -> CONTAINS -> `Decorator.Decorator(Greeter)` (primary ctor)
17. `Decorator` -> CONTAINS -> `Decorator.Inner`
18. `Decorator` -> CONTAINS -> `Decorator.Decorate()`

### CALLS (4)

1. `LoudGreeter.Greet(string)` -> CALLS -> `Greeter.Greet(string)`
   (the `base.Greet(name)` invocation; the chained `.ToUpper()` targets
   `string.ToUpper` — framework, skipped)
2. `Caller.CallGreet()` -> CALLS -> `Greeter.Greet(string)`
   (`_greeter.Greet("x")`; static receiver type is `Greeter`, so the semantic model
   resolves to `Greeter.Greet` — the virtual slot, not any override)
3. `App.Run()` -> CALLS -> `Caller.CallGreet()`
   (`new Caller().CallGreet()`; the `new Caller()` part resolves to the implicit
   constructor symbol, NOT to `CallGreet` — no edge for it, per rules 2 and 4)
4. `Decorator.Decorate()` -> CALLS -> `Greeter.Greet(string)`
   (`Inner.Greet("deco")`; the property read is not an invocation. Per rule 8 this
   edge is attributed to `Decorate` ONLY — `Decorator`'s primary constructor has
   ZERO outgoing CALLS edges even though its declaring syntax is the whole class)

### IMPLEMENTS (1)

1. `Greeter` -> IMPLEMENTS -> `IGreeter`
   (`LoudGreeter` gets NO IMPLEMENTS edge — `IGreeter` is not in its direct base list)

### INHERITS (1)

1. `LoudGreeter` -> INHERITS -> `Greeter`
   (all other classes inherit `System.Object` — framework target, skipped;
   `IGreeter` has no base type)

### OVERRIDES (1)

1. `LoudGreeter.Greet(string)` -> OVERRIDES -> `Greeter.Greet(string)`
   (`Greeter.Greet` does NOT override `IGreeter.Greet` — interface implementation is
   not an override; `OverriddenMethod` is null there)

### REFERENCES (3)

1. `Caller._greeter` -> REFERENCES -> `Greeter` (field type)
2. `Decorator.Decorator(Greeter)` -> REFERENCES -> `Greeter` (primary-ctor parameter type)
3. `Decorator.Inner` -> REFERENCES -> `Greeter` (property type — carried by the
   Property node itself, never by an accessor)

(every other parameter/return/field/property type in the fixture is `string` or
`int` — framework, skipped)

## Phase 3 `get_callers` expectations (fixture-derived)

Target symbol: `Greeter.Greet(string)`.

- depth 1 → exactly 3 callers: `LoudGreeter.Greet(string)`, `Caller.CallGreet()`,
  `Decorator.Decorate()`
- depth 2 → exactly 4 callers: the three above plus `App.Run()`
  (transitive chain `App.Run -> Caller.CallGreet -> Greeter.Greet`)

`find_symbol("Greet")` must return the three `Greet` methods (distinct ids, each
`name` containing `Greet`), proving name-collision handling.
