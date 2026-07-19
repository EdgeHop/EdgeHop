# JsFolderFixture — expected graph (the directory-index regression contract)

Audited from an in-process extraction census on 2026-07-18 and pinned by `JsFolderFixtureTests`.
This fixture is a **bare directory** — no `.sln`, no `.csproj` — the anchor for indexing a
non-.NET (pure JS/TS) project by folder: `edgehop-extract index <this-directory>`. Driven
through the **real** pipeline — the Roslyn C# extractor (which **no-ops**, there being no
solution to load) and the oxc JS/TS extractor — merged by the host's
`IndexCommand.BuildDesiredGraph`. IDs are computed through the `js|` tier tag over the fixture,
never hard-coded in tests.

## Totals

**5 nodes / 4 edges** (CONTAINS 3, CALLS 1). The whole graph is the pinned contract — there is
no incidental C# codegen here, unlike the mixed `JsFixture`.

## JS nodes (5) — every one carries the mandatory `js|` tier tag

| Kind | Name | SourceDoc | notes |
|---|---|---|---|
| Namespace | `app.js` | `app.js` | the module container |
| Method | `greet` | `app.js` | **exported**; calls the module-private `decorate` |
| Method | `decorate` | `app.js` | module-private helper — a node, but NOT an export |
| Namespace | `util.js` | `util.js` | the module container |
| Method | `shout` | `util.js` | **exported** |

Explicitly absent (discovery skips them — proven by `greet`/`shout` staying unambiguous):
- `vendor.min.js` — `*.min.js` is skipped; its duplicate `greet`/`shout` exports are never parsed.
- `node_modules/dup/index.js` — `node_modules` is skipped wholesale.

## Edges (4)

### CONTAINS (3) — module → its declared members
- `app.js` → `greet`
- `app.js` → `decorate`
- `util.js` → `shout`

### CALLS (1) — resolved JS-internal call
- `greet` → `decorate` (a same-module call)

There are **no** `JS_CALLS` / `JS_INVOKES` edges: with no C# tier there is no `IJSRuntime` call
site and no `[JSInvokable]` target, so the interop passes derive nothing (the merged result is
the oxc graph unchanged).

## Why this exists

The Roslyn extractor returns an empty result with load description `roslyn (no solution)` for a
directory target, so a pure JS/TS tree indexes cleanly with the JS extractor alone. Once indexed,
the graph is queried exactly like any other: point `EDGEHOP_REPO` (MCP server) or the working
directory (`edgehop` CLI) at this folder — no `.sln` anywhere in the flow.
