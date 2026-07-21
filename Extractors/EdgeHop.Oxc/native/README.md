# edgehop-oxc — native JS/TS extractor for EdgeHop

A small Rust binary built on [oxc](https://oxc.rs) (`oxc_parser` + `oxc_semantic`) that reads
a JSON request from stdin and writes a graph JSON to stdout. It is the JS/TS equivalent of the
Roslyn extractor: `EdgeHop.Oxc` (the .NET plugin one directory up) shells out to it — **no
Node.js runtime**.

## Contract

- **stdin**: `{ "modules": [ { "moduleId", "sourceDoc", "lang", "source" } ] }`
  (`lang` ∈ `ts` | `tsx` | `js` | `jsx`).
- **stdout**: `{ "nodes": [...], "edges": [...], "interopExports": [...], "diagnostics": [...] }`.
  Every node `id` carries a mandatory `js|` tier tag (`Method:js|src/api/client.ts#getWidget`) so
  it can never collide with a C# (Roslyn) id.

## Coverage

Full JS/TS/JSX syntax + scope/binding resolution (oxc_semantic): module/class/function/field
nodes, `CONTAINS`, and `CALLS` for calls that bind to a declared function/class. Type-checker-
driven member resolution (`foo.bar()` via `foo`'s inferred type) is intentionally out of scope —
oxc does not type-check.

## Vendored layout

The binary is vendored per RID under `..\tools\<rid>\`, and `EdgeHop.Oxc.csproj` links each into
`runtimes/<rid>/native/` next to the consuming executable (the layout `OxcExtractor.LocateBinary`
probes for the running RID):

| RID          | Rust target                  | Binary            | Produced by            |
|--------------|------------------------------|-------------------|------------------------|
| `win-x64`    | `x86_64-pc-windows-gnu`      | `edgehop-oxc.exe` | committed (built here) |
| `linux-x64`  | `x86_64-unknown-linux-gnu`   | `edgehop-oxc`     | CI matrix              |
| `osx-arm64`  | `aarch64-apple-darwin`       | `edgehop-oxc`     | CI matrix              |

Only `win-x64` is committed; the CI oxc matrix job cross-compiles the Unix binaries and drops them
into `..\tools\linux-x64\` / `..\tools\osx-arm64\` before build/pack.

## Build

### win-x64 (locally, on Windows)

The machine has no MSVC C++ tools, so build with the **gnu** toolchain (self-contained linker),
then vendor the result:

```powershell
cargo +stable-x86_64-pc-windows-gnu build --release
Copy-Item target\release\edgehop-oxc.exe ..\tools\win-x64\edgehop-oxc.exe -Force
```

### linux-x64 / osx-arm64 (CI, cross-compiled)

These match what the CI matrix runs on each target host:

```sh
# linux-x64 (ubuntu runner)
cargo build --release --target x86_64-unknown-linux-gnu
cp target/x86_64-unknown-linux-gnu/release/edgehop-oxc ../tools/linux-x64/edgehop-oxc

# osx-arm64 (macos runner)
cargo build --release --target aarch64-apple-darwin
cp target/aarch64-apple-darwin/release/edgehop-oxc ../tools/osx-arm64/edgehop-oxc
```

`target/` is git-ignored; `Cargo.lock` is committed for reproducible builds.
