# EdgeHopExplorer.Js — expected graph (the pure-JS/HTML regression contract)

Audited from an in-process extraction census on 2026-07-18 and pinned by
`EdgeHopExplorerJsTests`. This sample is a **bare directory** — no `.sln`, no `.csproj` — the
anchor for indexing a non-.NET (pure JS/TS + HTML) project by folder:
`edgehop-extract index <this-directory>`. Driven through the **real** pipeline — the Roslyn C#
extractor (which **no-ops**, there being no solution to load) and the oxc JS/TS extractor — merged
by the host's `IndexCommand.BuildDesiredGraph`. Every id carries the `js|` tier tag.

## Totals

**25 nodes / 26 edges** (CONTAINS 19, CALLS 7). The whole graph is the pinned contract — there is
no C# codegen here, so nothing is incidental.

| Node kind | count |
|---|---|
| Method    | 13 |
| Namespace | 6 |
| NamedType | 4 |
| Field     | 2 |

There are no `Property` or `Event` nodes (JS has neither in the oxc model), and — with no C# tier —
no `JS_CALLS` / `JS_INVOKES` interop edges.

## The six modules (Namespace nodes)

| Module | kind | notes |
|---|---|---|
| `catalog.js` | standalone | the feature-catalog data + behavior |
| `render.js` | standalone | DOM rendering helpers |
| `types.ts` | standalone | TypeScript `type` / `interface` / `enum` |
| `index.html#0` | inline `<script>` | the driver page's inline module (sourceDoc `index.html`) |
| `features.html#0` | inline `<script>` | classic inline script (sourceDoc `features.html`) |
| `about.html#0` | inline `<script>` | inline script (sourceDoc `about.html`) |

Inline `<script>` blocks are discovered from `.html` and parsed as their own modules; a
`<script src="…">` reference is skipped (the referenced file is discovered on its own).

## Members (Method / NamedType / Field)

- `catalog.js`: `describeFeature`, `label` (Method); `Catalog` (NamedType) with `size` (Field) and
  `first` (Method); `FEATURES` (Field).
- `render.js`: `renderAll`, `renderItem` (Method).
- `types.ts`: `FeatureName` (type alias), `Feature` (interface), `Area` (enum) — all NamedType;
  `toFeature`, `areaOf` (Method). (Enum *members* are not emitted as separate nodes.)
- `index.html#0`: `boot`, `summarize` (Method).
- `features.html#0`: `track`, `record` (Method).
- `about.html#0`: `greet`, `message` (Method).

## Edges

### CALLS (7) — resolved same-module calls
`describeFeature → label`, `Catalog.first → describeFeature`, `renderAll → renderItem`,
`toFeature → areaOf`, `boot → summarize`, `track → record`, `greet → message`.

JS CALLS resolve by scope/binding **within a file** only — a call across an `import` boundary does
not resolve (oxc does not type-check), which is why every edge above is same-module.

### CONTAINS (19) — module → its declared members (and class → its members)
19 total: `catalog.js` → its 4 members + `Catalog` → its 2 members (6), `render.js` → 2,
`types.ts` → 5, and one per inline module (`index.html#0`, `features.html#0`, `about.html#0`) → 2 each (6).

## Explicitly absent (the skip list — proven by `describeFeature` staying unambiguous)
- `vendor/analytics.min.js` — `*.min.js` is skipped; its duplicate `describeFeature` / `label` /
  `track` exports are never parsed.
- `node_modules/left-pad/index.js` — `node_modules` is skipped wholesale; its `describeFeature` and
  `leftPad` never become nodes.

## Authoring note (HTML-comment handling — regression-guarded)
The oxc inline-`<script>` discovery matches and **skips HTML comments**, so a literal `<script>`
written inside `<!-- ... -->` is treated as prose, not a real tag. `about.html` deliberately includes
such a comment (mentioning a `ghostFn`) as a regression guard: it must produce no module and no
`ghostFn` node — asserted by `EdgeHopExplorerJsTests.Script_mentioned_inside_an_html_comment_is_skipped`.
(A `<script>` inside a JavaScript string, or a Razor `@* … *@` comment, is still not special-cased.)
