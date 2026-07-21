# Contributing to EdgeHop

Thanks for your interest in improving EdgeHop! This guide covers how to build, test, and
submit changes.

## Prerequisites

- **.NET 10 SDK**
- **Windows x64** — the JS/TS extractor ships a native win-x64 binary (`edgehop-oxc`), so
  the build and tests are Windows-only.
- **PowerShell** for the shell steps below.
- (Optional) **Neo4j** — only if you want to run the live Neo4j conformance tests. The
  default SQLite backend needs nothing extra.

## Build

```powershell
dotnet build EdgeHop.sln -c Release
```

> The test projects have no `ProjectReference` to the CLI/MCP/extractor executables by
> design (the tests drive the *real* built exes), so `dotnet test` does **not** rebuild them.
> Always `dotnet build` first.

## Test

```powershell
dotnet test tests/EdgeHop.Tests/EdgeHop.Tests.csproj -c Release --no-build
```

The live-Neo4j conformance suite (`tests/EdgeHop.Neo4j.Tests`) skips itself unless the
`NEO4J_*` environment variables are set, so the main suite runs credential-free.

### Regression anchors

Several fixtures and the two public sample solutions have **pinned node/edge counts** with a
matching `EXPECTED-GRAPH.md` contract. A count change is a *semantics* change: never adjust a
pinned count just to make a test pass. If your change legitimately alters the graph, update
the count **and** document why in that fixture's `EXPECTED-GRAPH.md` in the same commit.

## Changing the JS/TS extractor

The oxc extractor's native binary is **vendored** (committed). It is not rebuilt by
`dotnet build`. To change JS/TS extraction you must edit the Rust under
`Extractors/EdgeHop.Oxc/native/`, rebuild it, and re-vendor the binary. See the in-repo
development notes for the exact toolchain.

## Dependencies

Package versions are centrally pinned in `Directory.Packages.props`. **Do not** upgrade,
downgrade, substitute, or add packages as part of an unrelated change — a version bump is its
own, deliberate PR.

## Submitting changes

1. Branch off `main`.
2. Keep commits focused; write clear commit messages describing the *why*.
3. Make sure `dotnet build` and the main test suite are green.
4. Open a pull request describing the change and its motivation. CI (build + test on a
   Windows runner) must pass before merge.

## License

By contributing, you agree that your contributions will be licensed under the
[Apache License 2.0](LICENSE).
