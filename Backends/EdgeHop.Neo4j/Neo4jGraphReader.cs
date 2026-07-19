using Neo4j.Driver;

namespace EdgeHop.Core;

/// <summary>
/// Read-side queries over the code graph, backing <see cref="EdgeHopQueryService"/>.
/// The <see cref="IDriver"/> is thread-safe and shared; sessions are not, so every call
/// opens its own session, runs inside <c>ExecuteReadAsync</c>, and never stores a session.
/// <para>
/// Every user-supplied value (branch, query, kind, symbolId, fromId, toId, limit, topN)
/// travels as a Cypher parameter — never spliced into query text. The only exceptions are
/// the two tokens Cypher forbids as parameters: a variable-length bound (<c>[:CALLS*1..$depth]</c>
/// is invalid Cypher) and a relationship type (<c>[:$edgeType]</c> is invalid). The depth /
/// maxLength bound is interpolated ONLY after validating it as an integer in range, and the
/// edge type ONLY after validating it against <see cref="EdgeTypes.All"/> — mirroring the
/// writer's <c>BuildEdgeUpsertCypher</c>. The depth-1 relationship filter compares
/// <c>type(r)</c> to a parameter, so no interpolation happens there at all.
/// </para>
/// </summary>
public sealed class Neo4jGraphReader(IDriver driver, string database) : IGraphReader
{
    /// <summary>Mirror of <see cref="IGraphReader.MaxLimit"/> (the contract lives on the
    /// interface so validation behavior is backend-independent).</summary>
    public const int MaxLimit = IGraphReader.MaxLimit;

    /// <summary>Mirror of <see cref="IGraphReader.MinDepth"/>.</summary>
    public const int MinDepth = IGraphReader.MinDepth;

    /// <summary>Mirror of <see cref="IGraphReader.MaxDepth"/>.</summary>
    public const int MaxDepth = IGraphReader.MaxDepth;

    /// <summary>Mirror of <see cref="IGraphReader.MaxRelationshipDepth"/>.</summary>
    public const int MaxRelationshipDepth = IGraphReader.MaxRelationshipDepth;

    /// <summary>Mirror of <see cref="IGraphReader.MaxPathLength"/>.</summary>
    public const int MaxPathLength = IGraphReader.MaxPathLength;

    /// <summary>The node projection shared by every hit-shaped query: the six
    /// <see cref="SymbolHit"/> columns aliased so <see cref="MapHit"/> can read them.</summary>
    private const string NodeProjection =
        "n.id AS id, n.name AS name, n.kind AS kind, n.sourceDoc AS sourceDoc, " +
        "n.isComponent AS isComponent, n.routes AS routes";

    private readonly IDriver _driver =
        driver ?? throw new ArgumentNullException(nameof(driver));

    private readonly string _database = !string.IsNullOrWhiteSpace(database)
        ? database
        : throw new ArgumentException("A database name is required.", nameof(database));

    /// <summary>
    /// Case-insensitive substring match on <c>Symbol.name</c> within <paramref name="branch"/>,
    /// with an optional exact <paramref name="kind"/> filter. Results are ordered by name
    /// (then id, for determinism between equal names).
    /// </summary>
    /// <param name="branch">Branch to search (composite-key partition). Required.</param>
    /// <param name="query">Substring to match against <c>name</c>, case-insensitively.
    /// An empty or whitespace query returns an empty list without touching the database.</param>
    /// <param name="kind">Optional exact <c>kind</c> filter (one of <see cref="SymbolKinds"/>);
    /// null/whitespace means no filter.</param>
    /// <param name="limit">Maximum results. Values above <see cref="MaxLimit"/> are capped
    /// to <see cref="MaxLimit"/>; values below 1 return an empty list.</param>
    /// <param name="ct">Cancellation token observed while streaming results.</param>
    public async Task<IReadOnlyList<SymbolHit>> FindSymbolsAsync(
        string branch,
        string query,
        string? kind = null,
        int limit = 25,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        if (string.IsNullOrWhiteSpace(query) || limit < 1)
        {
            return [];
        }

        var parameters = new Dictionary<string, object>
        {
            ["branch"] = branch,
            ["query"] = query,
            ["limit"] = Math.Min(limit, MaxLimit),
        };

        // The kind filter is composed from a FIXED fragment; the kind VALUE is always a
        // parameter. (CONTAINS is a literal substring test — no wildcard/regex escaping
        // is needed for the query text either, because $query is a parameter too.)
        var kindFilter = string.Empty;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            parameters["kind"] = kind;
            kindFilter = " AND n.kind = $kind";
        }

        var cypher = $$"""
            MATCH (n:Symbol {branch: $branch})
            WHERE toLower(n.name) CONTAINS toLower($query){{kindFilter}}
            RETURN {{NodeProjection}}
            ORDER BY name ASC, id ASC
            LIMIT $limit
            """;

        return await RunHitQueryAsync(cypher, parameters, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the distinct symbols that call <paramref name="symbolId"/> within
    /// <paramref name="branch"/>, following <c>CALLS</c>, <c>HTTP_CALLS</c>, <c>JS_CALLS</c> and
    /// <c>JS_INVOKES</c> edges up to <paramref name="depth"/> hops (README query). The target symbol itself is never included in the results
    /// (relevant when call cycles reach back to the target). Unknown ids simply return
    /// an empty list.
    /// </summary>
    /// <param name="branch">Branch to search (composite-key partition). Required.</param>
    /// <param name="symbolId">Stable id of the callee. Required; always a parameter.</param>
    /// <param name="depth">Maximum hops, inclusive; must be within
    /// [<see cref="MinDepth"/>, <see cref="MaxDepth"/>].</param>
    /// <param name="ct">Cancellation token observed while streaming results.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="depth"/> is outside
    /// [<see cref="MinDepth"/>, <see cref="MaxDepth"/>].</exception>
    public async Task<IReadOnlyList<SymbolHit>> GetCallersAsync(
        string branch,
        string symbolId,
        int depth = 1,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolId);
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, MinDepth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(depth, MaxDepth);

        // Cypher cannot parameterize a variable-length bound ('[:CALLS*1..$depth]' is
        // invalid), so the depth — validated above as an integer in [1, 10] — is the
        // ONLY value ever interpolated into the query text. branch and symbolId stay
        // parameters, which is what the injection-safety tests pin down. HTTP_CALLS, JS_CALLS
        // and JS_INVOKES participate alongside CALLS so cross-tier callers (Web→Api, C#→JS,
        // JS→C#) surface in get_callers.
        var cypher = $$"""
            MATCH (n:Symbol {branch: $branch})-[:CALLS|HTTP_CALLS|JS_CALLS|JS_INVOKES*1..{{depth}}]->(t:Symbol {branch: $branch, id: $symbolId})
            WHERE n.id <> $symbolId
            RETURN DISTINCT {{NodeProjection}}
            ORDER BY name ASC, id ASC
            """;

        var parameters = new Dictionary<string, object>
        {
            ["branch"] = branch,
            ["symbolId"] = symbolId,
        };

        return await RunHitQueryAsync(cypher, parameters, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The distinct symbols related to <paramref name="symbolId"/> within
    /// <paramref name="branch"/>, reached by following edges in <paramref name="direction"/>
    /// up to <paramref name="depth"/> hops, optionally filtered to a single
    /// <paramref name="edgeType"/>. Each hit carries the edge type that reached it and the
    /// direction traversed. The anchor is never included; unknown ids return an empty list.
    /// The service validates ranges/rules (depth &gt; 1 requires a single edge type; any edge
    /// type must be in <see cref="EdgeTypes.All"/>); this method re-guards branch/id and, as
    /// defense in depth, the two tokens it interpolates.
    /// </summary>
    public async Task<IReadOnlyList<RelationshipHit>> GetRelationshipsAsync(
        string branch,
        string symbolId,
        RelationshipDirection direction,
        string? edgeType,
        int depth,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolId);
        if (limit < 1)
        {
            return [];
        }

        var cypher = BuildRelationshipsCypher(direction, edgeType, depth);

        var parameters = new Dictionary<string, object>
        {
            ["branch"] = branch,
            ["symbolId"] = symbolId,
            ["limit"] = limit,
        };

        // edgeType is a parameter both for the depth-1 type(r) filter and the depth>1
        // projection label; it is only ever interpolated (as a relationship-type token) by
        // BuildRelationshipsCypher, and only after the EdgeTypes.All check there.
        if (edgeType is not null)
        {
            parameters["edgeType"] = edgeType;
        }

        return await RunReadListAsync(
            cypher,
            parameters,
            r => new RelationshipHit(MapHit(r), r.Get<string>("edgeType"), r.Get<string>("direction")),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One shortest directed path from <paramref name="fromId"/> to <paramref name="toId"/>
    /// within <paramref name="branch"/>, following outgoing edges up to
    /// <paramref name="maxLength"/> hops, optionally restricted to a single
    /// <paramref name="edgeType"/>. Unreachable endpoints yield
    /// <see cref="PathResult.Found"/> false with empty nodes; <paramref name="fromId"/> equal
    /// to <paramref name="toId"/> yields a found single-node path of length 0 (when that
    /// symbol exists in the branch). The service validates ranges and the edge type before
    /// calling; this method re-guards branch/id and the interpolated bound/type token.
    /// </summary>
    public async Task<PathResult> GetPathAsync(
        string branch,
        string fromId,
        string toId,
        string? edgeType,
        int maxLength,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(fromId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toId);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, MinDepth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxLength, MaxPathLength);

        // Same anchor on both ends is a found, zero-length path — but only when that symbol
        // actually exists in the branch; short-circuit before touching shortestPath.
        if (string.Equals(fromId, toId, StringComparison.Ordinal))
        {
            var self = await FindByIdAsync(branch, fromId, ct).ConfigureAwait(false);
            return self is null
                ? new PathResult(fromId, toId, Found: false, [])
                : new PathResult(fromId, toId, Found: true, [new PathNode(self, EdgeTypeFromPrev: null)]);
        }

        // shortestPath cannot parameterize its length bound nor a relationship type, so the
        // int-validated maxLength and the EdgeTypes.All-validated type token are the ONLY
        // values interpolated; branch/fromId/toId stay parameters.
        var typeToken = string.Empty;
        if (edgeType is not null)
        {
            if (!EdgeTypes.IsValid(edgeType))
            {
                // Defense in depth: the service already validated this token.
                throw new ArgumentException($"'{edgeType}' is not a known edge type.", nameof(edgeType));
            }

            typeToken = ":" + edgeType;
        }

        var cypher = $$"""
            MATCH (a:Symbol {branch: $branch, id: $fromId}), (b:Symbol {branch: $branch, id: $toId})
            MATCH p = shortestPath((a)-[{{typeToken}}*..{{maxLength}}]->(b))
            WHERE ALL(x IN nodes(p) WHERE x.branch = $branch)
            RETURN [x IN nodes(p) | {id: x.id, name: x.name, kind: x.kind, sourceDoc: x.sourceDoc, isComponent: x.isComponent, routes: x.routes}] AS nodes,
                   [r IN relationships(p) | type(r)] AS types
            """;

        var parameters = new Dictionary<string, object>
        {
            ["branch"] = branch,
            ["fromId"] = fromId,
            ["toId"] = toId,
        };

        var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        try
        {
            return await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync(cypher, parameters).ConfigureAwait(false);
                var records = await cursor.ToListAsync(ct).ConfigureAwait(false);
                if (records.Count == 0)
                {
                    // No path (or an endpoint that does not exist) — unreachable.
                    return new PathResult(fromId, toId, Found: false, []);
                }

                var record = records[0];
                var nodeMaps = record.Get<List<IReadOnlyDictionary<string, object>>>("nodes");
                var types = record.Get<List<string>>("types");

                // A null map value may be elided by the driver, so read the nullable facets
                // via TryGetValue (a missing key reads as its null/false default).
                static object? Val(IReadOnlyDictionary<string, object> m, string key)
                    => m.TryGetValue(key, out var v) ? v : null;

                var nodes = new List<PathNode>(nodeMaps.Count);
                for (var i = 0; i < nodeMaps.Count; i++)
                {
                    var m = nodeMaps[i];
                    var hit = new SymbolHit(
                        m["id"].As<string>(),
                        m["name"].As<string>(),
                        m["kind"].As<string>(),
                        Val(m, "sourceDoc").As<string?>(),
                        Val(m, "isComponent").As<bool?>() ?? false,
                        Val(m, "routes").As<List<string>?>());

                    // Index 0 has no predecessor; each later node records the edge type
                    // linking the previous node to it (types has nodes.Count - 1 entries).
                    nodes.Add(new PathNode(hit, i == 0 ? null : types[i - 1]));
                }

                return new PathResult(fromId, toId, Found: true, nodes);
            }).ConfigureAwait(false);
        }
        finally
        {
            await session.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Per-branch orientation stats for <paramref name="branch"/>: total node/edge counts,
    /// node counts by kind, edge counts by type, and the top <paramref name="topN"/> god
    /// nodes by degree (excluding <c>CONTAINS</c> edges). The service clamps
    /// <paramref name="topN"/> into [1, <see cref="IGraphReader.MaxTopN"/>] before calling.
    /// </summary>
    public async Task<GraphStatsResult> GetStatsAsync(
        string branch,
        int topN,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        const string totalNodesCypher = """
            MATCH (n:Symbol {branch: $branch})
            RETURN count(n) AS c
            """;

        const string totalEdgesCypher = """
            MATCH (:Symbol {branch: $branch})-[r]->(:Symbol {branch: $branch})
            RETURN count(r) AS c
            """;

        const string nodesByKindCypher = """
            MATCH (n:Symbol {branch: $branch})
            RETURN n.kind AS kind, count(*) AS c
            ORDER BY c DESC, kind ASC
            """;

        const string edgesByTypeCypher = """
            MATCH (:Symbol {branch: $branch})-[r]->(:Symbol {branch: $branch})
            RETURN type(r) AS type, count(*) AS c
            ORDER BY c DESC, type ASC
            """;

        // God nodes: degree counts every incident non-CONTAINS edge once per endpoint
        // (the undirected '-[r]-' match), mirroring the sqlite fromId+toId union-all. The
        // 'CONTAINS' literal is a constant, not user input. topN is a plain LIMIT parameter.
        // WHERE degree > 0 excludes zero-degree nodes (isolated, or reached only by CONTAINS)
        // so a god-node list is genuinely "the hubs" — and so BOTH backends agree exactly:
        // the sqlite deg CTE is built purely from non-CONTAINS edge endpoints, so it never
        // yields a degree-0 row either (design-spec D6 / D9 cross-backend parity).
        const string godNodesCypher = """
            MATCH (n:Symbol {branch: $branch})
            OPTIONAL MATCH (n)-[r]-(:Symbol {branch: $branch}) WHERE type(r) <> 'CONTAINS'
            WITH n, count(r) AS degree
            WHERE degree > 0
            RETURN n.id AS id, n.name AS name, n.kind AS kind, n.sourceDoc AS sourceDoc,
                   n.isComponent AS isComponent, n.routes AS routes, degree
            ORDER BY degree DESC, id ASC
            LIMIT $topN
            """;

        var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        try
        {
            return await session.ExecuteReadAsync(async tx =>
            {
                var branchParams = new Dictionary<string, object> { ["branch"] = branch };

                async Task<long> CountAsync(string cypher)
                {
                    var cursor = await tx.RunAsync(cypher, branchParams).ConfigureAwait(false);
                    var rows = await cursor.ToListAsync(ct).ConfigureAwait(false);
                    return rows[0].Get<long>("c");
                }

                var totalNodes = await CountAsync(totalNodesCypher).ConfigureAwait(false);
                var totalEdges = await CountAsync(totalEdgesCypher).ConfigureAwait(false);

                var kindCursor = await tx.RunAsync(nodesByKindCypher, branchParams).ConfigureAwait(false);
                var kindRows = await kindCursor.ToListAsync(ct).ConfigureAwait(false);
                var nodesByKind = kindRows
                    .Select(r => new KindCount(r.Get<string>("kind"), r.Get<long>("c")))
                    .ToList();

                var typeCursor = await tx.RunAsync(edgesByTypeCypher, branchParams).ConfigureAwait(false);
                var typeRows = await typeCursor.ToListAsync(ct).ConfigureAwait(false);
                var edgesByType = typeRows
                    .Select(r => new EdgeTypeCount(r.Get<string>("type"), r.Get<long>("c")))
                    .ToList();

                var godCursor = await tx.RunAsync(
                    godNodesCypher,
                    new Dictionary<string, object> { ["branch"] = branch, ["topN"] = topN }).ConfigureAwait(false);
                var godRows = await godCursor.ToListAsync(ct).ConfigureAwait(false);
                var godNodes = godRows
                    .Select(r => new DegreeHit(MapHit(r), r.Get<long>("degree")))
                    .ToList();

                return new GraphStatsResult(branch, totalNodes, totalEdges, nodesByKind, edgesByType, godNodes);
            }).ConfigureAwait(false);
        }
        finally
        {
            await session.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds the <c>get_relationships</c> Cypher for <paramref name="direction"/>,
    /// <paramref name="edgeType"/> and <paramref name="depth"/>. Depth-1 walks a single hop
    /// and returns the actual <c>type(r)</c> per hit (edge-type filter, if any, is a
    /// parameter); depth &gt; 1 walks a validated single edge type (interpolated as a
    /// relationship-type token, exactly like the writer). The variable-length bound and the
    /// edge type are the only interpolated tokens, and both are re-validated here as defense
    /// in depth; branch/symbolId/limit stay parameters.
    /// </summary>
    private static string BuildRelationshipsCypher(
        RelationshipDirection direction, string? edgeType, int depth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, MinDepth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(depth, MaxRelationshipDepth);

        const string order = "ORDER BY edgeType ASC, direction ASC, name ASC, id ASC";

        if (depth == 1)
        {
            // type(r) is comparable to a parameter, so no interpolation happens for depth-1.
            var typeFilter = edgeType is null ? string.Empty : " AND type(r) = $edgeType";

            return direction switch
            {
                RelationshipDirection.Out => $$"""
                    MATCH (s:Symbol {branch: $branch, id: $symbolId})-[r]->(n:Symbol {branch: $branch})
                    WHERE n.id <> $symbolId{{typeFilter}}
                    RETURN DISTINCT {{NodeProjection}}, type(r) AS edgeType, 'out' AS direction
                    {{order}}
                    LIMIT $limit
                    """,
                RelationshipDirection.In => $$"""
                    MATCH (s:Symbol {branch: $branch, id: $symbolId})<-[r]-(n:Symbol {branch: $branch})
                    WHERE n.id <> $symbolId{{typeFilter}}
                    RETURN DISTINCT {{NodeProjection}}, type(r) AS edgeType, 'in' AS direction
                    {{order}}
                    LIMIT $limit
                    """,
                _ => $$"""
                    MATCH (s:Symbol {branch: $branch, id: $symbolId})-[r]-(n:Symbol {branch: $branch})
                    WHERE n.id <> $symbolId{{typeFilter}}
                    RETURN DISTINCT {{NodeProjection}}, type(r) AS edgeType,
                           CASE WHEN startNode(r) = s THEN 'out' ELSE 'in' END AS direction
                    {{order}}
                    LIMIT $limit
                    """,
            };
        }

        // depth > 1: the service guarantees a single validated edge type. Re-validate before
        // interpolating it as a relationship-type token (writer's BuildEdgeUpsertCypher rule).
        if (!EdgeTypes.IsValid(edgeType))
        {
            throw new ArgumentException(
                "depth > 1 requires a single valid edge type.", nameof(edgeType));
        }

        return direction switch
        {
            RelationshipDirection.Out => $$"""
                MATCH (s:Symbol {branch: $branch, id: $symbolId})-[:{{edgeType}}*1..{{depth}}]->(n:Symbol {branch: $branch})
                WHERE n.id <> $symbolId
                RETURN DISTINCT {{NodeProjection}}, $edgeType AS edgeType, 'out' AS direction
                {{order}}
                LIMIT $limit
                """,
            RelationshipDirection.In => $$"""
                MATCH (s:Symbol {branch: $branch, id: $symbolId})<-[:{{edgeType}}*1..{{depth}}]-(n:Symbol {branch: $branch})
                WHERE n.id <> $symbolId
                RETURN DISTINCT {{NodeProjection}}, $edgeType AS edgeType, 'in' AS direction
                {{order}}
                LIMIT $limit
                """,
            // BOTH at depth > 1 follows each direction independently and unions the results
            // (chosen semantics per the decision log: a CTE/traversal per direction).
            _ => $$"""
                CALL {
                    MATCH (s:Symbol {branch: $branch, id: $symbolId})-[:{{edgeType}}*1..{{depth}}]->(n:Symbol {branch: $branch})
                    WHERE n.id <> $symbolId
                    RETURN DISTINCT n, 'out' AS direction
                    UNION
                    MATCH (s:Symbol {branch: $branch, id: $symbolId})<-[:{{edgeType}}*1..{{depth}}]-(n:Symbol {branch: $branch})
                    WHERE n.id <> $symbolId
                    RETURN DISTINCT n, 'in' AS direction
                }
                RETURN {{NodeProjection}}, $edgeType AS edgeType, direction
                {{order}}
                LIMIT $limit
                """,
        };
    }

    /// <summary>The single symbol with <paramref name="id"/> in <paramref name="branch"/>,
    /// or null when it does not exist. Backs the <c>fromId == toId</c> short-circuit.</summary>
    private async Task<SymbolHit?> FindByIdAsync(string branch, string id, CancellationToken ct)
    {
        var cypher = $$"""
            MATCH (n:Symbol {branch: $branch, id: $id})
            RETURN {{NodeProjection}}
            LIMIT 1
            """;

        var hits = await RunReadListAsync(
            cypher,
            new Dictionary<string, object> { ["branch"] = branch, ["id"] = id },
            MapHit,
            ct).ConfigureAwait(false);

        return hits.Count > 0 ? hits[0] : null;
    }

    /// <summary>Maps one record projecting the <see cref="NodeProjection"/> columns to a
    /// <see cref="SymbolHit"/>. <c>isComponent</c>/<c>routes</c> tolerate a store indexed
    /// before those facets existed (missing property → false/null).</summary>
    private static SymbolHit MapHit(IRecord r) => new(
        r.Get<string>("id"),
        r.Get<string>("name"),
        r.Get<string>("kind"),
        r.Get<string?>("sourceDoc"),
        r.Get<bool?>("isComponent") ?? false,
        r.Get<List<string>?>("routes"));

    /// <summary>
    /// Runs a read query projecting the <see cref="NodeProjection"/> columns and maps each
    /// record to a <see cref="SymbolHit"/>. One session per call; read transaction function.
    /// </summary>
    private Task<IReadOnlyList<SymbolHit>> RunHitQueryAsync(
        string cypher, IDictionary<string, object> parameters, CancellationToken ct)
        => RunReadListAsync(cypher, parameters, MapHit, ct);

    /// <summary>
    /// Runs a read query and projects each record with <paramref name="map"/>. One session
    /// per call; read transaction function; the session is always closed.
    /// </summary>
    private async Task<IReadOnlyList<T>> RunReadListAsync<T>(
        string cypher,
        IDictionary<string, object> parameters,
        Func<IRecord, T> map,
        CancellationToken ct)
    {
        var session = _driver.AsyncSession(o => o.WithDatabase(_database));
        try
        {
            return await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync(cypher, parameters).ConfigureAwait(false);
                var records = await cursor.ToListAsync(ct).ConfigureAwait(false);
                return (IReadOnlyList<T>)records.Select(map).ToList();
            }).ConfigureAwait(false);
        }
        finally
        {
            await session.CloseAsync().ConfigureAwait(false);
        }
    }
}
