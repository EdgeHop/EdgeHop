# Third-Party Notices

EdgeHop (licensed under Apache-2.0, see `LICENSE`) is built on and distributes
components from the third-party open-source projects listed below. This file is
provided to satisfy the attribution requirements of their licenses.

Two distribution surfaces carry third-party code:

- **The native `edgehop-oxc` binary** — a Rust program that statically links the
  crates in `Extractors/EdgeHop.Oxc/native/Cargo.lock`. These are compiled into the
  shipped binary, so their license/copyright notices travel with EdgeHop.
- **The .NET assemblies** — the Roslyn, MCP, store-driver, and hosting packages
  restored from NuGet (`Directory.Packages.props`) and deployed alongside the tool.

---

## Native binary (`edgehop-oxc`, Rust)

### oxc — The JavaScript Oxidation Compiler

The `edgehop-oxc` JS/TS parser is built on **oxc** (`oxc`, `oxc_parser`,
`oxc_semantic`, `oxc_ast`, and the related `oxc_*` crates).

- Project: https://github.com/oxc-project/oxc
- License: MIT

```
MIT License

Copyright (c) 2023 Boshen

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### serde / serde_json

- Projects: https://github.com/serde-rs/serde, https://github.com/serde-rs/json
- License: MIT OR Apache-2.0

### Other transitive crates

The `edgehop-oxc` binary also links the following crates (from `Cargo.lock`),
each licensed under permissive terms — predominantly **MIT**, **Apache-2.0**
(often dual `MIT OR Apache-2.0`), or the **Unicode** license for the
`unicode-*` data crates. Their copyright belongs to their respective authors.

```
allocator-api2, autocfg, bitflags, castaway, cfg-if, compact_str, cow-utils,
dragonbox_ecma, either, fastrand, hashbrown, itertools, itoa, memchr, nonmax,
num-bigint, num-integer, num-traits, owo-colors, oxc-miette, oxc-miette-derive,
percent-encoding, phf, phf_generator, phf_macros, phf_shared, proc-macro2,
quote, rustc-hash, rustversion, ryu, self_cell, seq-macro, siphasher, smallvec,
smawk, static_assertions, syn, textwrap, thiserror, thiserror-impl,
unicode-id-start, unicode-ident, unicode-linebreak, unicode-segmentation,
unicode-width, zmij
```

The authoritative, versioned list — with the exact resolved versions — is
`Extractors/EdgeHop.Oxc/native/Cargo.lock`. Per-crate license text can be
regenerated from that lockfile with `cargo about` or `cargo license`.

---

## .NET assemblies (NuGet)

Pinned versions are in `Directory.Packages.props`.

### Roslyn — .NET Compiler Platform

- `Microsoft.CodeAnalysis.CSharp.Workspaces`, `Microsoft.CodeAnalysis.Workspaces.MSBuild`
- Project: https://github.com/dotnet/roslyn
- License: MIT — Copyright (c) .NET Foundation and Contributors

### Microsoft.Build.Locator

- Project: https://github.com/microsoft/MSBuildLocator
- License: MIT — Copyright (c) Microsoft Corporation

### Microsoft.Data.Sqlite / SQLitePCLRaw

- Projects: https://github.com/dotnet/efcore, https://github.com/ericsink/SQLitePCL.raw
- Licenses: MIT (Microsoft.Data.Sqlite) / Apache-2.0 (SQLitePCLRaw).
  The bundled `e_sqlite3` native library wraps **SQLite**, which is in the
  public domain (https://www.sqlite.org/copyright.html).

### Neo4j.Driver

- Project: https://github.com/neo4j/neo4j-dotnet-driver
- License: Apache-2.0 — Copyright (c) Neo4j Sweden AB

### ModelContextProtocol (MCP C# SDK)

- Project: https://github.com/modelcontextprotocol/csharp-sdk
- License: MIT — Copyright (c) Anthropic and Model Context Protocol contributors

### Microsoft.Extensions.Hosting

- Project: https://github.com/dotnet/runtime
- License: MIT — Copyright (c) .NET Foundation and Contributors

---

Build-time-only tooling (`Nerdbank.GitVersioning`, MIT) and test-only packages
(`xunit`, `Microsoft.NET.Test.Sdk`, both MIT/Apache-2.0) are not part of the
distributed tool and are listed here only for completeness.
