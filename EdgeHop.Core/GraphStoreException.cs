namespace EdgeHop.Core;

/// <summary>
/// A backend-neutral wrapper for a graph store's native runtime failure (a Neo4j driver
/// connectivity/auth/query error, a SQLite locked/corrupt/unwritable-file error, …). It
/// exists so front ends can react to "the store failed" without referencing any backend's
/// driver type: <see cref="TranslatingGraphReader"/> converts each backend's native
/// exception into this Core type at the read boundary, and callers such as the
/// <c>edgehop</c> CLI catch <b>this</b> instead of <c>Neo4jException</c> /
/// <c>SqliteException</c>. That keeps the driver assemblies out of every front end's IL —
/// the isolation guarantee behind the pluggable-backend split.
/// </summary>
public sealed class GraphStoreException : Exception
{
    public GraphStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
