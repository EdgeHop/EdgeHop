namespace EdgeHop.Core;

/// <summary>
/// A pluggable graph-store backend, discovered reflectively by
/// <see cref="GraphStoreFactory"/> from the assembly <c>EdgeHop.&lt;BackendName&gt;</c>
/// (e.g. <c>EdgeHop.Sqlite</c>, <c>EdgeHop.Neo4j</c>). Each implementation lives in its
/// OWN assembly that references its driver package; <see cref="EdgeHop.Core"/> references
/// neither driver, so nothing on the default (sqlite) path can JIT-resolve the Neo4j driver
/// — the isolation guarantee is structural, not by-convention.
/// <para>
/// Adding a new backend is: create <c>EdgeHop.&lt;Name&gt;</c> with a single public
/// parameterless <see cref="IGraphStoreProvider"/>, reference it from the front-end exes so
/// it deploys, and select it via <c>EDGEHOP_BACKEND=&lt;name&gt;</c>. No change to Core.
/// </para>
/// </summary>
public interface IGraphStoreProvider
{
    /// <summary>The <c>EDGEHOP_BACKEND</c> value this provider serves, lower-case
    /// (e.g. <c>"sqlite"</c>, <c>"neo4j"</c>). The factory validates the loaded provider's
    /// name matches the requested backend.</summary>
    string BackendName { get; }

    /// <summary>True when this backend's required configuration is present (typically env
    /// vars). Reads configuration ONLY — never opens a connection or touches the driver, so
    /// a false result is a cheap, side-effect-free probe (used by dry-run and test skips).</summary>
    bool IsConfigured { get; }

    /// <summary>Creates the store. Never opens a connection. May throw
    /// <see cref="InvalidOperationException"/> for missing required configuration (message
    /// names exactly what to set), or a driver argument/format exception for malformed
    /// settings (e.g. a bad URI) — front ends already translate both to a clean exit.</summary>
    /// <param name="pathHint">The caller's natural path, used only by backends with a
    /// path-derived store location (the SQLite store-per-solution derivation); ignored by
    /// others.</param>
    IGraphStore Create(string? pathHint);
}
