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

## Build (Windows)

The machine has no MSVC C++ tools, so build with the **gnu** toolchain (self-contained linker):

```powershell
cargo +stable-x86_64-pc-windows-gnu build --release
```

Then vendor the result (interim distribution — the repo has no remote/CI yet):

```powershell
Copy-Item target\release\edgehop-oxc.exe ..\tools\win-x64\edgehop-oxc.exe -Force
```

`..\tools\win-x64\edgehop-oxc.exe` is committed and copied next to the consuming executable by
`EdgeHop.Oxc.csproj`. `target/` is git-ignored; `Cargo.lock` is committed for reproducible builds.
