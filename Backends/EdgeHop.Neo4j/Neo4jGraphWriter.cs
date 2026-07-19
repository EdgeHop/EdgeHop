using Neo4j.Driver;

namespace EdgeHop.Core;

/// <summary>
/// Batched, idempotent Neo4j writer for <see cref="NodeRow"/> and <see cref="EdgeRow"/>.
/// Everything is upserted with <c>MERGE</c> on the composite key <c>(branch, id)</c> so
/// re-runs never create duplicates. Writes are batched with <c>UNWIND $rows AS row</c>
/// and chunked (~2000 rows per transaction) rather than one round-trip per row.
/// Deletes (<see cref="DeleteNodesAsync"/> / <see cref="DeleteEdgesAsync"/> /
/// <see cref="DeleteBranchAsync"/>) are always branch-scoped with the branch as a query
/// parameter — no code path can express a cross-branch delete.
/// The <see cref="IDriver"/> is thread-safe and shared; sessions are not, so this class
/// opens a session per call and never stores one.
/// </summary>
public sealed class Neo4jGraphWriter : IGraphWriter
{
    /// <summary>Rows per write transaction. "A few thousand" per the README; 2000 keeps
    /// transactions comfortably small while still amortizing round-trips.</summary>
    private const int ChunkSize = 2000;

    private const string UpsertNodesCypher = """
        UNWIND $rows AS row
        MERGE (s:Symbol {branch: row.branch, id: row.id})
        SET s.name = row.name, s.kind = row.kind, s.sourceDoc = row.sourceDoc,
            s.assembly = row.assembly, s.isAbstract = row.isAbstract,
            s.isComponent = row.isComponent, s.routes = row.routes
        """;

    private readonly IDriver _driver;
    private readonly string _database;

    public Neo4jGraphWriter(IDriver driver, string database)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        _driver = driver;
        _database = database;
    }

    /// <summary>Upserts <paramref name="nodes"/> in chunked <c>UNWIND … MERGE</c> transactions.</summary>
    public async Task UpsertNodesAsync(IReadOnlyCollection<NodeRow> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (nodes.Count == 0)
        {
            return;
        }

        var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        try
        {
            foreach (var chunk in nodes.Chunk(ChunkSize))
            {
                var rows = new List<object>(chunk.Length);
                foreach (var node in chunk)
                {
                    rows.Add(new Dictionary<string, object?>
                    {
                        ["branch"] = node.Branch,
                        ["id"] = node.Id,
                        ["name"] = node.Name,
                        ["kind"] = node.Kind,
                        ["sourceDoc"] = node.SourceDoc,
                        ["assembly"] = node.Assembly,
                        ["isAbstract"] = node.IsAbstract,
                        ["isComponent"] = node.IsComponent,
                        // SET s.routes = null REMOVES the property, which is the correct
                        // upsert semantics when a @page directive is deleted.
                        ["routes"] = node.Routes?.ToList(),
                    });
                }

                var parameters = new Dictionary<string, object> { ["rows"] = rows };
                await session.ExecuteWriteAsync(async tx =>
                {
                    var cursor = await tx.RunAsync(UpsertNodesCypher, parameters).ConfigureAwait(false);
                    return await cursor.ConsumeAsync().ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
        }
        finally
        {
            await session.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Upserts <paramref name="edges"/>. Cypher cannot parameterize a relationship TYPE,
    /// so edges are grouped by <see cref="EdgeRow.Type"/> and one <c>UNWIND</c> query is
    /// built per type — with the type token taken only from the <see cref="EdgeTypes"/>
    /// whitelist. Unknown types are rejected up front; unvalidated input is never spliced
    /// into Cypher text.
    /// </summary>
    /// <exception cref="ArgumentException">Any edge has a <see cref="EdgeRow.Type"/> not in <see cref="EdgeTypes"/>.</exception>
    public async Task UpsertEdgesAsync(IReadOnlyCollection<EdgeRow> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);
        if (edges.Count == 0)
        {
            return;
        }

        // Validate every type against the whitelist BEFORE any write is attempted, so a
        // bad batch fails whole rather than half-applied across types.
        var unknown = edges
            .Select(e => e.Type)
            .Where(t => !EdgeTypes.IsValid(t))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException(
                $"Unknown edge type(s): {string.Join(", ", unknown)}. " +
                $"Valid types: {string.Join(", ", EdgeTypes.All)}.",
                nameof(edges));
        }

        var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        try
        {
            foreach (var group in edges.GroupBy(e => e.Type, StringComparer.Ordinal))
            {
                // Safe by construction: group.Key was validated against EdgeTypes.All above,
                // so only a known constant token can ever reach the query text.
                var cypher = BuildEdgeUpsertCypher(group.Key);

                foreach (var chunk in group.Chunk(ChunkSize))
                {
                    var rows = new List<object>(chunk.Length);
                    foreach (var edge in chunk)
                    {
                        rows.Add(new Dictionary<string, object?>
                        {
                            ["branch"] = edge.Branch,
                            ["fromId"] = edge.FromId,
                            ["toId"] = edge.ToId,
                            ["sourceDoc"] = edge.SourceDoc,
                        });
                    }

                    var parameters = new Dictionary<string, object> { ["rows"] = rows };
                    await session.ExecuteWriteAsync(async tx =>
                    {
                        var cursor = await tx.RunAsync(cypher, parameters).ConfigureAwait(false);
                        return await cursor.ConsumeAsync().ConfigureAwait(false);
                    }).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await session.CloseAsync().ConfigureAwait(false);
        }
    }

    private const string DeleteNodesCypher = """
        UNWIND $ids AS id
        MATCH (s:Symbol {branch: $branch, id: id})
        DETACH DELETE s
        """;

    private const string DeleteBranchBatchCypher = """
        MATCH (s:Symbol {branch: $branch})
        WITH s LIMIT $limit
        DETACH DELETE s
        RETURN count(*) AS deleted
        """;

    /// <summary>
    /// Surgically deletes the listed nodes (and, via <c>DETACH</c>, every relationship
    /// attached to them) under <paramref name="branch"/>. Branch and ids are always
    /// query parameters — a cross-branch delete is inexpressible. Chunked.
    /// </summary>
    public async Task DeleteNodesAsync(string branch, IReadOnlyCollection<string> ids)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return;
        }

        var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        try
        {
            foreach (var chunk in ids.Chunk(ChunkSize))
            {
                var parameters = new Dictionary<string, object>
                {
                    ["branch"] = branch,
                    ["ids"] = chunk.ToList(),
                };
                await session.ExecuteWriteAsync(async tx =>
                {
                    var cursor = await tx.RunAsync(DeleteNodesCypher, parameters).ConfigureAwait(false);
                    return await cursor.ConsumeAsync().ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
        }
        finally
        {
            await session.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Surgically deletes the listed relationships under <paramref name="branch"/>.
    /// Edge types are validated against <see cref="EdgeTypes"/> up front (same whole-batch
    /// rejection as the upsert path); branch and endpoint ids stay parameters. Chunked
    /// per type.
    /// </summary>
    /// <exception cref="ArgumentException">Any key has a type not in <see cref="EdgeTypes"/>.</exception>
    public async Task DeleteEdgesAsync(string branch, IReadOnlyCollection<EdgeKey> edges)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentNullException.ThrowIfNull(edges);
        if (edges.Count == 0)
        {
            return;
        }

        var unknown = edges
            .Select(e => e.Type)
            .Where(t => !EdgeTypes.IsValid(t))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException(
                $"Unknown edge type(s): {string.Join(", ", unknown)}. " +
                $"Valid types: {string.Join(", ", EdgeTypes.All)}.",
                nameof(edges));
        }

        var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        try
        {
            foreach (var group in edges.GroupBy(e => e.Type, StringComparer.Ordinal))
            {
                var cypher = BuildEdgeDeleteCypher(group.Key);

                foreach (var chunk in group.Chunk(ChunkSize))
                {
                    var rows = new List<object>(chunk.Length);
                    foreach (var edge in chunk)
                    {
                        rows.Add(new Dictionary<string, object?>
                        {
                            ["fromId"] = edge.FromId,
                            ["toId"] = edge.ToId,
                        });
                    }

                    var parameters = new Dictionary<string, object>
                    {
                        ["branch"] = branch,
                        ["rows"] = rows,
                    };
                    await session.ExecuteWriteAsync(async tx =>
                    {
                        var cursor = await tx.RunAsync(cypher, parameters).ConfigureAwait(false);
                        return await cursor.ConsumeAsync().ConfigureAwait(false);
                    }).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await session.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes EVERYTHING stored under <paramref name="branch"/>, in batches of
    /// <see cref="ChunkSize"/> until none remain. This is the whole-branch prune backing
    /// the explicit <c>prune --yes</c> verb — callers own the confirmation; this method
    /// only guarantees the delete cannot escape the branch (branch is a parameter on the
    /// only MATCH). Returns the number of nodes deleted.
    /// </summary>
    public async Task<long> DeleteBranchAsync(string branch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        try
        {
            long total = 0;
            while (true)
            {
                var deleted = await session.ExecuteWriteAsync(async tx =>
                {
                    var cursor = await tx.RunAsync(
                        DeleteBranchBatchCypher,
                        new Dictionary<string, object>
                        {
                            ["branch"] = branch,
                            ["limit"] = ChunkSize,
                        }).ConfigureAwait(false);
                    var record = await cursor.SingleAsync().ConfigureAwait(false);
                    return record.Get<long>("deleted");
                }).ConfigureAwait(false);

                total += deleted;
                if (deleted < ChunkSize)
                {
                    return total;
                }
            }
        }
        finally
        {
            await session.CloseAsync().ConfigureAwait(false);
        }
    }

    private static string BuildEdgeDeleteCypher(string validatedEdgeType)
    {
        if (!EdgeTypes.IsValid(validatedEdgeType))
        {
            // Defense in depth: this method must never see an unvalidated token.
            throw new ArgumentException(
                $"'{validatedEdgeType}' is not a known edge type.",
                nameof(validatedEdgeType));
        }

        return $$"""
            UNWIND $rows AS row
            MATCH (:Symbol {branch: $branch, id: row.fromId})
                  -[r:{{validatedEdgeType}} {branch: $branch}]->
                  (:Symbol {branch: $branch, id: row.toId})
            DELETE r
            """;
    }

    private static string BuildEdgeUpsertCypher(string validatedEdgeType)
    {
        if (!EdgeTypes.IsValid(validatedEdgeType))
        {
            // Defense in depth: this method must never see an unvalidated token.
            throw new ArgumentException(
                $"'{validatedEdgeType}' is not a known edge type.",
                nameof(validatedEdgeType));
        }

        return $$"""
            UNWIND $rows AS row
            MATCH (a:Symbol {branch: row.branch, id: row.fromId})
            MATCH (b:Symbol {branch: row.branch, id: row.toId})
            MERGE (a)-[r:{{validatedEdgeType}} {branch: row.branch}]->(b)
            SET r.sourceDoc = row.sourceDoc
            """;
    }
}
