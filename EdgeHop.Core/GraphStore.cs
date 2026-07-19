namespace EdgeHop.Core;

/// <summary>
/// Write side of a graph backend: batched, idempotent upserts keyed on the composite
/// identity <c>(branch, id)</c>, plus branch-scoped deletes. Implementations MUST make a
/// cross-branch delete inexpressible: the branch is always a bound parameter/value on
/// every delete, never composed into query text (see <see cref="Neo4jGraphWriter"/>, the
/// reference implementation).
/// </summary>
public interface IGraphWriter
{
    /// <summary>Upserts <paramref name="nodes"/> by <c>(branch, id)</c>; re-running the
    /// same rows is idempotent (no duplicates, latest property values win).</summary>
    Task UpsertNodesAsync(IReadOnlyCollection<NodeRow> nodes);

    /// <summary>Upserts <paramref name="edges"/> by <c>(branch, type, fromId, toId)</c>.
    /// Every <see cref="EdgeRow.Type"/> must be in <see cref="EdgeTypes"/>; a batch
    /// containing any unknown type is rejected whole with <see cref="ArgumentException"/>
    /// before anything is written.</summary>
    Task UpsertEdgesAsync(IReadOnlyCollection<EdgeRow> edges);

    /// <summary>Surgically deletes the listed nodes (and every edge attached to them)
    /// under <paramref name="branch"/>.</summary>
    Task DeleteNodesAsync(string branch, IReadOnlyCollection<string> ids);

    /// <summary>Surgically deletes the listed edge identities under
    /// <paramref name="branch"/>. Unknown edge types reject the whole batch, exactly like
    /// <see cref="UpsertEdgesAsync"/>.</summary>
    Task DeleteEdgesAsync(string branch, IReadOnlyCollection<EdgeKey> edges);

    /// <summary>Deletes EVERYTHING stored under <paramref name="branch"/> and returns the
    /// number of nodes deleted. This backs the explicit <c>prune --yes</c> verb — callers
    /// own the confirmation.</summary>
    Task<long> DeleteBranchAsync(string branch);
}

/// <summary>
/// Key-set reads used by <see cref="GraphReconciler"/> (and the <c>branches</c> verb):
/// which node ids and edge identities a branch currently holds.
/// </summary>
public interface IGraphSnapshotReader
{
    /// <summary>All node ids currently stored under <paramref name="branch"/>.</summary>
    Task<IReadOnlyList<string>> GetNodeIdsAsync(
        string branch, CancellationToken cancellationToken = default);

    /// <summary>
    /// All edge identities currently stored under <paramref name="branch"/>. Edge types
    /// not in <see cref="EdgeTypes.All"/> are filtered out (reported via
    /// <paramref name="onUnknownType"/>) so an edge type this build does not know about
    /// can never end up in a delete plan.
    /// </summary>
    Task<IReadOnlyList<EdgeKey>> GetEdgeKeysAsync(
        string branch,
        Action<string>? onUnknownType = null,
        CancellationToken cancellationToken = default);

    /// <summary>Every distinct branch value in the store with its node count, ordered by
    /// branch name.</summary>
    Task<IReadOnlyList<(string Branch, long Nodes)>> GetBranchesAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-side queries over the code graph, backing <see cref="EdgeHopQueryService"/>
/// (and through it the MCP tools and the <c>edgehop</c> CLI). The validation contract —
/// argument checks, the <see cref="MaxLimit"/> clamp, the depth range — is part of THIS
/// interface so behavior is identical on every backend; implementations must not loosen
/// or tighten it.
/// </summary>
public interface IGraphReader
{
    /// <summary>Hard cap applied to the <c>limit</c> of <see cref="FindSymbolsAsync"/>.</summary>
    const int MaxLimit = 100;

    /// <summary>Smallest allowed <c>depth</c> for <see cref="GetCallersAsync"/>.</summary>
    const int MinDepth = 1;

    /// <summary>Largest allowed <c>depth</c> for <see cref="GetCallersAsync"/>.</summary>
    const int MaxDepth = 10;

    /// <summary>Largest allowed <c>depth</c> for <see cref="GetRelationshipsAsync"/>. A
    /// depth above 1 requires a single edge type (the service enforces this).</summary>
    const int MaxRelationshipDepth = 10;

    /// <summary>Largest allowed <c>maxLength</c> for <see cref="GetPathAsync"/> — bounds the
    /// bounded walk so it cannot explode.</summary>
    const int MaxPathLength = 15;

    /// <summary>Default <c>maxLength</c> for <see cref="GetPathAsync"/> when a caller does
    /// not specify one.</summary>
    const int DefaultPathLength = 10;

    /// <summary>Largest allowed <c>topN</c> for the god-node list of
    /// <see cref="GetStatsAsync"/>. The service clamps into [1, <see cref="MaxTopN"/>].</summary>
    const int MaxTopN = 50;

    /// <summary>
    /// Case-insensitive SUBSTRING match on the symbol name within
    /// <paramref name="branch"/>, with an optional exact <paramref name="kind"/> filter.
    /// No wildcards or regex — <c>*</c> and <c>?</c> match literally. Results ordered
    /// <c>name ASC, id ASC</c>. An empty/whitespace <paramref name="query"/> (or a
    /// <paramref name="limit"/> below 1) returns an empty list without touching storage;
    /// limits above <see cref="MaxLimit"/> are capped to it.
    /// </summary>
    Task<IReadOnlyList<SymbolHit>> FindSymbolsAsync(
        string branch,
        string query,
        string? kind = null,
        int limit = 25,
        CancellationToken ct = default);

    /// <summary>
    /// The distinct symbols that call <paramref name="symbolId"/> within
    /// <paramref name="branch"/>, following <c>CALLS</c>, <c>HTTP_CALLS</c>, <c>JS_CALLS</c> and
    /// <c>JS_INVOKES</c> edges (incoming, directional) up to <paramref name="depth"/> hops. The target itself is never included (relevant
    /// when call cycles reach back to it); unknown ids return an empty list. Results
    /// ordered <c>name ASC, id ASC</c>. Depth outside
    /// [<see cref="MinDepth"/>, <see cref="MaxDepth"/>] throws
    /// <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    Task<IReadOnlyList<SymbolHit>> GetCallersAsync(
        string branch,
        string symbolId,
        int depth = 1,
        CancellationToken ct = default);

    /// <summary>
    /// The distinct symbols related to <paramref name="symbolId"/> within
    /// <paramref name="branch"/>, reached by following edges in
    /// <paramref name="direction"/> up to <paramref name="depth"/> hops, optionally
    /// filtered to a single <paramref name="edgeType"/>. Each hit carries the edge type
    /// that reached it and the direction traversed. The anchor itself is never included;
    /// unknown ids return an empty list. The service validates ranges and rules (a depth
    /// above 1 requires a single <paramref name="edgeType"/>; any <paramref name="edgeType"/>
    /// must be in <see cref="EdgeTypes.All"/>) before calling — implementations re-guard
    /// only branch/id non-emptiness.
    /// </summary>
    Task<IReadOnlyList<RelationshipHit>> GetRelationshipsAsync(
        string branch,
        string symbolId,
        RelationshipDirection direction,
        string? edgeType,
        int depth,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// One shortest directed path from <paramref name="fromId"/> to <paramref name="toId"/>
    /// within <paramref name="branch"/>, following outgoing edges up to
    /// <paramref name="maxLength"/> hops, optionally restricted to a single
    /// <paramref name="edgeType"/>. Returns <see cref="PathResult.Found"/> false with empty
    /// nodes when unreachable; <paramref name="fromId"/> equal to <paramref name="toId"/>
    /// yields a found single-node path of length 0. The service validates ranges and the
    /// edge type before calling.
    /// </summary>
    Task<PathResult> GetPathAsync(
        string branch,
        string fromId,
        string toId,
        string? edgeType,
        int maxLength,
        CancellationToken ct = default);

    /// <summary>
    /// Per-branch orientation stats for <paramref name="branch"/>: total node/edge counts,
    /// node counts by kind, edge counts by type, and the top <paramref name="topN"/>
    /// god nodes by degree (excluding <c>CONTAINS</c> edges). The service clamps
    /// <paramref name="topN"/> into [1, <see cref="MaxTopN"/>] before calling.
    /// </summary>
    Task<GraphStatsResult> GetStatsAsync(
        string branch,
        int topN,
        CancellationToken ct = default);
}

/// <summary>
/// One graph backend: owns the connection/lifetime and schema setup that callers
/// previously juggled by hand (driver + settings + schema application), and exposes the
/// three backend surfaces. Obtain instances from <see cref="GraphStoreFactory"/> — the
/// single place backend selection lives.
/// </summary>
public interface IGraphStore : IAsyncDisposable
{
    /// <summary>Write surface (indexer, reconciler, prune).</summary>
    IGraphWriter Writer { get; }

    /// <summary>Key-set read surface (reconciler, branches verb, dry-run plans).</summary>
    IGraphSnapshotReader Snapshot { get; }

    /// <summary>Query surface (find-symbol / get-callers front ends).</summary>
    IGraphReader Reader { get; }

    /// <summary>Human-readable connection description for status output, e.g.
    /// <c>bolt://localhost:7687, database 'neo4j'</c>. Never contains credentials.</summary>
    string Description { get; }

    /// <summary>Applies backend schema (constraints/indexes/tables). Idempotent; safe to
    /// run on every startup.</summary>
    Task EnsureSchemaAsync(CancellationToken ct = default);
}
