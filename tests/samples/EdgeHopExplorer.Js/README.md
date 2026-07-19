# EdgeHop Explorer — JavaScript / HTML edition

A **pure HTML + JavaScript** sample (no `.csproj`, no `.sln`, no build) that renders the same
EdgeHop feature catalog as `EdgeHopExplorer.BlazorServer`, implemented entirely in the browser.
It exists to exercise the native **oxc** JS/TS extractor end-to-end.

## Run it

It is static — open `index.html` in a browser, or serve the folder with any static server
(`npx serve`, `python -m http.server`, …). There is nothing to compile.

## Index it with EdgeHop

Because there is no solution file, EdgeHop indexes it as a **bare directory** — the Roslyn C#
extractor no-ops and the oxc extractor graphs the tree:

```
edgehop-extract index tests/samples/EdgeHopExplorer.Js
edgehop find-symbol describeFeature --json
```

## What it exercises

| Surface | Where |
|---|---|
| HTML pages as the driver | `index.html`, `features.html`, `about.html` |
| JavaScript modules "pulled down" via `<script src>` | `js/catalog.js`, `js/render.js` (external refs are skipped by oxc and discovered as their own files) |
| Inline `<script>` blocks in HTML | one per page — each becomes its own module node (`<page>.html#0`) with its own resolved `CALLS` edge |
| Module (`Namespace`) / function+method (`Method`) / class (`NamedType`) / `const`+field (`Field`) nodes | `js/catalog.js` |
| A resolved same-file `CALLS` edge | `describeFeature → label`, `Catalog.first → describeFeature`, `renderAll → renderItem`, and one per inline script |
| TypeScript `type` / `interface` / `enum` → `NamedType` | `js/types.ts` |
| The skip list (proving the graph stays clean) | `vendor/analytics.min.js` (`*.min.js`) and `node_modules/left-pad/` (`node_modules`) are never parsed |

Exact node/edge counts are pinned in `EXPECTED-GRAPH.md` and asserted by `EdgeHopExplorerJsTests`.
