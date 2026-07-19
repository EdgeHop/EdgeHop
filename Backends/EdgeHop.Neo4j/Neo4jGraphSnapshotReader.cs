using Neo4j.Driver;

namespace EdgeHop.Core;

/// <summary>
/// Key-set reads used by <see cref="GraphReconciler"/> (and the <c>branches</c> verb):
/// which node ids and edge identities a branch currently holds. Branch-scoped; every
/// value is a query parameter. Session per call, mirroring the other Neo4j classes.
/// </summary>
public sealed class Neo4jGraphSnapshotReader : IGraphSnapshotReader
{
    private const string NodeIdsCypher = """
        MATCH (s:Symbol {branch: $branch})
        RETURN s.id AS id
        """;

    private const string EdgeKeysCypher = """
        MATCH (a:Symbol {branch: $branch})-[r {branch: $branch}]->(b:Symbol {branch: $branch})
        RETURN type(r) AS type, a.id AS fromId, b.id AS toId
        """;

    private const string BranchesCypher = """
        MATCH (s:Symbol)
        RETURN s.branch AS branch, count(s) AS nodes
        ORDER BY branch
        """;

    private readonly IDriver _driver;
    private readonly string _database;

    public Neo4jGraphSnapshotReader(IDriver driver, string database)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        _driver = driver;
        _database = database;
    }

    /// <summary>All node ids currently stored under <paramref name="branch"/>.</summary>
    public async Task<IReadOnlyList<string>> GetNodeIdsAsync(
        string branch, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        try
        {
            return await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync(
                    NodeIdsCypher,
                    new Dictionary<string, object> { ["branch"] = branch }).ConfigureAwait(false);
                var records = await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);
                return (IReadOnlyList<string>)records.Select(r => r.Get<string>("id")).ToList();
            }).ConfigureAwait(false);
        }
        finally
        {
            await session.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// All edge identities currently stored under <paramref name="branch"/>.
    /// Relationship types not in <see cref="EdgeTypes.All"/> are filtered out client-side
    /// (reported via <paramref name="onUnknownType"/>) so an edge type this build does
    /// not know about can never end up in a delete plan.
    /// </summary>
    public async Task<IReadOnlyList<EdgeKey>> GetEdgeKeysAsync(
        string branch,
        Action<string>? onUnknownType = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        try
        {
            return await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync(
                    EdgeKeysCypher,
                    new Dictionary<string, object> { ["branch"] = branch }).ConfigureAwait(false);
                var records = await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);

                var keys = new List<EdgeKey>(records.Count);
                foreach (var record in records)
                {
                    var type = record.Get<string>("type");
                    if (!EdgeTypes.IsValid(type))
                    {
                        onUnknownType?.Invoke(type);
                        continue;
                    }

                    keys.Add(new EdgeKey(type, record.Get<string>("fromId"), record.Get<string>("toId")));
                }

                return (IReadOnlyList<EdgeKey>)keys;
            }).ConfigureAwait(false);
        }
        finally
        {
            await session.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Every distinct branch value in the store with its node count.</summary>
    public async Task<IReadOnlyList<(string Branch, long Nodes)>> GetBranchesAsync(
        CancellationToken cancellationToken = default)
    {
        var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        try
        {
            return await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync(BranchesCypher).ConfigureAwait(false);
                var records = await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);
                return (IReadOnlyList<(string, long)>)records
                    .Select(r => (r.Get<string>("branch"), r.Get<long>("nodes")))
                    .ToList();
            }).ConfigureAwait(false);
        }
        finally
        {
            await session.CloseAsync().ConfigureAwait(false);
        }
    }
}
