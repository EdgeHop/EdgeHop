<p align="center">
  <img src="https://raw.githubusercontent.com/EdgeHop/EdgeHop/main/Assets/Images/edgehop-title.svg" alt="EdgeHop" width="100%">
</p>

<h3 align="center">Stop grepping. Start knowing.</h3>

<p align="center">
  <a href="https://github.com/EdgeHop/EdgeHop/actions/workflows/ci.yml"><img src="https://github.com/EdgeHop/EdgeHop/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://www.nuget.org/packages/EdgeHop"><img src="https://img.shields.io/nuget/vpre/EdgeHop.svg" alt="NuGet"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-Apache_2.0-blue.svg" alt="License: Apache 2.0"></a>
</p>

**A code graph for your .NET solution that an AI coding assistant can query instead of grepping.**

EdgeHop builds an accurate, durable graph of the symbols in a .NET solution — and the
relationships between them — then serves it to an AI assistant over the
[Model Context Protocol (MCP)](https://modelcontextprotocol.io). Instead of guessing at
call chains from text search, your assistant asks *"who calls this method?"*, *"what
implements this interface?"*, or *"what breaks if I change this?"* and gets answers derived
from the compiler's own semantic model — including across the C#-to-JavaScript boundary.

---

## What it does

- **Indexes** a `.sln` (or a plain project directory) into a code graph: C# symbols from
  Roslyn's semantic model, and JavaScript/TypeScript symbols from a native
  [oxc](https://oxc.rs) parse — merged into one graph per git branch.
- **Captures real relationships**, not text matches: calls, interface implementations,
  inheritance, overrides, type references, containment, Blazor component rendering, and two
  cross-tier bridges — HTTP client-to-endpoint calls and bidirectional C#↔JS interop.
- **Serves the graph over MCP** so an assistant (e.g. Claude Code) can traverse it with five
  focused tools, and over a plain CLI for scripting.
- **Stays current** with your working tree via watch mode and optional git hooks that
  re-index in the background on commit, merge, and checkout — per branch, without touching
  your working directory.

## Why it's different

Most "code intelligence for AI" tooling falls into one of two camps, and EdgeHop
deliberately avoids both:

- **Text / Tree-sitter indexers** parse syntax without resolving symbols. They can tell you
  a token named `Save` appears in twelve files; they can't tell you *which* `Save` a given
  call actually binds to. EdgeHop uses **Roslyn's semantic model**, so a `CALLS` edge is a
  resolved invocation, not a name collision — the difference between a guess and an answer.
- **Monolithic `graph.json` extractors** (e.g. Graphify-style tools) dump the whole graph to
  a single file that must be regenerated wholesale on every change. EdgeHop stores the graph
  in an **indexed, durable, incrementally-reconciled** database and updates only what
  changed, so it scales and stays truthful as you work.

Beyond that, three things are unusual:

- **Cross-tier edges.** EdgeHop links a Web-tier `HttpClient` call to the C# method that
  serves the matching endpoint, and links C#↔JavaScript interop in *both* directions
  (`IJSRuntime.InvokeAsync` → JS export, and JS `DotNet.invoke*` → `[JSInvokable]`). Call
  chains stay walkable straight across boundaries a compiler can't cross.
- **Branch-aware and local-first.** The graph is scoped per git branch and lives entirely on
  your machine. No shared server, no credentials, no telemetry. Switch branches and the next
  query reflects it.
- **Pluggable storage and extractors.** SQLite by default (embedded, zero-config), Neo4j
  optional. The C# and JS extractors are independent plugins.

## Requirements

- **.NET 10 SDK/runtime**
- **Windows x64** — the bundled `edgehop-oxc` JS/TS parser is a native win-x64 binary
- No database or credentials for the default SQLite backend. Neo4j is opt-in.

## Installation

EdgeHop ships as a single .NET global tool:

```powershell
dotnet tool install -g EdgeHop --prerelease
dotnet tool update  -g EdgeHop --prerelease   # later, to upgrade
```

## Quick start

**1. Index a solution (or a directory):**

```powershell
edgehop index C:\path\to\YourApp.sln
```

**2. Query it from the CLI:**

```powershell
edgehop find-symbol OrderService
edgehop get-callers <symbolId> --depth 3
edgehop get-relationships <symbolId> --edge-type IMPLEMENTS
edgehop get-path <fromId> <toId>
edgehop stats
```

Add `--json` to any query verb for machine-readable output.

**3. Wire it into your AI assistant** — point an MCP client at the `edgehop mcp` command.
For Claude Code, add to `.mcp.json`:

```json
{
  "mcpServers": {
    "edgehop": {
      "command": "edgehop",
      "args": ["mcp"],
      "env": { "EDGEHOP_REPO": "C:\\path\\to\\your\\solution" }
    }
  }
}
```

**4. Keep it fresh** — watch mode, or install background git hooks:

```powershell
edgehop index C:\path\to\YourApp.sln --watch
edgehop install-hooks C:\path\to\YourApp.sln   # re-index on commit / merge / checkout
```

## What the graph captures

**Node kinds:** namespaces, types (class/struct/interface/enum), methods, properties,
fields, Blazor components, and JS/TS functions.

**Edge types:**

| type         | meaning                                                            |
|--------------|-------------------------------------------------------------------|
| `CONTAINS`   | namespace → type, type → member                                   |
| `CALLS`      | method invocation (resolved via the semantic model)               |
| `IMPLEMENTS` | class/struct implements interface                                 |
| `INHERITS`   | derived type → base type                                          |
| `OVERRIDES`  | override → overridden method                                      |
| `REFERENCES` | a symbol uses a type (parameter, return, field type, …)           |
| `RENDERS`    | a Blazor component renders a child component in its markup         |
| `HTTP_CALLS` | a Web-tier `HttpClient` call → the C# method serving that endpoint |
| `JS_CALLS`   | C# `IJSRuntime` interop call → the JS function it invokes          |
| `JS_INVOKES` | JS `DotNet.invoke*` → the `[JSInvokable]` C# method it targets     |

## The MCP query surface

| tool                 | answers                                                              |
|----------------------|---------------------------------------------------------------------|
| `find_symbol`        | where a symbol lives; whether it's a component; which route it serves|
| `get_callers`        | who calls a method, N hops deep — across HTTP and JS interop too     |
| `get_relationships`  | everything related to a symbol by any edge type; filter by direction/type |
| `get_path`           | the shortest directed path between two symbols (reachability/impact) |
| `graph_stats`        | per-branch totals, counts by kind and edge type, and the busiest nodes |

Text and content search stays your assistant's `grep` job — EdgeHop indexes *structure*, not
text.

## Configuration

All configuration is environment variables (no config file, no stored credentials):

| variable            | purpose                                                              |
|---------------------|----------------------------------------------------------------------|
| `EDGEHOP_BACKEND`   | `sqlite` (default) or `neo4j`                                         |
| `EDGEHOP_SQLITE_PATH` | override the store file (default is derived per repo under `%LOCALAPPDATA%\EdgeHop\stores\`) |
| `EDGEHOP_REPO`      | which repo's current branch the MCP server follows                   |
| `EDGEHOP_BRANCH`    | force a branch (otherwise resolved from git)                         |
| `EDGEHOP_EXTRACTORS`| subset the loaded extractors (default: all)                          |
| `EDGEHOP_JS_INTEROP`| C#↔JS interop match mode: `precise` (default) / `broad` / `off`      |
| `NEO4J_URI` etc.    | Neo4j connection info, read only when `EDGEHOP_BACKEND=neo4j`         |

The SQLite store is per-solution and derived from the repo, so multiple solutions index side
by side with zero configuration.

## How it works

```
 C#/Razor ──▶ Roslyn extractor ─┐
                                 ├─▶ reconcile (per-branch diff) ─▶ graph store ─▶ MCP / CLI
 JS/TS ─────▶ oxc extractor  ────┘                                   (SQLite/Neo4j)
```

Extraction is whole-solution; storage is incremental — each index run reconciles the new
graph against the stored one for that branch and applies only the difference. Stores and
extractors are reflection-loaded plugins, so neither the core nor the host depends on a
specific database driver or on MSBuild.

## Contributing

Contributions are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md) for how to build, test,
and submit changes.

## License

Licensed under the [Apache License 2.0](LICENSE).
