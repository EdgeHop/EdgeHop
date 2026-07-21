# Changelog

All notable changes to EdgeHop are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0-alpha] — 2026-07-20

Initial public alpha. EdgeHop is distributed as a single `edgehop` .NET global tool
(package id `EdgeHop`).

### Added

- **C# code graph** from Roslyn's semantic model: symbols and resolved relationships
  (`CONTAINS`, `CALLS`, `IMPLEMENTS`, `INHERITS`, `OVERRIDES`, `REFERENCES`).
- **Blazor component graph** with `RENDERS` edges and route awareness.
- **Native JS/TS extractor** (vendored oxc binary, no Node.js) producing JS symbols merged
  into the same per-branch graph.
- **Cross-tier edges**: `HTTP_CALLS` (Web `HttpClient` → C# endpoint) and bidirectional
  C#↔JS interop (`JS_CALLS`, `JS_INVOKES`).
- **Pluggable storage** behind `EDGEHOP_BACKEND`: embedded SQLite (default, per-solution,
  zero-config) or Neo4j; and **pluggable extractors** loaded by reflection.
- **Query surface** as five MCP tools / CLI verbs: `find_symbol`, `get_callers`,
  `get_relationships`, `get_path`, `graph_stats`.
- **Branch-aware indexing** with incremental reconcile, watch mode, other-branch indexing via
  private worktrees, and background git hooks (`install-hooks` / `uninstall-hooks`).
- **CI/CD**: build + test on a Windows runner; `v*` tags publish to nuget.org via NuGet
  Trusted Publishing (OIDC, no stored API key).

[Unreleased]: https://github.com/EdgeHop/EdgeHop/compare/v0.1.0...HEAD
[0.1.0-alpha]: https://github.com/EdgeHop/EdgeHop/releases/tag/v0.1.0
