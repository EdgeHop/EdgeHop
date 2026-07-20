# EdgeHop — Roslyn + oxc → pluggable code graph for Claude Code

**Status: feature-complete through bidirectional JS interop (Handoff 6).** The core pipeline
(C# extractor → graph store → MCP query server), the Blazor component graph, the reconcile
engine with git-branch detection, the indexer verbs, watch mode, worktree-based
other-branch indexing, a pluggable graph backend — an embedded SQLite store (the default)
or Neo4j, selected by `EDGEHOP_BACKEND` (see "Backends" below; the LiteGraph candidate
was evaluated and rejected at Gate 0), cross-tier `HTTP_CALLS` edges with git-hook
re-indexing, and a five-tool query surface. **Handoff 6** made both the stores and the
*extractors* pluggable reflected assemblies (under `Backends/` and `Extractors/`), added a
native **oxc**-based JS/TS extractor (no Node.js runtime), and derived cross-tier C#↔JS
interop edges in **both** directions — `JS_CALLS` (C#→JS) and `JS_INVOKES` (JS→C#). The
sections below document each capability; the stage-by-stage history is preserved for context.

Target: **.NET 10 / ASP.NET Core 10** (Windows, PowerShell, Visual Studio + Claude Code).

---

## What this is

A code graph of a .NET solution in a pluggable store (embedded SQLite by default, or
Neo4j), built from Roslyn's semantic model (not Tree-sitter) and — for the front-end tier —
a native oxc parse of the solution's JavaScript/TypeScript. Nodes are C# and JS symbols;
edges are the resolved relationships between them (calls, implementations, references,
containment, and the cross-tier HTTP/JS-interop bridges). Claude Code queries the graph
through an MCP server instead of grepping source files — giving it accurate call-chain and
change-impact answers, including across the C#↔JS boundary.

The design deliberately replaces the single monolithic `graph.json` model (used by
tools like Graphify) with an indexed, durable, incrementally-updatable graph database.
Phases 1–3 prove the C# extraction → storage → query loop end to end. Everything
version-sensitive (Razor markup extraction, live branch sync) comes later, once this
foundation is solid.

---

## Installation & distribution

EdgeHop ships as a **single .NET global tool**, package id `EdgeHop`. It bundles all three
heads — the indexer, the query CLI, and the MCP server — behind one `edgehop` command
(.NET tool packages allow only one command per package, so the command is a thin dispatcher,
`EdgeHop.Tool`, over the existing entry assemblies).

```powershell
# from nuget.org / a private feed, or a local folder feed:
dotnet tool install -g EdgeHop
dotnet tool update  -g EdgeHop     # later, to upgrade

edgehop index <sln|dir> [--branch b] [--watch]   # was: edgehop-extract index
edgehop find-symbol <query> [--json]             # + get-callers / get-relationships / get-path / stats
edgehop mcp                                       # the stdio MCP server Claude Code launches
edgehop --version
```

**Prerequisites on the target machine:** the **.NET 10 SDK/runtime**, and **Windows x64** —
the JS/TS `edgehop-oxc` binary is a vendored win-x64 native, so the package runs only there.
The default SQLite backend is embedded and credential-free, so nothing else is needed;
Neo4j stays opt-in via `EDGEHOP_BACKEND=neo4j` + `NEO4J_*`.

The package embeds everything the reflection loaders need: the `edgehop-oxc` native binary,
all four extractor/backend plugins (`EdgeHop.Roslyn` / `EdgeHop.Oxc` / `EdgeHop.Sqlite` /
`EdgeHop.Neo4j`), and the Roslyn MSBuild BuildHost. The three standalone exes
(`edgehop.exe`, `edgehop-extract.exe`, `EdgeHop.Mcp.exe`) still build for the test suite,
which drives them directly.

**Wiring the MCP server** once installed — point `.mcp.json` at the tool command:

```json
{ "mcpServers": { "edgehop": {
    "command": "edgehop", "args": ["mcp"],
    "env": { "EDGEHOP_REPO": "C:\\path\\to\\your\\solution" } } } }
```

(Before a global install, `"command": "dnx", "args": ["edgehop", "mcp"]` resolves the tool
on demand from a feed.)

**Versioning** is [Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning)
(build-time only, `PrivateAssets=all`): the version comes from git height + `version.json`
(base `0.1-alpha`; `main`/`master` are the public-release refs). Local builds carry a
`-gXXXX` commit suffix; a CI build with `-p:PublicRelease=true` off a release ref produces a
clean version. Bump the base in `version.json` or `git tag vX.Y.Z` to cut a release.

**Packing / publishing** (see `.github/workflows/ci.yml` for the automated path):

```powershell
dotnet pack EdgeHop.Tool\EdgeHop.Tool.csproj -c Release -p:PublicRelease=true
#  -> artifacts\nuget\EdgeHop.<version>.nupkg
dotnet nuget push artifacts\nuget\EdgeHop.*.nupkg -k <API_KEY> -s https://api.nuget.org/v3/index.json
```

> **Fixture isolation:** the sample solutions under `tests/EdgeHop.Tests/fixtures/` and
> `tests/samples/` are pristine, built independently at test time, and must NOT inherit the
> repo's build customization. Barrier `Directory.Build.props` / `Directory.Packages.props`
> files at each tree root stop MSBuild's upward search (so NBGV/CPM never leak in). Do not
> remove them.

---

## Solution layout

```
EdgeHop.sln
├── EdgeHop.Core            // model, stable IDs, reconciler, query service, branch
│                             // resolution, and the pluggable seams: IGraphStore /
│                             // GraphStoreFactory, IExtractor / ExtractorFactory, InteropSurface
│                             // / JsInteropMatcher. NO store driver, NO MSBuild.
├── EdgeHop.Indexer         // edgehop-extract: the app-shell host — verb dispatch, the
│                             // load→extract→reconcile pipeline, watch loop, git hooks,
│                             // worktree manager. Reflection-loads extractors + stores.
├── Extractors/
│   ├── EdgeHop.Roslyn      // the C#/Razor extractor plugin (IExtractor); Razor + HTTP +
│   │                         // JS-interop passes, WorkspaceSession, MsBuildBootstrap
│   └── EdgeHop.Oxc         // the JS/TS extractor plugin (IExtractor): shells out to the
│                             // vendored native `edgehop-oxc` binary (Rust/oxc, no Node)
├── Backends/
│   ├── EdgeHop.Sqlite      // the SQLite store plugin (IGraphStoreProvider) — the default
│   └── EdgeHop.Neo4j       // the Neo4j store plugin (IGraphStoreProvider)
├── EdgeHop.Cli             // edgehop: thin query CLI (find-symbol / get-callers / …)
├── EdgeHop.Mcp             // MCP server Claude Code connects to (five tools)
├── EdgeHop.Tool            // the single distributable: the `EdgeHop` .NET global tool.
│                             // Thin dispatcher packing the three heads behind one `edgehop`
│                             // command (index / find-symbol / … / mcp). See "Installation".
└── tests/
    ├── EdgeHop.Tests       // xUnit; fixtures/ (Tiny / Blazor / Http / Js / JsFolder)
    ├── EdgeHop.Neo4j.Tests // live-Neo4j conformance (skipped unless NEO4J_* is set)
    └── samples/              // runnable, self-documenting demo + regression targets:
        ├── EdgeHopExplorer.BlazorServer  // every node kind + all 10 edge types (Blazor Server)
        └── EdgeHopExplorer.Js            // pure JS/HTML, indexed as a bare directory (oxc)
```

Handoff 6 split the former monolithic `EdgeHop.Roslyn` (which was BOTH the extractor and
the `edgehop-extract` exe) into the reflection-loaded `Extractors/EdgeHop.Roslyn` plugin
plus the `EdgeHop.Indexer` host, mirroring the `Backends/` store split. Both stores and
both extractors are loaded by reflection (assembly-name → provider scan), so `EdgeHop.Core`
and the host reference neither MSBuild nor any store driver.

---

## Pinned dependencies

**Verified current on NuGet as of July 2026. Do not substitute versions or invent
API signatures — if a symbol you expect isn't present in the pinned version, stop and
report it rather than guessing against a different version.**

Use Central Package Management: put versions in `Directory.Packages.props` at the repo
root, and reference packages without versions in each `.csproj`.

### `Directory.Packages.props`

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup>
    <!-- Roslyn (workspaces + MSBuild) -->
    <PackageVersion Include="Microsoft.CodeAnalysis.CSharp.Workspaces" Version="5.6.0" />
    <PackageVersion Include="Microsoft.CodeAnalysis.Workspaces.MSBuild" Version="5.6.0" />
    <PackageVersion Include="Microsoft.Build.Locator" Version="1.9.1" />

    <!-- Neo4j -->
    <PackageVersion Include="Neo4j.Driver" Version="6.2.1" />

    <!-- MCP server (stable line; NOT the 2.0.0-preview) -->
    <PackageVersion Include="ModelContextProtocol" Version="1.4.1" />
    <PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.0" />

    <!-- Build-time only: Git-height versioning for the global-tool package (PrivateAssets=all). -->
    <PackageVersion Include="Nerdbank.GitVersioning" Version="3.10.91" />

    <!-- Tests -->
    <PackageVersion Include="xunit" Version="2.9.2" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.0.0" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.13.0" />
  </ItemGroup>
</Project>
```

Implementation notes:

- **`Microsoft.Build.Locator` is mandatory and must be called before any MSBuild or
  MSBuildWorkspace type is touched.** Call `MSBuildLocator.RegisterDefaults()` once at
  process startup, before the first reference to `MSBuildWorkspace`. Getting this wrong
  produces confusing "could not load MSBuild" runtime errors. The extractor project must
  **not** statically import MSBuild assemblies at the top of the same method that registers
  the locator — isolate the workspace-creating code in a separate method/class so the JIT
  doesn't resolve MSBuild types before `RegisterDefaults()` runs.
- **Do not add** `Microsoft.CodeAnalysis.CSharp` separately — it comes transitively via
  `...CSharp.Workspaces`. Adding both at mismatched versions is a common break.
- `ModelContextProtocol` **1.4.1 is the stable release.** There is a `2.0.0-preview` line
  aligned to a newer spec revision — do **not** use it here; Claude Code's MCP client works
  with the stable line.
- Verify `Microsoft.Extensions.Hosting` resolves at 10.0.0 for the target SDK; if the exact
  patch differs, use the latest 10.0.x, but keep it on the 10 line.

---

## Neo4j: schema (fix this before writing any writer code)

Run this DDL against the target database **first**. Node identity and the uniqueness
constraint are the foundation everything keys off — nothing else should be built until
this is in place and tested.

Node label for phase 1–3 C# symbols: `Symbol`. A single label with a `kind` property is
simpler than many labels for the C# graph and keeps Cypher uniform; refine later if needed.

```cypher
// Uniqueness constraint on the composite identity.
// Even though phases 1-3 are single-branch, we bake `branch` into identity NOW so the
// later multi-branch handoff is a data change, not a schema migration. For phase 1-3,
// every node is written with branch = 'main' (or the one branch being indexed).
CREATE CONSTRAINT symbol_id IF NOT EXISTS
FOR (s:Symbol)
REQUIRE (s.branch, s.id) IS UNIQUE;

// Lookup indexes
CREATE INDEX symbol_name IF NOT EXISTS FOR (s:Symbol) ON (s.name);
CREATE INDEX symbol_kind IF NOT EXISTS FOR (s:Symbol) ON (s.kind);
CREATE INDEX symbol_sourcedoc IF NOT EXISTS FOR (s:Symbol) ON (s.sourceDoc);
```

### Node properties (`:Symbol`)

| property     | type   | notes                                                                 |
|--------------|--------|-----------------------------------------------------------------------|
| `branch`     | string | `'main'` in this handoff. Part of composite identity.                 |
| `id`         | string | Stable symbol ID — see `SymbolIdFormat` below. Composite key w/ branch.|
| `name`       | string | Short display name (`ToDisplayString` with a short format).           |
| `kind`       | string | `NamedType` \| `Method` \| `Property` \| `Field` \| `Event` \| `Namespace`. |
| `sourceDoc`  | string | Relative path of the declaring document. May be null (namespaces). For Razor components and their `@code` members this is the authored `.razor` file, not the generated `*_razor.g.cs`. |
| `assembly`   | string | Containing assembly name.                                             |
| `isAbstract` | bool   | Optional; convenient for impact queries.                              |
| `isComponent`| bool   | Handoff 2: true for NamedTypes inheriting (transitively) from `Microsoft.AspNetCore.Components.ComponentBase`. |
| `routes`     | string[] | `@page` route templates (`[Route]` attributes) on components (Handoff 2) in declaration order; and (Handoff 4) verb-prefixed HTTP route templates (`"GET /league/{id}"`) on the Method that registers/serves them — a minimal-API endpoint-registration method or an attribute-routed controller action. Absent when none. |

### Edge (relationship) types

All relationships carry `branch` and `sourceDoc` properties (the `sourceDoc` of the
*source-side* declaration that produced the edge). Note (Handoff 2): `sourceDoc` is a
display/debug property — incremental updates reconcile by whole-graph key diff, NOT by
per-document deletes (see "Handoff 2 / Phase 5" for why per-document was rejected).

| type          | from → to                | meaning                                   |
|---------------|--------------------------|-------------------------------------------|
| `CONTAINS`    | container → member       | namespace→type, type→method/property/field|
| `CALLS`       | Method → Method          | invocation (resolved via semantic model)  |
| `IMPLEMENTS`  | NamedType → NamedType    | class/struct implements interface         |
| `INHERITS`    | NamedType → NamedType    | derived → base class                      |
| `REFERENCES`  | Symbol → NamedType       | uses a type (param/return/field type etc.)|
| `OVERRIDES`   | Method → Method          | override → overridden                      |
| `RENDERS`     | NamedType → NamedType    | Handoff 2: Blazor component statically renders a child component in its markup (source-declared targets only; routable pages are reached at runtime via `routes`, not RENDERS — the Router dispatches dynamically) |
| `HTTP_CALLS`  | Method → Method          | Handoff 4: a client method's `HttpClient` call to the method serving the matching endpoint (verb + route-template shape) — the cross-tier Web→ApiService boundary compile-time edges cannot cross. Target is the minimal-API registration method (lambda handlers have no node) or the controller action. Traversed by `get_callers` alongside `CALLS`. |
| `JS_CALLS`    | C# Method → JS Method    | Handoff 6: a Blazor `IJSRuntime`/`IJSObjectReference` `InvokeAsync`/`InvokeVoidAsync` call (constant identifier) to the JS function the oxc extractor exported under that name. Precise = module-import leaf or globally-unique name; opt-in broad = name-only. Crosses the C#→JS tier boundary. |
| `JS_INVOKES`  | JS Method → C# Method    | Handoff 6: the reverse — a JS `DotNet.invokeMethod[Async]("Asm","Id",…)` (static) or `objRef.invokeMethod[Async]("Id",…)` (instance) call to the C# `[JSInvokable]` method it targets. Precise = static assembly+identifier / instance unique identifier; opt-in broad = identifier-only. Crosses the JS→C# tier boundary. |

Keep the edge set small. Every addition must be justified by a query it enables.

`get_callers` follows `CALLS`, `HTTP_CALLS`, `JS_CALLS` **and** `JS_INVOKES` (an API
endpoint's callers include the Web-tier client methods that hit its route; a JS function's
callers include the C# methods that invoke it; a C# `[JSInvokable]` method's callers include
the JavaScript that invokes it). The cross-tier types are directional caller→callee, so a
chain like a Web click handler → `HTTP_CALLS` → endpoint → `CALLS` → service, or C# →
`JS_CALLS` → JS → `JS_INVOKES` → C#, is fully walkable in one `get_callers` traversal.

### Write pattern

Upsert everything with `MERGE` on the composite key so re-runs are idempotent:

```cypher
MERGE (s:Symbol {branch: $branch, id: $id})
SET s.name = $name, s.kind = $kind, s.sourceDoc = $sourceDoc,
    s.assembly = $assembly, s.isAbstract = $isAbstract
```

```cypher
MATCH (a:Symbol {branch: $branch, id: $fromId})
MATCH (b:Symbol {branch: $branch, id: $toId})
MERGE (a)-[r:CALLS {branch: $branch}]->(b)
SET r.sourceDoc = $sourceDoc
```

Batch writes with `UNWIND $rows AS row ...` (a few thousand rows per transaction) rather
than one round-trip per node/edge, or full-solution builds will be painfully slow.

---

## Stable symbol IDs — the foundation

Every node keys off a deterministic ID that must be identical across recompiles.
Getting this subtly wrong (generics, overloads, partial types) produces duplicate nodes
instead of clean upserts. Use a fixed `SymbolDisplayFormat` and prefix with the symbol
kind to avoid cross-kind collisions.

Put this in `EdgeHop.Core`:

```csharp
using Microsoft.CodeAnalysis;

public static class SymbolIdFormat
{
    // Fully-qualified, includes containing types & namespaces, parameter types,
    // type parameters, and enough detail to disambiguate overloads.
    public static readonly SymbolDisplayFormat Format = new SymbolDisplayFormat(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle:
            SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions:
            SymbolDisplayGenericsOptions.IncludeTypeParameters |
            SymbolDisplayGenericsOptions.IncludeTypeConstraints,
        memberOptions:
            SymbolDisplayMemberOptions.IncludeParameters |
            SymbolDisplayMemberOptions.IncludeContainingType |
            SymbolDisplayMemberOptions.IncludeType,
        parameterOptions:
            SymbolDisplayParameterOptions.IncludeType |
            SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
            SymbolDisplayMiscellaneousOptions.ExpandNullable);

    public static string GetId(ISymbol symbol)
    {
        // Kind prefix guards against a method and a property (etc.) ever colliding.
        var kind = symbol.Kind.ToString();
        var display = symbol.ToDisplayString(Format);
        return $"{kind}:{display}";
    }
}
```

Test this **first** (see checkpoints). Known pitfalls to write tests for:

- **Overloads:** `Foo(int)` and `Foo(string)` must produce different IDs (params included → they do).
- **Generics:** `List<T>` vs a closed `List<int>` — decide and document whether you key on the
  *definition* (`OriginalDefinition`) or the constructed type. **Recommendation: normalize to
  `symbol.OriginalDefinition` before generating the ID** so `List<int>` and `List<string>`
  usages both point at the one `List<T>` node. Do this consistently in `GetId`.
- **Partial types / partial methods:** multiple declarations, one symbol — Roslyn already
  gives you a single `ISymbol`, so IDs coincide correctly. Add a test proving it.
- **Constructed vs. definition methods on generic types:** also normalize via
  `OriginalDefinition`.

---

## EdgeHop.Roslyn — extractor behavior

1. `MSBuildLocator.RegisterDefaults()` at startup (see dependency notes above).
2. `MSBuildWorkspace.Create()`; subscribe to `workspace.WorkspaceFailed` and **log every
   diagnostic** — MSBuild load failures are the #1 source of a silently-incomplete graph.
   Treat any `Failure`-level diagnostic as a hard stop for phase 1 (fix the load before
   trusting the graph).
3. `await workspace.OpenSolutionAsync(slnPath)`.
4. For each `Project` → `await project.GetCompilationAsync()`.
5. Walk symbols: enumerate the compilation's global namespace recursively for declared
   types and members (`INamespaceSymbol` → `INamedTypeSymbol` → members). Emit `:Symbol`
   nodes + `CONTAINS` edges.
6. Edges:
   - `IMPLEMENTS` / `INHERITS`: from each `INamedTypeSymbol`'s `Interfaces` / `BaseType`.
   - `OVERRIDES`: from `IMethodSymbol.OverriddenMethod`.
   - `REFERENCES`: from parameter/return/field/property types.
   - `CALLS`: walk each method's syntax with its `SemanticModel`; for every
     `InvocationExpressionSyntax`, resolve `GetSymbolInfo(...).Symbol` to the target method,
     normalize via `OriginalDefinition`, emit the edge. **Only add nodes for symbols whose
     `sourceDoc` is inside the solution**; you may reference external/framework symbols as
     edge targets but decide (and document) whether to create nodes for them. Recommendation
     for phase 1–3: create nodes only for source-declared symbols; **skip edges** whose target
     is a metadata/framework symbol to keep the first graph focused and small.
7. Every node/edge written with `branch = "main"` and the correct `sourceDoc`.

Do **not** use solution-wide `SymbolFinder.FindReferencesAsync` in this handoff — deriving
edges directly from each declaration's semantic model is enough for a full build and avoids
the expensive solution-wide search. `SymbolFinder` becomes relevant only in the later
incremental/indexer phase.

Deliver the extractor as a console app: `edgehop-extract <path-to.sln>` that connects to
Neo4j (connection string + creds from environment variables — **never** hard-coded, and
the agent must not enter credentials anywhere; the developer supplies them), applies the
schema DDL if absent, then does a full build.

---

## EdgeHop.Mcp — the query surface

A minimal MCP server over stdio using `ModelContextProtocol` 1.4.1 + generic host.
**Phase 3 exposes only two tools.** Do not add more until the loop is validated.
*(Superseded by owner direction in Handoff 5 — decision D8: the surface is now five
tools. See "Handoff 5 — Query-surface expansion" below.)*

- `find_symbol(query: string)` → fuzzy/name match against `:Symbol.name` (and optional
  `kind` filter). Returns id, name, kind, sourceDoc for up to N matches.
- `get_callers(symbolId: string, depth: int = 1)` → who calls this, up to `depth` hops:
  `MATCH (c:Symbol)-[:CALLS*1..$depth]->(:Symbol {branch:$branch, id:$symbolId}) RETURN DISTINCT c`

Branch (Handoff 2): resolved PER TOOL CALL via the shared `BranchResolver` —
`EDGEHOP_BRANCH` env var, else the current git branch of `EDGEHOP_REPO` (set in
`.mcp.json`), else `"main"`. A mid-session `git switch` is picked up by the very next
tool call.

Connection details (Neo4j URI, user, password, database) come from environment variables read
at startup. The `IDriver` is a singleton, created once, shared across tool calls (it's
thread-safe; sessions are not — open a session per tool invocation).

Register in the repo-root `.mcp.json` so Claude Code discovers it (see `.mcp.json.sample`).

---

## Phased build order + test-first checkpoints

Build strictly in this order. Each checkpoint must pass before proceeding — this keeps the
schema stable before dependent code is written.

**Phase 1 — Core + schema**
1. `SymbolIdFormat` with `OriginalDefinition` normalization.
2. xUnit tests: overloads differ; generic usages collapse to the definition; partial type
   yields one ID; nested/containing types qualify correctly. *(No Neo4j needed — these run
   on hand-built compilations via `CSharpCompilation.Create` with small source snippets.)*
3. Neo4j writer + DDL applied; a round-trip test: MERGE a node, read it back, MERGE again,
   assert no duplicate (proves idempotent upsert on the composite key).
   *(Requires a local Neo4j — see "Environment" below.)*

**Phase 2 — C# extractor**
4. Point the extractor at a **tiny throwaway .NET 10 solution** (2–3 classes, an interface,
   an override, a couple of method calls) checked into `EdgeHop.Tests/fixtures/`.
   Assert the exact node count and each expected edge. This fixture is the regression anchor.
5. Only then run it against a real solution. Expect MSBuild load diagnostics on first run;
   resolve them. Sanity-check node/edge counts are plausible, spot-check a known call chain.

**Phase 3 — MCP server**
6. `find_symbol` returns the fixture's known symbols.
7. `get_callers` returns the known caller from the fixture at depth 1, and the transitive
   caller at depth 2.
8. Wire `.mcp.json`, restart Claude Code, confirm it can call both tools and trace a chain in
   a real solution that you verify by hand.

---

## Environment (developer workstation, this handoff)

- **.NET 10 SDK** installed (the MCP templates and dnx-launched servers require it).
- **Neo4j**: local single instance is fine. Community Edition is sufficient for phases 1–3
  (single database, single branch). Docker Desktop or the Neo4j Windows service both work;
  the developer sets it up and provides the bolt URI + credentials via env vars. The agent
  must not create accounts, set passwords, or enter credentials — surface what's needed and
  let the developer do it.
- Env vars the tools read: `NEO4J_URI` (e.g. `bolt://localhost:7687`), `NEO4J_USER`,
  `NEO4J_PASSWORD`, `NEO4J_DATABASE` (default `neo4j`).

---

## Explicitly deferred (still out of scope after Handoff 6)

- **Multi-database vs. composite-key branch isolation** decision (Enterprise vs.
  Community). Handoff 2 uses composite-key isolation (`branch` in identity) throughout.
- **Cross-tier JS/TS `fetch` → C# endpoint edges.** Both *interop* directions ARE now
  delivered (Handoff 6: `JS_CALLS` C#→JS via `IJSRuntime`, `JS_INVOKES` JS→C# via
  `DotNet.invoke*`→`[JSInvokable]`), and the *C#→C#* HTTP boundary is Handoff 4
  (`HTTP_CALLS`). Inferring edges from JavaScript/TypeScript `fetch`/`axios` calls into C#
  HTTP endpoints (matching a fetch URL to a route) remains out of scope — a different
  problem from the interop bridge, and a low-value one now that the interop edges exist.
- **oxc's type-checker gap** — the JS extractor resolves syntax + scope/binding (oxc does
  NOT type-check), so a member call via an inferred type (`foo.bar()`) produces no JS
  `CALLS` edge. Deferred to a possible future typescript-go pass; ~90% coverage without it.
- **Shared / networked multi-developer backend** (owner decision 2026-07-17, do not
  re-propose). EdgeHop is per-developer, per-solution and local. If a shared graph is
  ever wanted, the assessed shape is a CI-fed read-mostly instance, not a store multiple
  developers write concurrently (reconcile is single-writer-per-branch).
- **`SymbolFinder.FindReferencesAsync`**: still unused. The reconcile-by-diff design
  (below) made per-document dependency tracking unnecessary.

Delivered in Handoff 4 (were deferred after Handoff 2):
- **Push-driven re-indexing** → local git hooks (`edgehop-extract install-hooks`):
  post-commit/-merge/-checkout background re-index. See "Handoff 4 / Git-hook
  re-indexing". (No server-side/CI hook: with no shared backend to feed, CI re-indexing
  would only validate — dropped in favor of the local hooks that keep your own store
  fresh.)
- **In-memory workspace refresh for watch cycles** (`WithDocumentText` /
  `WithAdditionalDocumentText`) → `WorkspaceSession`: a watch batch of known,
  still-existing documents refreshes the kept solution in memory instead of reloading
  MSBuild; membership changes and branch switches still full-load. Extraction and
  reconcile stay whole-solution, so correctness is unchanged.

---

## Items to verify against a real solution before trusting them (flagged, not assumed)

- Whether `MSBuildWorkspace` opens your full target solution cleanly on .NET 10 without
  `WorkspaceFailed` diagnostics that drop projects. **Check the diagnostics; do not assume.**
- Exact `Microsoft.Extensions.Hosting` patch version available for the installed SDK.
- That generic-normalization via `OriginalDefinition` gives the node granularity you actually
  want for a generics-heavy codebase — confirm on a real query before locking it in.

---

## Front ends (added 2026-07-16, owner-approved scope change)

The query backend is a single transport-neutral service — `EdgeHop.Core.EdgeHopQueryService`
(input validation, limit clamping, exact truncation detection) over `Neo4jGraphReader`. Two
thin front ends expose it; neither contains query logic of its own:

### `edgehop` CLI (local use)

```
edgehop find-symbol <query> [--kind <NamedType|Method|Property|Field|Event|Namespace>]
                              [--limit <n>] [--branch <name>] [--json]
edgehop get-callers <symbolId> [--depth <1-10>] [--branch <name>] [--json]
edgehop get-relationships <symbolId> [--direction out|in|both] [--edge-type <TYPE>]
                              [--depth <1-10>] [--branch <name>] [--json]   # Handoff 5
edgehop get-path <fromId> <toId> [--edge-type <TYPE>] [--max-length <1-15>]
                              [--branch <name>] [--json]                    # Handoff 5
edgehop stats [--top <n>] [--branch <name>] [--json]                     # Handoff 5
```

- Search is a case-insensitive SUBSTRING match: `greet` finds `Greet`, `Greeting` and
  `LoudGreeter`. There are NO wildcards and no regex — `*` and `?` are matched as literal
  characters, so `greet*` finds nothing. Every search already behaves like `*query*`.
- Human-readable output by default; `--json` prints exactly the MCP wire shape
  (`{"hits":[...],"truncated":...}` / `{"targetId":...,"depth":...,"callers":[...]}`) on
  stdout and nothing else, so output can be piped.
- When `--branch` is omitted, the branch is resolved exactly like the MCP server's
  (Handoff 2): `EDGEHOP_BRANCH` env var, else the current git branch of
  `EDGEHOP_REPO`, else the current directory's git branch, else `main`. Connection
  comes from the same `NEO4J_*` environment variables. Exit codes: 0 success (even with
  0 matches), 1 usage/config/validation error.
- Run via `dotnet run --project EdgeHop.Cli -- <args>` or the built
  `EdgeHop.Cli\bin\Debug\net10.0\edgehop.exe`.

### MCP server (collaborative / offloaded use)

`EdgeHop.Mcp` over stdio, registered in the repo-root `.mcp.json`
(which points at the built exe — REBUILD `EdgeHop.Mcp` after code changes; the exe is
not auto-rebuilt). Tool descriptions document the same no-wildcards substring semantics.
**Superseded by Handoff 5** (D8, below): the surface is no longer "exactly `find_symbol`
and `get_callers`" — it is the full five-tool set `{find_symbol, get_callers,
get_relationships, get_path, graph_stats}`. Branch is resolved per tool call, not pinned
to `main`.

---

## Handoff 2 (2026-07-17) — component graph, reconcile, branches, watch

Delivered as Phases 4–8. No new projects and **no new packages**: the Razor pass is pure
Roslyn over the compiler's own generated code (no `Microsoft.AspNetCore.Razor.Language`,
no AngleSharp), and the indexer grew inside `EdgeHop.Roslyn` (`edgehop-extract`);
the query CLI stays MSBuild-free. Pinned versions unchanged.

### Phase 4 — Blazor component graph

The Razor SDK compiles every `.razor` file into an in-memory `*_razor.g.cs` tree that
already flows through the extractor. The Razor pass (`RazorComponentPass`) derives from
those trees what the plain C# walk cannot see:

- **RENDERS** edges (component → child component) from `OpenComponent<T>` type arguments,
  including inferred-generic tags via the `__Blazor.*.TypeInference` helper bodies.
  Source-declared targets only — MudBlazor/framework components get no edge.
- **Handler bindings reuse CALLS** (`BuildRenderTree → handler`) from
  `EventCallbackFactory.Create*` / `RuntimeHelpers.CreateInferredEventCallback`
  method-group arguments — so `get_callers` sees UI handlers with zero reader changes.
  (Lambda handlers were already captured by the ordinary CALLS walk.)
- **`isComponent`** (ComponentBase base-walk) and **`routes`** (`[Route]` attributes from
  `@page`) node properties.
- **sourceDoc remap**: every symbol declared in a generated tree maps to the authored
  `.razor` file via the tree's `#pragma checksum` directive (NOT `#line` mappings — those
  sit in hidden regions and frequently point at `_Imports.razor`).

Node-emission refinements that came with this: the **authored-symbol rule** — symbols
declared only in SDK-injected build artifacts under `obj`/`bin` path segments (e.g. the
Razor SDK's `obj/.../EmbeddedAttribute.cs`) get no node; the compiler-plumbing
`__Blazor` namespace is never walked. The `_Imports` class is kept (it maps to the real
`_Imports.razor`). Regression anchor: `EdgeHop.Tests/fixtures/BlazorFixture/` with its
own `EXPECTED-GRAPH.md` (19 nodes / 22 edges); TinyFixture's 19/28 contract is untouched.

### Phase 5 — reconcile engine (how the graph stays truthful)

`GraphReconciler` (Core) makes a branch exactly match a fresh whole-solution extraction:
upsert every desired node/edge, then delete `existing − desired` key sets (node ids;
`(type, fromId, toId)` edge keys via `Neo4jGraphSnapshotReader`). Per-document surgical
deletes were **rejected** — edge identity collapses across documents and renames break
caller edges in files that were not re-extracted; whole-solution extract + diff is
correct by construction and extraction is the cheap part at this scale.

Safety: every delete MATCHes `{branch: $branch}` on all endpoints with values as
parameters (cross-branch deletes are inexpressible); an empty desired set against a
non-empty branch throws unless `--allow-empty`; edge types unknown to this build are
filtered out of snapshots and can never be deleted; whole-branch prune is a separate
verb gated on `--yes`.

### Phase 6 — indexer verbs + branch resolution

```
edgehop-extract index <sln-or-dir> [--branch <b>] [--dry-run] [--allow-empty]
                                     [--watch [--debounce <ms>]] [--no-worktree]
edgehop-extract prune --branch <b> [--yes]
edgehop-extract branches
edgehop-extract <sln-or-dir> [--dry-run]      # legacy form == index
```

`index` = load → extract → reconcile. **Deliberate behavior change:** a full build now
also prunes stale rows (MERGE-only builds never did). `--dry-run` prints the reconcile
plan (what would be deleted) without writing. Exit codes unchanged: 0 / 1 / 2
(workspace Failure hard stop).

**Directory targets — code-graphing a non-.NET project (added 2026-07-18).** The `index`
target (and `install-hooks`) may be a **project directory** instead of a `.sln` file. When
it is a directory, the C# (Roslyn) extractor no-ops — it has no solution to load and never
even registers MSBuild — and the JS/TS extractor graphs the tree, so a **pure JS/TS project
with no solution** code-graphs with `edgehop-extract index C:\my-js-app`. Auto-detected
from the target (a directory vs a file); no new flag. The store path and branch derive from
that folder's repo exactly as for a `.sln` (the SQLite store-per-solution derivation and
`BranchResolver` already accept a directory), so **the MCP server and `edgehop` CLI need no
change** to query a folder-indexed graph — point `EDGEHOP_REPO` (MCP) or the working
directory (CLI) at the folder as usual. `--watch` reacts to `.js/.ts`/markup edits as well as
`.cs/.razor`; worktree-routed other-branch indexing (`--branch X` ≠ checked-out) still applies
to `.sln` targets only (a directory + differing `--branch` stamps the current tree, like
`--no-worktree`). Regression anchor: `EdgeHop.Tests/fixtures/JsFolderFixture` — a bare JS
directory with no `.sln`/`.csproj`, **5 nodes / 4 edges**, with its own `EXPECTED-GRAPH.md`.

**Branch resolution** (shared `BranchResolver`, identical in all three heads):
explicit `--branch` > `EDGEHOP_BRANCH` env var > current git branch of
`EDGEHOP_REPO` env var > current git branch of the natural path (solution dir for the
indexer, cwd for the CLI) > `"main"`. Detection reads `.git/HEAD` directly (no git.exe;
handles the worktree `.git`-file layout; detached HEAD → 12-char SHA). The MCP server
resolves **per tool call**, so a mid-session `git switch` is picked up by the next call;
`.mcp.json` sets `EDGEHOP_REPO`. The indexer's design-time builds run with
`NuGetAudit=false` — a dependency CVE on the indexed branch is that branch's CI problem
and must not block indexing.

### Phase 7 — watch mode

`index --watch`: initial full cycle, then a `FileSystemWatcher` on the repo
(`.cs`/`.razor`, excluding `obj`/`bin`/`.git`) plus a dedicated watcher on the gitdir's
`HEAD` (branch switches), feeding a debounced (default 1500 ms) loop. Each batch runs an
**extract → reconcile** cycle over a solution obtained from a kept-alive
`WorkspaceSession` (Handoff 4): when the batch touched only documents the solution
already knows and that still exist on disk, the session refreshes them in memory
(`WithDocumentText` / `WithAdditionalDocumentText` — a `.razor` refresh re-runs the Razor
generator) instead of reloading MSBuild; created/deleted/renamed files, branch switches
and watcher overflow full-load. **Extraction and reconcile stay whole-solution either
way**, so correctness never depends on the batch contents (per-document surgical deletes
were rejected — see Phase 5). The timing line reports `refresh (N doc(s))` vs `load`.
Cycle failures keep the last good graph and the loop alive; Ctrl+C exits 0. On a branch
switch the old branch's data is retained (prune it explicitly if unwanted). Measured on a
real Blazor solution: cold cycle ≈4 s fixture / ~½–2 min full-solution load; a warm
in-memory refresh skips that load entirely.

### Handoff 4 — git-hook re-indexing (push-driven replacement)

`edgehop-extract install-hooks <sln> [--repo p]` writes `post-commit`, `post-merge`
and `post-checkout` git hooks that background-re-index the solution (`edgehop-extract
index <sln>`, detached, output suppressed) after commits, merges/pulls and branch
switches — the hands-off alternative to leaving `--watch` running. Everything EdgeHop
writes lives in a marker-delimited managed block (`# >>> edgehop hooks … >>>`); an
existing hook that lacks the markers is **never** overwritten (install refuses, whole,
and writes nothing). `post-checkout` only fires on branch checkouts (`$3 = 1`). The
hooks directory is resolved via `git rev-parse --git-path hooks`, so worktrees and a
configured `core.hooksPath` are honored. `uninstall-hooks [--repo p]` removes only the
managed block, deleting a hook file only when nothing but the shebang remains.

Concurrent index runs against the same store (two hooks firing during a rebase, or a
hook racing a manual `index`) serialize on a per-store cross-process file lock
(`IndexLock`, an exclusive `FileShare.None` handle under `%TEMP%\edgehop-locks\`; the
OS releases it on process exit, so a crash never strands it) — keeping reconcile
single-writer-per-branch. There is deliberately **no** server-side/CI re-indexing hook:
with no shared backend to feed, CI re-indexing could only validate, so it was dropped in
favor of these local hooks (the shared-team backend was dropped 2026-07-17; a CI-fed
read-mostly instance is the assessed shape if one is ever wanted).

### Phase 8 — other-branch indexing via private worktrees

`index <sln> --branch B` where B is not the checked-out branch routes through
`WorktreeManager`: a private worktree under
`%LOCALAPPDATA%\EdgeHop\worktrees\<repo>-<hash>\<branch>` (created with
`git worktree add`, refreshed with `reset --hard` + `clean -fd` — it is a disposable
cache, never user-edited), then restored and indexed under branch value B. The
developer's working tree — dirty or not — is **never touched**; in-place checkouts never
happen. The branch must exist locally (no auto-create). `--no-worktree` stamps B onto
the current tree instead (CI/containers).

### Querying branches

`edgehop find-symbol <q> --branch <b>` / MCP tools follow the resolution above.
`edgehop-extract branches` lists every branch value in the store with node counts.

---

## Handoff 3 (2026-07-17) — Backends

The graph store is pluggable behind four `EdgeHop.Core` interfaces — `IGraphStore`
(connection/lifetime/schema) exposing `IGraphWriter`, `IGraphSnapshotReader` and
`IGraphReader` (whose contract also owns the validation constants `MaxLimit` /
`MinDepth` / `MaxDepth`). `GraphStoreFactory.FromEnvironment()` is the ONLY place a
backend is chosen; the extractor, the `edgehop` CLI and the MCP server all obtain
their store there, and each reports the active backend in its startup/stderr output
(`Backend: …` / `edgehop: backend …`).

### Selection

```
EDGEHOP_BACKEND = sqlite   (default when unset — embedded, serverless)
                  | neo4j
```

Any other value is a startup error naming the valid values. The MCP server resolves
the backend once at startup; branch resolution stays per tool call.

> Owner decision 2026-07-17: sqlite is the default (the handoff spec originally kept
> neo4j-when-unset; simplicity won — the default backend needs no server, no JVM and
> no credentials). Set `EDGEHOP_BACKEND=neo4j` to get the Handoff 2 behavior.

### `neo4j`

Unchanged from Handoff 2: `NEO4J_URI` / `NEO4J_USER` / `NEO4J_PASSWORD` /
`NEO4J_DATABASE`, schema DDL above, branches multiplexed by the `branch` property.

### `sqlite` (default)

An embedded single-file store (`Microsoft.Data.Sqlite`, WAL journaling) — no server,
no JVM, **no credentials**, and **one store file per solution** with branches inside
it, so indexing multiple solutions needs no configuration at all. The default path is
derived per repo (precedence mirrors `BranchResolver`):

```
EDGEHOP_SQLITE_PATH            explicit override — always wins
repo of EDGEHOP_REPO           → %LOCALAPPDATA%\EdgeHop\stores\<repo>-<hash>.db
repo of the natural path         → same derivation (solution dir for the indexer,
                                   cwd for the CLI; MCP uses EDGEHOP_REPO)
no repository found              → %LOCALAPPDATA%\EdgeHop\edgehop.db (shared)
```

The `<repo>-<hash>` naming is the worktree cache's scheme (leaf name for humans,
8-hex path hash as identity), so every head — indexer, CLI, MCP server — derives the
SAME file for the same repo. A branch indexed via the private-worktree route stores
under the ORIGINAL repo's file, not the worktree's path (`IndexOptions.StoreHint`).

- Schema: `nodes(branch,id,…)` PK `(branch,id)`; `edges(branch,type,fromId,toId,…)`
  PK = exactly the reconciler's edge identity; name/kind/toId lookup indexes. Applied
  idempotently and lazily on first use — a fresh file answers queries as empty.
- Semantics are conformance-tested against the Neo4j behavior (same test contract:
  round-trip idempotency, endpoint-gated edge upserts, whole-batch unknown-type
  rejection, branch-scoped surgical deletes, reconciler, cycle-safe `get_callers` via
  a bounded recursive CTE, case-insensitive literal-substring `find_symbol` with
  `%`/`_`/`\` escaped, `ORDER BY name, id`). The SQLite suite always runs — the full
  graph test suite passes on a machine with no Neo4j at all.
- Concurrency: WAL means the MCP server's reads never block on an indexer write;
  cross-process writes serialize via SQLite file locking with a 30 s busy timeout.
- Measured on a real solution (2,407 nodes / 4,074 edges): key lookup 0.005 ms,
  `find-symbol` ≤1 ms, `get-callers` depth 5 ≈18 ms — all far inside the Gate 0
  50 ms target (the LiteGraph candidate missed it 8–90×).

Deletes remain branch-scoped and parameterized on every backend (cross-branch deletes
stay inexpressible), `prune --yes` remains the only whole-branch delete surface, and
re-indexing from source IS the migration path between backends — the graph is derived
data; no exporter exists on purpose.

---

## Handoff 4 (2026-07-18) — Cross-tier HTTP edges, git-hook re-indexing, in-memory watch refresh

Three items deferred after Handoff 2 are now delivered. The summary:

### `HTTP_CALLS` — the C#→C# cross-tier edge

`HttpEdgePass` (a two-phase pass in `EdgeHop.Roslyn`, run after the per-project symbol
+ Razor walk) links a Web-tier `HttpClient` call to the C# method that serves the
matching endpoint — the Web→ApiService boundary no compile-time edge crosses. It runs in
two halves because callers and endpoints live in different projects: `Collect` gathers
registrations and call sites per compilation; `Emit` matches across all of them.

- **Endpoints** (the `routes` anchor + edge target): minimal-API `Map{Get,Post,Put,
  Delete,Patch}` invocations — route = the constant `pattern` argument, composed with
  `MapGroup` prefixes reachable through the receiver chain or a same-method group local —
  attributed to the **enclosing method** (a minimal-API lambda handler has no symbol
  node, so the registration method, e.g. `LeagueEndpoints.Map`, is the stable,
  queryable anchor); and attribute-routed controller actions (`[HttpGet("{id}")]`
  composed with the class `[Route]`, `[controller]`/`[action]` tokens substituted),
  attributed to the action method itself. Each anchor's `routes` property lists its
  verb-prefixed templates (`"GET /league/{id}"`) in declaration order. Non-constant route
  patterns are skipped (logged) — an unresolvable route is not a registered route.
- **Callers**: invocations resolving to `System.Net.Http.HttpClient` /
  `HttpClientJsonExtensions` methods whose name implies a verb (`GetFromJsonAsync`→GET,
  `PostAsJsonAsync`→POST, …; `Send*` is skipped — no verb without inspecting the request).
  The `requestUri` becomes a template: string constants verbatim, interpolation holes as
  single-segment wildcards (`$"/league/{id}"`), a non-constant trailing concat operand
  (`"/courses" + query`) assumed to be a query string, everything from the first `?`
  stripped.
- **Match**: verb equality + route-template *shape* — literal segments compare
  case-insensitively, a parameter segment (`{id:int}`, an interpolation hole) matches any
  one segment, a catch-all (`{*rest}`/`{**rest}`) matches the tail. Every match emits one
  `HTTP_CALLS` (deduped by edge key; the method→method model collapses a caller that hits
  two routes of the same registration method to one edge). `get_callers` traverses
  `HTTP_CALLS` with `CALLS`.
- **Measured on a real solution**: 87 registered routes, 81 client call sites → 79
  `HTTP_CALLS` edges; node count unchanged (2,407), edges 4,074 → 4,153 (= +79).
  `get_callers` on `LeagueEndpoints.Map` returns the five API-client league methods
  across the tier boundary (plus the real AppHost `CALLS` caller). Regression anchor:
  `fixtures/HttpFixture` (a two-project Web+Api solution with **no** ProjectReference
  between the tiers), **22 nodes / 32 edges** (CONTAINS 21, CALLS 5, HTTP_CALLS 6),
  contract in its `EXPECTED-GRAPH.md`. Single-tier fixtures self-no-op (Tiny 19/28,
  Blazor 19/22 unchanged).

> Owner decision (2026-07-17, Q&A): `HTTP_CALLS` is method→method (client method →
> endpoint-registration method), NOT a synthetic per-route `Endpoint` node — zero new
> node kinds, and the registration method's existing `CALLS` chain into its handler logic
> lets `get_callers` walk from a Web caller down into the service layer.

### Git-hook re-indexing + in-memory watch refresh

See "Phase 7 — watch mode" and "Handoff 4 — git-hook re-indexing" above:
`WorkspaceSession` gives watch cycles an in-memory document refresh (full-load fallback
on membership/branch changes; extraction stays whole-solution), and `install-hooks`
writes managed post-commit/-merge/-checkout hooks that background-re-index — the
push-driven replacement, serialized per store by `IndexLock`. JS/TS `fetch`→C# edges and
any shared multi-developer backend remain out of scope.

---

## Handoff 5 (2026-07-18) — Query-surface expansion

Read-side only: extraction, writers and schema are untouched (no DDL migration — the
`isComponent` / `routes` columns already exist). Three new MCP tools and three new CLI
verbs draw the rest of the graph — every edge type, not just `CALLS` — into first-class
queries, and every `SymbolHit` now surfaces `isComponent` / `routes`. The delivered
semantics and the decision log follow.

> **Owner direction (decision D8): the README "Phase 3 exposes only two tools" rule is
> superseded.** The MCP surface is now the five tools below; branch is resolved per tool
> call on every one of them (not pinned to `main`). The two-tool restraint was a
> loop-validation guard from the first handoff — the loop is long validated.

> **Re-index note (decision D4):** `isComponent` and `routes` are populated by extraction.
> A store indexed **before** Handoff 4/5 simply reports `isComponent=false` / `routes=null`
> until it is re-indexed (`edgehop-extract index <sln>`). This is expected, not a bug —
> the read path reflects whatever the last index wrote.

### The query surface (five MCP tools / their CLI verbs)

Every query is branch-scoped and parameterized on both backends (SQLite and Neo4j) with
shared conformance tests (D9); user strings are always bound parameters — the only values
ever interpolated into query text are a validated integer bound (the `get_callers` depth
pattern) or a validated edge-type token drawn from the closed `EdgeTypes.All` whitelist
(the writer pattern). Cross-branch reads stay inexpressible.

Edge types in the graph: `CONTAINS`, `CALLS`, `IMPLEMENTS`, `INHERITS`, `REFERENCES`,
`OVERRIDES`, `RENDERS`, `HTTP_CALLS`, `JS_CALLS`, `JS_INVOKES`.

| MCP tool | CLI verb | what it answers |
|----------|----------|-----------------|
| `find_symbol(query, kind?)` | `find-symbol` | where a symbol lives (now with `isComponent` / `routes`) |
| `get_callers(symbolId, depth=1)` | `get-callers` | who calls this (follows `CALLS` + `HTTP_CALLS` + `JS_CALLS` + `JS_INVOKES`) |
| `get_relationships(symbolId, direction="out", edgeType?, depth=1)` | `get-relationships` | everything related to a symbol by any edge type |
| `get_path(fromId, toId, edgeType?, maxLength=10)` | `get-path` | one shortest directed path between two symbols |
| `graph_stats(topN=10)` | `stats` | per-branch orientation: totals, counts by kind/type, god nodes |

**`get_relationships`** — related symbols by edge. `direction` is `out` \| `in` \| `both`
(default `out`); `edgeType` filters to one of the ten types (omit for all); `depth` is
1..10 (default 1). **Depth > 1 requires a single explicit `edgeType`** — mixed-type
multi-hop is meaningless and forbidden (`ArgumentException`). Each hit carries the related
`SymbolHit` plus the `edgeType` and `direction` that reached it, so one depth-1 call
answers "everything related." Fan-out is capped at the shared `MaxRequestLimit` (99) with
exact truncation (probe limit+1, same as `find_symbol`).

**`get_path`** — one shortest directed path over out-edges from `fromId` to `toId`,
optionally constrained to a single `edgeType`; `maxLength` is 1..15 (default 10). Returns
the ordered nodes plus the typed edge linking each to its predecessor, or `found:false`
with empty nodes when unreachable. `fromId == toId` is a found, length-0 single-node path.

**`graph_stats`** — per-branch totals (nodes, edges), counts by kind and by edge type, and
the top-N "god nodes" by degree (`topN` 1..50, default 10, clamped not thrown). **God-node
degree excludes `CONTAINS`** (D6) — otherwise namespaces and types, which contain
everything, would always dominate; the interesting hubs are the heavily-called/-referenced
methods and types.

### Wire shapes (`--json` prints exactly the MCP tool shape, camelCase, nulls omitted)

`find_symbol` / `get_callers` are unchanged except each hit now carries `isComponent`
(always emitted) and `routes` (omitted when null):

```jsonc
// find_symbol / a hit inside any tool's result
{ "id": "Method:Foo.Bar()", "name": "Bar", "kind": "Method",
  "sourceDoc": "Foo.cs", "isComponent": false }          // routes omitted when null
{ "id": "NamedType:Pages.Home", "name": "Home", "kind": "NamedType",
  "sourceDoc": "Pages/Home.razor", "isComponent": true, "routes": ["/", "/home"] }

// get_relationships
{ "targetId": "NamedType:Greeter", "direction": "out", "edgeType": null, "depth": 1,
  "hits": [ { "symbol": { … }, "edgeType": "IMPLEMENTS", "direction": "out" } ],
  "truncated": false }

// get_path  (nodes[0].edgeTypeFromPrev is always null)
{ "fromId": "Method:A.M()", "toId": "Method:C.M()", "found": true,
  "nodes": [ { "symbol": { … }, "edgeTypeFromPrev": null },
             { "symbol": { … }, "edgeTypeFromPrev": "CALLS" },
             { "symbol": { … }, "edgeTypeFromPrev": "CALLS" } ] }

// graph_stats
{ "branch": "UI", "totalNodes": 2407, "totalEdges": 4153,
  "nodesByKind": [ { "kind": "Method", "count": 1204 }, … ],
  "edgesByType": [ { "type": "CONTAINS", "count": 2380 }, … ],
  "godNodes": [ { "symbol": { … }, "degree": 142 }, … ] }
```

Human (non-`--json`) CLI output: `get-relationships` groups neighbors like `get-callers`
(each line prefixed with its `edgeType direction`); `get-path` prints an arrowed chain
(`A --CALLS--> B --CALLS--> C`) plus node lines, or `no path`; `stats` prints totals with
kind/type tables and the god-node list. `WriteHit` gains `component` / `routes:` lines
only when present, so existing route-less output is byte-for-byte unchanged.

### Graph-first hook (artifact, install-it-yourself)

`hooks/edgehop-graph-first.ps1` is a PowerShell `PreToolUse` hook a consuming repo can
opt into: on a `Grep`/`Glob` call it writes a non-blocking `additionalContext` nudge to
prefer the `edgehop` tools for **structural** questions (leaving text/content search to
grep). It always exits 0 and never blocks. It is delivered as a script + docs only
(decision D7) — EdgeHop does **not** install it into this repo or into any consuming
repo. To adopt it, add to the consuming repo's `.claude/settings.json`:

```json
{
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "Grep|Glob",
        "hooks": [
          { "type": "command",
            "command": "pwsh -NoProfile -File .claude/hooks/edgehop-graph-first.ps1" }
        ]
      }
    ]
  }
}
```

---

## Handoff 6 (2026-07-18) — Pluggable extractors, oxc JS/TS extractor, bidirectional JS interop

Three things landed together: the extractors became reflection-loaded plugins (mirroring the
Handoff 3 store split), a native JS/TS extractor was added, and cross-tier C#↔JS interop
edges were derived in both directions.

### Pluggable backends AND extractors

Both stores and both extractors are now reflected assemblies discovered by an assembly-name →
provider scan, so `EdgeHop.Core` holds only abstractions:

- **Stores** — `Backends/EdgeHop.Sqlite` and `Backends/EdgeHop.Neo4j`, each an
  `IGraphStoreProvider`, loaded by `GraphStoreFactory` from `EDGEHOP_BACKEND`
  (`sqlite` default / `neo4j`). Proven isolated: the SQLite path runs with the Neo4j driver
  DLLs deleted. Neo4j's live conformance tests moved to `EdgeHop.Neo4j.Tests` (skipped
  unless `NEO4J_*` is set), off the default test critical path.
- **Extractors** — `Extractors/EdgeHop.Roslyn` (C#/Razor) and `Extractors/EdgeHop.Oxc`
  (JS/TS), each an `IExtractor`, loaded by `ExtractorFactory` (both by default;
  `EDGEHOP_EXTRACTORS` can subset). The former `EdgeHop.Roslyn` exe split into the
  `EdgeHop.Indexer` host (app-shell) plus the extractor plugin.

All extractors' rows merge into **one** `ExtractionResult` reconciled **once per branch** (the
reconciler diffs the whole branch — two reconciles would mutually prune). A pure-C# solution is
byte-for-byte unchanged: the pinned single-tier fixtures still hold.

### oxc JS/TS extractor (no Node.js)

`Extractors/EdgeHop.Oxc` shells out to a small **native Rust binary** (`edgehop-oxc`, built
on [oxc](https://oxc.rs) — `oxc_parser` + `oxc_semantic`) over stdin/stdout JSON. It discovers
`.js/.ts/.jsx/.tsx` and inline `<script>` under the solution root (skipping `node_modules`,
`obj`, `bin`, `_framework`, `*.min.js`, `*.d.ts`, …) and emits module/class/function/field nodes,
`CONTAINS`, and binding-resolved `CALLS`. Every JS node id carries a mandatory `js|` tier tag so
it can never collide with a C# id; the rows merge into the same per-branch graph.

- **No Node.js runtime** — the engine is Rust crates, not a JS package. Built with the **gnu**
  toolchain (`cargo +stable-x86_64-pc-windows-gnu`; the machine has no MSVC C++ tools). Source
  in `Extractors/EdgeHop.Oxc/native/`; the compiled `win-x64` binary is **vendored in-repo**
  under `tools/win-x64/` (the repo has no remote/CI to host a release) and copied next to the
  consuming exe by the plugin's csproj.
- **Coverage** ≈ 90% of tsc: full JS/TS/JSX syntax + scope/binding resolution. The ~10% gap is
  type-checker-driven member resolution (`foo.bar()` via an inferred type) — oxc does not
  type-check, and produces no false edge for it.

### Bidirectional JS interop — `JS_CALLS` (C#→JS) and `JS_INVOKES` (JS→C#)

Like the HTTP pass, each extractor collects its side of the interop surface and the **host**
matches them after every extractor runs (both endpoints then coexist as nodes in one branch):

- **`JS_CALLS` (C#→JS)** — Roslyn's `JsInteropPass` collects `IJSRuntime`/`IJSObjectReference`
  `InvokeAsync`/`InvokeVoidAsync` sites with a constant identifier, bound to the enclosing C#
  method, correlating a receiver back to its `import("./x.js")` module. oxc contributes the JS
  exports. The matcher links them.
- **`JS_INVOKES` (JS→C#)** — oxc collects `DotNet.invokeMethod[Async]("Asm","Id",…)` (static) and
  `objRef.invokeMethod[Async]("Id",…)` (instance) sites, bound to the enclosing JS function.
  Roslyn contributes `[JSInvokable]` methods (identifier = the attribute argument or method
  name). The matcher links them.

Both are gated by **`EDGEHOP_JS_INTEROP`**: `precise` (default) emits only unambiguous matches
— C#→JS by import-module leaf or globally-unique export name; JS→C# static by (assembly,
identifier) or instance by unique identifier. `broad` fans out by name only (more recall, some
plausible-but-wrong edges); `off` disables both. `get_callers` traverses both types.

Owner decisions: distinct edge types per direction (`JS_CALLS` ≠ `JS_INVOKES` — opposite
endpoints); only C#↔JS interop is built (JS/TS `fetch`→HTTP-endpoint stays out of scope, above).

### Anchor + verification

New regression anchor `fixtures/JsFixture` (**21 nodes / 25 edges**: CONTAINS 18, CALLS 2,
`JS_CALLS` 2, `JS_INVOKES` 3) — a Blazor component + collocated JS driven end-to-end through the
real Roslyn+oxc+host pipeline in precise mode, with anti-cases (non-constant identifier,
unexported name, the `ComponentBase.InvokeAsync(StateHasChanged)` dispatcher, no-target
`DotNet.invoke`, and skipped `*.min.js`/`node_modules` duplicates). **322 tests pass** (232
critical-path + 90 live-Neo4j conformance). Verified on the target: `get-callers` crosses each
interop boundary on the fixture; **Golf has zero `JS_INVOKES`** (no `DotNet.invoke*`/`[JSInvokable]`
— no false positives on a real ~4,150-edge solution).
