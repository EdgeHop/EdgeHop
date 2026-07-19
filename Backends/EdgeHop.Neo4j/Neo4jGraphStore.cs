using Neo4j.Driver;

namespace EdgeHop.Core;

/// <summary>
/// The Neo4j backend: wraps driver creation from <see cref="Neo4jSettings"/> and schema
/// application (<see cref="Neo4jSchema.ApplyAsync"/>) behind <see cref="IGraphStore"/>,
/// so callers no longer juggle <c>IDriver</c> + settings + schema themselves. The
/// <see cref="IDriver"/> is created once (thread-safe, process-wide) and disposed with
/// the store; the per-surface classes open a session per call as before.
/// </summary>
/// <remarks>
/// Construction throws the driver's own <see cref="ArgumentException"/> /
/// <see cref="FormatException"/> / <see cref="NotSupportedException"/> on a malformed
/// URI — front ends catch these and report a configuration error (never echoing the raw
/// URI, which could embed userinfo credentials). Construction does not open a network
/// connection.
/// </remarks>
public sealed class Neo4jGraphStore : IGraphStore
{
    private readonly IDriver _driver;
    private readonly string _database;

    public Neo4jGraphStore(Neo4jSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _driver = GraphDatabase.Driver(
            settings.Uri, AuthTokens.Basic(settings.User, settings.Password));
        _database = settings.Database;
        Description = $"{settings.Uri}, database '{settings.Database}'";
        Writer = new Neo4jGraphWriter(_driver, settings.Database);
        Snapshot = new Neo4jGraphSnapshotReader(_driver, settings.Database);
        // Translate the driver's native Neo4jException to a Core GraphStoreException at the
        // read boundary, so front ends handle store failures without referencing this driver.
        Reader = new TranslatingGraphReader(
            new Neo4jGraphReader(_driver, settings.Database), static ex => ex is Neo4jException);
    }

    public IGraphWriter Writer { get; }

    public IGraphSnapshotReader Snapshot { get; }

    public IGraphReader Reader { get; }

    public string Description { get; }

    /// <summary>Applies the README DDL (uniqueness constraint + lookup indexes); every
    /// statement is <c>IF NOT EXISTS</c>, so this is idempotent.</summary>
    public Task EnsureSchemaAsync(CancellationToken ct = default) =>
        Neo4jSchema.ApplyAsync(_driver, _database);

    public ValueTask DisposeAsync() => _driver.DisposeAsync();
}
