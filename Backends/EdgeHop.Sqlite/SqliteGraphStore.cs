using Microsoft.Data.Sqlite;

namespace EdgeHop.Core;

/// <summary>
/// The SQLite backend (Handoff 3): an embedded, serverless store — no JVM, no
/// credentials, branch isolation via the same composite-key model as Neo4j with every
/// statement branch-scoped and parameterized. Chosen over LiteGraph by the Gate 0
/// performance spike.
/// <para>
/// Connection strategy: one short-lived <see cref="SqliteConnection"/> per operation
/// (mirroring the Neo4j session-per-call pattern), with pooling DISABLED so disposing the
/// store releases every file handle deterministically — tests delete their temp stores,
/// and Windows will not delete a file something still holds. The schema is ensured
/// lazily on the first operation (cheap, idempotent), so every surface works against a
/// fresh file — an empty store answers queries with empty results, exactly like an empty
/// Neo4j database. Cross-process contention (indexer + MCP server) is handled by WAL
/// journaling plus SQLite's busy handler (Microsoft.Data.Sqlite retries for the command
/// timeout, default 30 s).
/// </para>
/// </summary>
public sealed class SqliteGraphStore : IGraphStore
{
    private readonly string _connectionString;
    private readonly string _databasePath;
    private bool _schemaEnsured;

    public SqliteGraphStore(SqliteSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.DatabasePath);

        _databasePath = Path.GetFullPath(settings.DatabasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Pooling = false,
        }.ToString();

        Description = $"sqlite '{_databasePath}'";
        Writer = new SqliteGraphWriter(OpenConnection);
        Snapshot = new SqliteGraphSnapshotReader(OpenConnection);
        // Translate the driver's native SqliteException to a Core GraphStoreException at the
        // read boundary, so front ends handle store failures without referencing this driver.
        Reader = new TranslatingGraphReader(
            new SqliteGraphReader(OpenConnection), static ex => ex is SqliteException);
    }

    public IGraphWriter Writer { get; }

    public IGraphSnapshotReader Snapshot { get; }

    public IGraphReader Reader { get; }

    public string Description { get; }

    public Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        using var connection = OpenConnection();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask; // no pooled handles to release

    /// <summary>
    /// Opens a connection, creating the containing directory and applying the schema on
    /// this store's first open. Handed to the three surface classes as their connection
    /// source — they never compose connection strings themselves.
    /// </summary>
    private SqliteConnection OpenConnection()
    {
        if (!_schemaEnsured)
        {
            var directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        if (!_schemaEnsured)
        {
            SqliteSchema.Apply(connection);
            _schemaEnsured = true;
        }

        return connection;
    }
}
