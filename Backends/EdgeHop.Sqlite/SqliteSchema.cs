using Microsoft.Data.Sqlite;

namespace EdgeHop.Core;

/// <summary>
/// The SQLite backend's schema, mirroring the Neo4j DDL's role: composite identity
/// <c>(branch, id)</c> as the node primary key, <c>(branch, type, fromId, toId)</c> as
/// the edge primary key (exactly the <see cref="EdgeKey"/> identity within a branch),
/// plus the lookup indexes the two query shapes need. Every statement is
/// <c>IF NOT EXISTS</c>, so application is idempotent and safe on every startup.
/// </summary>
public static class SqliteSchema
{
    /// <summary>All DDL statements, in application order.</summary>
    public static readonly IReadOnlyList<string> AllStatements = new[]
    {
        """
        CREATE TABLE IF NOT EXISTS nodes (
            branch      TEXT NOT NULL,
            id          TEXT NOT NULL,
            name        TEXT NOT NULL,
            kind        TEXT NOT NULL,
            sourceDoc   TEXT NULL,
            assembly    TEXT NOT NULL,
            isAbstract  INTEGER NOT NULL,
            isComponent INTEGER NOT NULL,
            routes      TEXT NULL,
            PRIMARY KEY (branch, id)
        );
        """,
        "CREATE INDEX IF NOT EXISTS idx_nodes_branch_name ON nodes(branch, name);",
        "CREATE INDEX IF NOT EXISTS idx_nodes_branch_kind ON nodes(branch, kind);",
        """
        CREATE TABLE IF NOT EXISTS edges (
            branch    TEXT NOT NULL,
            type      TEXT NOT NULL,
            fromId    TEXT NOT NULL,
            toId      TEXT NOT NULL,
            sourceDoc TEXT NULL,
            PRIMARY KEY (branch, type, fromId, toId)
        );
        """,
        "CREATE INDEX IF NOT EXISTS idx_edges_branch_to ON edges(branch, toId, type);",
        "CREATE INDEX IF NOT EXISTS idx_edges_branch_from ON edges(branch, fromId);",
    };

    /// <summary>
    /// Applies the DDL over <paramref name="connection"/> (already open) and switches the
    /// database to WAL journaling so readers (MCP server) never block on the writer
    /// (indexer) across processes — the two-process behavior validated in the Gate 0
    /// spike. WAL is a persistent database property; setting it repeatedly is a no-op.
    /// </summary>
    public static void Apply(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode = WAL;";
        pragma.ExecuteNonQuery();

        foreach (var statement in AllStatements)
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }
    }
}
