namespace EdgeHop.Core;

/// <summary>Result of a symbol search: the matching symbols plus a flag that is true when
/// the result was cut off at the limit (more matches exist — narrow the query).</summary>
public sealed record SymbolSearchResult(IReadOnlyList<SymbolHit> Hits, bool Truncated);

/// <summary>Result of a caller search: the target id and depth echoed back, plus the
/// distinct callers found within that many CALLS/HTTP_CALLS/JS_CALLS/JS_INVOKES hops (the target
/// itself excluded).</summary>
public sealed record CallerSearchResult(string TargetId, int Depth, IReadOnlyList<SymbolHit> Callers);

/// <summary>One related symbol plus the edge that reached it and the direction traversed.</summary>
public sealed record RelationshipHit(SymbolHit Symbol, string EdgeType, string Direction);

/// <summary>Result of a relationship search: the target id, direction, edge-type filter and
/// depth echoed back, plus the related hits and a flag that is true when the result was cut
/// off at the fan-out cap (more relations exist — narrow with an edge type).</summary>
public sealed record RelationshipSearchResult(
    string TargetId, string Direction, string? EdgeType, int Depth,
    IReadOnlyList<RelationshipHit> Hits, bool Truncated);

/// <summary>One node on a path (index 0 = fromId). <see cref="EdgeTypeFromPrev"/> is null
/// for index 0, else the edge type linking the previous node to this one.</summary>
public sealed record PathNode(SymbolHit Symbol, string? EdgeTypeFromPrev);

/// <summary>Result of a path search: the endpoints echoed back, whether a path was found,
/// and the ordered nodes (empty when not found; a single node for a zero-length path).</summary>
public sealed record PathResult(
    string FromId, string ToId, bool Found, IReadOnlyList<PathNode> Nodes);

/// <summary>Node count for one <see cref="SymbolKinds"/> value.</summary>
public sealed record KindCount(string Kind, long Count);

/// <summary>Edge count for one <see cref="EdgeTypes"/> value.</summary>
public sealed record EdgeTypeCount(string Type, long Count);

/// <summary>One god node: a symbol and its degree (edges excluding <c>CONTAINS</c>).</summary>
public sealed record DegreeHit(SymbolHit Symbol, long Degree);

/// <summary>Per-branch orientation stats: totals, node counts by kind, edge counts by type,
/// and the highest-degree god nodes (degree excludes <c>CONTAINS</c>).</summary>
public sealed record GraphStatsResult(
    string Branch, long TotalNodes, long TotalEdges,
    IReadOnlyList<KindCount> NodesByKind, IReadOnlyList<EdgeTypeCount> EdgesByType,
    IReadOnlyList<DegreeHit> GodNodes);

/// <summary>
/// Transport-neutral query facade over <see cref="IGraphReader"/> — the single backend
/// shared by every front end: the MCP server (<c>EdgeHop.Mcp</c>, for collaborative or
/// offloaded use) and the <c>edgehop</c> CLI (<c>EdgeHop.Cli</c>, for local use).
/// All behavior that must be identical across front ends lives here: input validation,
/// the limit clamp, and exact truncation detection. Front ends only translate errors and
/// results into their own surface (<c>McpException</c> / stderr + exit codes) and must
/// not reimplement any of this logic.
/// <para>
/// SEARCH SEMANTICS: <see cref="FindSymbolsAsync"/> is a case-insensitive substring match.
/// There are NO wildcards and no regex — <c>*</c> and <c>?</c> are matched as literal
/// characters — so every search already behaves like <c>*query*</c>.
/// </para>
/// <para>
/// Throws plain BCL exceptions only (<see cref="ArgumentException"/> /
/// <see cref="ArgumentOutOfRangeException"/>): EdgeHop.Core has no dependency on any
/// front-end technology.
/// </para>
/// </summary>
public sealed class EdgeHopQueryService(IGraphReader reader)
{
    /// <summary>Default number of hits a symbol search returns.</summary>
    public const int DefaultLimit = 25;

    /// <summary>
    /// Largest effective limit a caller may request — one less than
    /// <see cref="IGraphReader.MaxLimit"/>, because truncation detection probes for
    /// limit + 1 rows and that probe must stay within the reader's hard cap for the
    /// <see cref="SymbolSearchResult.Truncated"/> flag to remain exact.
    /// </summary>
    public const int MaxRequestLimit = IGraphReader.MaxLimit - 1;

    private readonly IGraphReader _reader =
        reader ?? throw new ArgumentNullException(nameof(reader));

    /// <summary>
    /// Canonical user-visible form of a BCL exception message thrown by this service:
    /// the first line only, with the trailing <c>(Parameter 'name')</c> decoration
    /// removed — i.e. the human-authored sentence. EVERY front end must present the
    /// service's <see cref="ArgumentException"/> messages through this method (and only
    /// this method) so the pinned error text stays identical on every surface
    /// (MCP wire, CLI stderr). It is also safe for other exception messages
    /// (driver/URI errors): messages without the decoration pass through unchanged.
    /// </summary>
    public static string CleanMessage(string message)
    {
        var line = message.AsSpan();
        var newline = line.IndexOfAny('\r', '\n');
        if (newline >= 0)
        {
            line = line[..newline];
        }

        var paramSuffix = line.IndexOf(" (Parameter '", StringComparison.Ordinal);
        return (paramSuffix < 0 ? line : line[..paramSuffix]).ToString();
    }

    /// <summary>
    /// Case-insensitive substring search on symbol names within <paramref name="branch"/>,
    /// optionally filtered to one <see cref="SymbolKinds"/> value. An empty or whitespace
    /// <paramref name="query"/> (or a non-positive <paramref name="limit"/>) returns an
    /// empty result rather than an error — front ends surface it as "0 matches".
    /// <paramref name="limit"/> is clamped to <see cref="MaxRequestLimit"/>.
    /// </summary>
    public async Task<SymbolSearchResult> FindSymbolsAsync(
        string branch,
        string query,
        string? kind = null,
        int limit = DefaultLimit,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        if (string.IsNullOrWhiteSpace(query) || limit < 1)
        {
            return new SymbolSearchResult([], Truncated: false);
        }

        // Ask the reader for ONE row more than we return: an (effectiveLimit + 1)-th row
        // is proof the result really was cut off, so Truncated is exact — exactly-limit
        // matches report false, limit+1-or-more report true.
        var effectiveLimit = Math.Min(limit, MaxRequestLimit);
        var hits = await _reader
            .FindSymbolsAsync(branch, query, kind, effectiveLimit + 1, ct)
            .ConfigureAwait(false);

        return new SymbolSearchResult(
            Hits: hits.Take(effectiveLimit).ToArray(),
            Truncated: hits.Count > effectiveLimit);
    }

    /// <summary>
    /// The distinct symbols that call <paramref name="symbolId"/> within
    /// <paramref name="branch"/>, following CALLS, HTTP_CALLS, JS_CALLS and JS_INVOKES edges up to
    /// <paramref name="depth"/> hops. The target itself is never included. Unknown ids
    /// return an empty result.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="symbolId"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="depth"/> is outside
    /// [<see cref="IGraphReader.MinDepth"/>, <see cref="IGraphReader.MaxDepth"/>].</exception>
    public async Task<CallerSearchResult> GetCallersAsync(
        string branch,
        string symbolId,
        int depth = 1,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        if (string.IsNullOrWhiteSpace(symbolId))
        {
            throw new ArgumentException(
                "symbolId must be a non-empty stable symbol id; call find_symbol to look one up.",
                nameof(symbolId));
        }

        if (depth is < IGraphReader.MinDepth or > IGraphReader.MaxDepth)
        {
            throw new ArgumentOutOfRangeException(
                nameof(depth),
                depth,
                $"depth must be between {IGraphReader.MinDepth} and {IGraphReader.MaxDepth}; got {depth}.");
        }

        var callers = await _reader
            .GetCallersAsync(branch, symbolId, depth, ct)
            .ConfigureAwait(false);

        return new CallerSearchResult(symbolId, depth, callers);
    }

    /// <summary>
    /// The distinct symbols related to <paramref name="symbolId"/> within
    /// <paramref name="branch"/>, reached by following edges in <paramref name="direction"/>
    /// up to <paramref name="depth"/> hops, optionally filtered to a single
    /// <paramref name="edgeType"/>. Each hit carries the edge type that reached it and the
    /// direction traversed. The anchor itself is never included; unknown ids return an empty
    /// result. The result is capped at <see cref="MaxRequestLimit"/> hits to bound fan-out;
    /// <see cref="RelationshipSearchResult.Truncated"/> is true when that cap was hit.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="symbolId"/> is null or whitespace;
    /// or <paramref name="depth"/> is above 1 without a single <paramref name="edgeType"/>;
    /// or <paramref name="edgeType"/> is not one of <see cref="EdgeTypes.All"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="depth"/> is outside
    /// [<see cref="IGraphReader.MinDepth"/>, <see cref="IGraphReader.MaxRelationshipDepth"/>].</exception>
    public async Task<RelationshipSearchResult> GetRelationshipsAsync(
        string branch,
        string symbolId,
        RelationshipDirection direction,
        string? edgeType = null,
        int depth = 1,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        if (string.IsNullOrWhiteSpace(symbolId))
        {
            throw new ArgumentException(
                "symbolId must be a non-empty stable symbol id; call find_symbol to look one up.",
                nameof(symbolId));
        }

        if (depth is < IGraphReader.MinDepth or > IGraphReader.MaxRelationshipDepth)
        {
            throw new ArgumentOutOfRangeException(
                nameof(depth),
                depth,
                $"depth must be between {IGraphReader.MinDepth} and {IGraphReader.MaxRelationshipDepth}; got {depth}.");
        }

        // A whitespace edge type is no filter at all; normalize so the rules below see null.
        edgeType = string.IsNullOrWhiteSpace(edgeType) ? null : edgeType;

        if (depth > 1 && edgeType is null)
        {
            throw new ArgumentException(
                "depth > 1 requires a single --edge-type; multi-hop mixed-type traversal is not supported.",
                nameof(edgeType));
        }

        if (edgeType is not null && !EdgeTypes.All.Contains(edgeType))
        {
            throw new ArgumentException(
                $"Unknown edge type '{edgeType}'. Valid types: {string.Join(", ", EdgeTypes.All)}.",
                nameof(edgeType));
        }

        // Cap fan-out and probe for one extra row so Truncated is exact — identical to the
        // FindSymbols limit + 1 idiom.
        var effectiveLimit = MaxRequestLimit;
        var hits = await _reader
            .GetRelationshipsAsync(branch, symbolId, direction, edgeType, depth, effectiveLimit + 1, ct)
            .ConfigureAwait(false);

        return new RelationshipSearchResult(
            TargetId: symbolId,
            Direction: RelationshipDirections.ToWire(direction),
            EdgeType: edgeType,
            Depth: depth,
            Hits: hits.Take(effectiveLimit).ToArray(),
            Truncated: hits.Count > effectiveLimit);
    }

    /// <summary>
    /// One shortest directed path from <paramref name="fromId"/> to <paramref name="toId"/>
    /// within <paramref name="branch"/>, following outgoing edges up to
    /// <paramref name="maxLength"/> hops, optionally restricted to a single
    /// <paramref name="edgeType"/>. Unreachable endpoints yield
    /// <see cref="PathResult.Found"/> false with empty nodes; <paramref name="fromId"/> equal
    /// to <paramref name="toId"/> yields a found single-node path of length 0.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="fromId"/> or
    /// <paramref name="toId"/> is null or whitespace; or <paramref name="edgeType"/> is not
    /// one of <see cref="EdgeTypes.All"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLength"/> is outside
    /// [<see cref="IGraphReader.MinDepth"/>, <see cref="IGraphReader.MaxPathLength"/>].</exception>
    public async Task<PathResult> GetPathAsync(
        string branch,
        string fromId,
        string toId,
        string? edgeType = null,
        int maxLength = IGraphReader.DefaultPathLength,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        if (string.IsNullOrWhiteSpace(fromId))
        {
            throw new ArgumentException(
                "fromId must be a non-empty stable symbol id; call find_symbol to look one up.",
                nameof(fromId));
        }

        if (string.IsNullOrWhiteSpace(toId))
        {
            throw new ArgumentException(
                "toId must be a non-empty stable symbol id; call find_symbol to look one up.",
                nameof(toId));
        }

        if (maxLength is < IGraphReader.MinDepth or > IGraphReader.MaxPathLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxLength),
                maxLength,
                $"maxLength must be between {IGraphReader.MinDepth} and {IGraphReader.MaxPathLength}; got {maxLength}.");
        }

        // A whitespace edge type is no filter at all; normalize before validating.
        edgeType = string.IsNullOrWhiteSpace(edgeType) ? null : edgeType;
        if (edgeType is not null && !EdgeTypes.All.Contains(edgeType))
        {
            throw new ArgumentException(
                $"Unknown edge type '{edgeType}'. Valid types: {string.Join(", ", EdgeTypes.All)}.",
                nameof(edgeType));
        }

        return await _reader
            .GetPathAsync(branch, fromId, toId, edgeType, maxLength, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Per-branch orientation stats for <paramref name="branch"/>: total node/edge counts,
    /// node counts by kind, edge counts by type, and the top <paramref name="topN"/> god
    /// nodes by degree (excluding <c>CONTAINS</c> edges). <paramref name="topN"/> is clamped
    /// into [1, <see cref="IGraphReader.MaxTopN"/>] rather than rejected.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="branch"/> is null or whitespace.</exception>
    public async Task<GraphStatsResult> GetStatsAsync(
        string branch,
        int topN = 10,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        var effectiveTopN = Math.Clamp(topN, 1, IGraphReader.MaxTopN);

        return await _reader
            .GetStatsAsync(branch, effectiveTopN, ct)
            .ConfigureAwait(false);
    }
}
