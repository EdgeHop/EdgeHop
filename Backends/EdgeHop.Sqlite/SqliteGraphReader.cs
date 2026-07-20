using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace EdgeHop.Core;

/// <summary>
/// SQLite implementation of <see cref="IGraphReader"/>, observably equivalent to
/// <see cref="Neo4jGraphReader"/>: identical argument validation (the contract lives on
/// <see cref="IGraphReader"/>), identical result semantics (case-insensitive substring
/// find; distinct incoming-CALLS/HTTP_CALLS/JS_CALLS/JS_INVOKES callers excluding the target;
/// <c>ORDER BY name, id</c>),
/// and the same injection posture — every user-supplied value is bound as a parameter,
/// never composed into SQL text. LIKE wildcards in the user's query are escaped so
/// <c>*</c>, <c>%</c>, <c>_</c> and <c>?</c> all match literally, preserving the
/// documented "no wildcards" search semantics.
/// </summary>
public sealed class SqliteGraphReader : IGraphReader
{
    private const string FindSymbolsSql = """
        SELECT id, name, kind, sourceDoc, isComponent, routes FROM nodes
        WHERE branch = $branch AND name LIKE $pattern ESCAPE '\'
        {0}
        ORDER BY name ASC, id ASC
        LIMIT $limit
        """;

    // Fixed-fragment kind filter, mirroring the Cypher composition: the fragment is a
    // constant; the kind VALUE is always a parameter.
    private const string KindFilterFragment = "AND kind = $kind";

    // Bounded-depth BFS over incoming CALLS/HTTP_CALLS/JS_CALLS/JS_INVOKES edges as a recursive
    // CTE. UNION (not UNION ALL) dedupes (id, hop) rows; the hop bound terminates cycles; the final
    // SELECT dedupes across hops, excludes the target (cycles can reach back to it), and joins node
    // properties — the same result set as Cypher's [:CALLS|HTTP_CALLS|JS_CALLS|JS_INVOKES*1..N]
    // with DISTINCT. The three cross-tier types participate so cross-tier callers (a Web client
    // method hitting an ApiService endpoint; a C# method invoking a JS function; a JS function
    // invoking a C# [JSInvokable] method) surface in get_callers like any other caller.
    private const string GetCallersSql = """
        WITH RECURSIVE callers (id, hop) AS (
            SELECT e.fromId, 1 FROM edges e
            WHERE e.branch = $branch AND e.type IN ('CALLS', 'HTTP_CALLS', 'JS_CALLS', 'JS_INVOKES') AND e.toId = $symbolId
            UNION
            SELECT e.fromId, c.hop + 1 FROM edges e
            JOIN callers c ON e.toId = c.id
            WHERE e.branch = $branch AND e.type IN ('CALLS', 'HTTP_CALLS', 'JS_CALLS', 'JS_INVOKES') AND c.hop < $depth
        )
        SELECT DISTINCT n.id, n.name, n.kind, n.sourceDoc, n.isComponent, n.routes
        FROM callers c
        JOIN nodes n ON n.branch = $branch AND n.id = c.id
        WHERE n.id <> $symbolId
        ORDER BY n.name ASC, n.id ASC
        """;

    // Depth-1 neighbor blocks. The neighbor is the OTHER endpoint of the edge; {0} is the
    // optional "AND e.type = $edgeType" filter (a constant fragment; the value is bound).
    private const string RelationshipsOutBlock = """
        SELECT n.id, n.name, n.kind, n.sourceDoc, n.isComponent, n.routes,
               e.type AS edgeType, 'out' AS direction
        FROM edges e
        JOIN nodes n ON n.branch = $branch AND n.id = e.toId
        WHERE e.branch = $branch AND e.fromId = $symbolId AND n.id <> $symbolId{0}
        """;

    private const string RelationshipsInBlock = """
        SELECT n.id, n.name, n.kind, n.sourceDoc, n.isComponent, n.routes,
               e.type AS edgeType, 'in' AS direction
        FROM edges e
        JOIN nodes n ON n.branch = $branch AND n.id = e.fromId
        WHERE e.branch = $branch AND e.toId = $symbolId AND n.id <> $symbolId{0}
        """;

    // Depth>1 recursive reach set (single validated edgeType, guaranteed by the service).
    // UNION (not ALL) + the hop bound terminates cycles exactly like GetCallersSql. The
    // OUT walk follows e.fromId -> e.toId; the IN walk flips them.
    private const string RelationshipsReachOutCte = """
        reach_out (id, hop) AS (
            SELECT e.toId, 1 FROM edges e
            WHERE e.branch = $branch AND e.type = $edgeType AND e.fromId = $symbolId
            UNION
            SELECT e.toId, r.hop + 1 FROM edges e
            JOIN reach_out r ON e.fromId = r.id
            WHERE e.branch = $branch AND e.type = $edgeType AND r.hop < $depth
        )
        """;

    private const string RelationshipsReachInCte = """
        reach_in (id, hop) AS (
            SELECT e.fromId, 1 FROM edges e
            WHERE e.branch = $branch AND e.type = $edgeType AND e.toId = $symbolId
            UNION
            SELECT e.fromId, r.hop + 1 FROM edges e
            JOIN reach_in r ON e.toId = r.id
            WHERE e.branch = $branch AND e.type = $edgeType AND r.hop < $depth
        )
        """;

    private const string RelationshipsReachOutSelect = """
        SELECT n.id, n.name, n.kind, n.sourceDoc, n.isComponent, n.routes,
               $edgeType AS edgeType, 'out' AS direction
        FROM reach_out r
        JOIN nodes n ON n.branch = $branch AND n.id = r.id
        WHERE n.id <> $symbolId
        """;

    private const string RelationshipsReachInSelect = """
        SELECT n.id, n.name, n.kind, n.sourceDoc, n.isComponent, n.routes,
               $edgeType AS edgeType, 'in' AS direction
        FROM reach_in r
        JOIN nodes n ON n.branch = $branch AND n.id = r.id
        WHERE n.id <> $symbolId
        """;

    // Shared distinct/order/limit envelope; {0} is the direction-composed inner SELECT.
    private const string RelationshipsWrapper = """
        SELECT DISTINCT id, name, kind, sourceDoc, isComponent, routes, edgeType, direction
        FROM (
        {0}
        )
        ORDER BY edgeType ASC, direction ASC, name ASC, id ASC
        LIMIT $limit
        """;

    // Shortest directed simple path fromId -> toId over out-edges. The path column carries a
    // unit-separator (char(31))-delimited id trail so the walk can (a) reject revisits via
    // instr (acyclic simple path — this is what bounds the fan-out with the maxLength guard)
    // and (b) be split in C# to reconstruct the ordered nodes; types carries the parallel
    // edge-type trail. char(31) is used as the delimiter (NOT '|') because a symbol id can
    // itself contain '|' — a bitwise-or operator overload renders "operator |", and test id
    // schemes suffix ids with "|branch" — which would corrupt both the revisit guard and the
    // C# split; char(31) (US) cannot appear in a C# display string or a branch name. {0} is
    // the optional "AND e.type = $edgeType" filter. UNION ALL is correct: the revisit guard
    // makes each surviving row a distinct simple path, and ORDER BY depth + LIMIT 1 picks the
    // shortest reaching toId.
    private const string GetPathSql = """
        WITH RECURSIVE walk (id, depth, path, types) AS (
            SELECT $fromId, 0, char(31) || $fromId || char(31), ''
            UNION ALL
            SELECT e.toId, w.depth + 1,
                   w.path || e.toId || char(31),
                   w.types || e.type || char(31)
            FROM edges e
            JOIN walk w ON e.fromId = w.id
            WHERE e.branch = $branch AND w.depth < $maxLength
              AND instr(w.path, char(31) || e.toId || char(31)) = 0{0}
        )
        SELECT path, types, depth FROM walk WHERE id = $toId ORDER BY depth ASC LIMIT 1
        """;

    // Node projection for the id set on a reconstructed path; {0} is the numbered
    // "$p0, $p1, ..." IN-list — every id is a bound parameter, never concatenated.
    private const string FetchNodesByIdsSql = """
        SELECT id, name, kind, sourceDoc, isComponent, routes FROM nodes
        WHERE branch = $branch AND id IN ({0})
        """;

    private const string StatsTotalNodesSql =
        "SELECT COUNT(*) FROM nodes WHERE branch = $branch";

    private const string StatsTotalEdgesSql =
        "SELECT COUNT(*) FROM edges WHERE branch = $branch";

    private const string StatsNodesByKindSql = """
        SELECT kind, COUNT(*) AS c FROM nodes
        WHERE branch = $branch
        GROUP BY kind
        ORDER BY c DESC, kind ASC
        """;

    private const string StatsEdgesByTypeSql = """
        SELECT type, COUNT(*) AS c FROM edges
        WHERE branch = $branch
        GROUP BY type
        ORDER BY c DESC, type ASC
        """;

    // God nodes by degree EXCLUDING CONTAINS (else namespaces/types dominate). Each edge
    // contributes to the degree of both endpoints once (the fromId + toId union-all), so a
    // node's degree is its total non-CONTAINS incident-edge count. CONTAINS is a constant
    // literal, exactly like GetCallersSql's IN ('CALLS', 'HTTP_CALLS', 'JS_CALLS', 'JS_INVOKES').
    private const string StatsGodNodesSql = """
        WITH deg (id, degree) AS (
            SELECT id, COUNT(*) FROM (
                SELECT fromId AS id FROM edges WHERE branch = $branch AND type <> 'CONTAINS'
                UNION ALL
                SELECT toId AS id FROM edges WHERE branch = $branch AND type <> 'CONTAINS'
            )
            GROUP BY id
        )
        SELECT n.id, n.name, n.kind, n.sourceDoc, n.isComponent, n.routes, d.degree
        FROM deg d
        JOIN nodes n ON n.branch = $branch AND n.id = d.id
        ORDER BY d.degree DESC, n.id ASC
        LIMIT $topN
        """;

    private readonly Func<SqliteConnection> _openConnection;

    public SqliteGraphReader(Func<SqliteConnection> openConnection)
    {
        ArgumentNullException.ThrowIfNull(openConnection);
        _openConnection = openConnection;
    }

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

        using var connection = _openConnection();
        using var command = connection.CreateCommand();
        var kindFilter = string.Empty;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            command.Parameters.AddWithValue("$kind", kind);
            kindFilter = KindFilterFragment;
        }

        command.CommandText = string.Format(FindSymbolsSql, kindFilter);
        command.Parameters.AddWithValue("$branch", branch);
        command.Parameters.AddWithValue("$pattern", "%" + EscapeLikePattern(query) + "%");
        command.Parameters.AddWithValue("$limit", Math.Min(limit, IGraphReader.MaxLimit));

        return await ReadHitsAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SymbolHit>> GetCallersAsync(
        string branch,
        string symbolId,
        int depth = 1,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolId);
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, IGraphReader.MinDepth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(depth, IGraphReader.MaxDepth);

        using var connection = _openConnection();
        using var command = connection.CreateCommand();
        command.CommandText = GetCallersSql;
        command.Parameters.AddWithValue("$branch", branch);
        command.Parameters.AddWithValue("$symbolId", symbolId);
        command.Parameters.AddWithValue("$depth", depth);

        return await ReadHitsAsync(command, ct).ConfigureAwait(false);
    }

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
            // Mirror Neo4jGraphReader: a non-positive limit returns empty rather than
            // SQLite's unbounded "LIMIT -1", keeping the IGraphReader contract identical.
            return [];
        }

        using var connection = _openConnection();
        using var command = connection.CreateCommand();
        command.CommandText = BuildRelationshipsSql(direction, edgeType is not null, depth);
        command.Parameters.AddWithValue("$branch", branch);
        command.Parameters.AddWithValue("$symbolId", symbolId);
        command.Parameters.AddWithValue("$limit", limit);
        if (edgeType is not null)
        {
            command.Parameters.AddWithValue("$edgeType", edgeType);
        }

        if (depth > 1)
        {
            command.Parameters.AddWithValue("$depth", depth);
        }

        var hits = new List<RelationshipHit>();
        using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            hits.Add(new RelationshipHit(ReadHit(reader), reader.GetString(6), reader.GetString(7)));
        }

        return hits;
    }

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

        using var connection = _openConnection();

        // A path to itself is length 0 — short-circuit before the walk (and only report it
        // found when the anchor actually exists in this branch).
        if (string.Equals(fromId, toId, StringComparison.Ordinal))
        {
            var self = await FetchNodesByIdsAsync(connection, branch, [fromId], ct).ConfigureAwait(false);
            return self.TryGetValue(fromId, out var hit)
                ? new PathResult(fromId, toId, true, [new PathNode(hit, null)])
                : new PathResult(fromId, toId, false, []);
        }

        string? pathTrail = null;
        string? typeTrail = null;
        using (var command = connection.CreateCommand())
        {
            var edgeFilter = edgeType is not null ? "\n              AND e.type = $edgeType" : string.Empty;
            command.CommandText = string.Format(GetPathSql, edgeFilter);
            command.Parameters.AddWithValue("$branch", branch);
            command.Parameters.AddWithValue("$fromId", fromId);
            command.Parameters.AddWithValue("$toId", toId);
            command.Parameters.AddWithValue("$maxLength", maxLength);
            if (edgeType is not null)
            {
                command.Parameters.AddWithValue("$edgeType", edgeType);
            }

            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                pathTrail = reader.GetString(0);
                typeTrail = reader.GetString(1);
            }
        }

        if (pathTrail is null)
        {
            return new PathResult(fromId, toId, false, []);
        }

        var ids = pathTrail.Split('\u001f', StringSplitOptions.RemoveEmptyEntries);
        var types = typeTrail!.Split('\u001f', StringSplitOptions.RemoveEmptyEntries);
        var lookup = await FetchNodesByIdsAsync(connection, branch, ids, ct).ConfigureAwait(false);

        var nodes = new List<PathNode>(ids.Length);
        for (var i = 0; i < ids.Length; i++)
        {
            if (!lookup.TryGetValue(ids[i], out var hit))
            {
                // An edge referenced a node absent from the branch — treat as unreachable
                // rather than emit a partial path.
                return new PathResult(fromId, toId, false, []);
            }

            nodes.Add(new PathNode(hit, i == 0 ? null : types[i - 1]));
        }

        return new PathResult(fromId, toId, true, nodes);
    }

    public async Task<GraphStatsResult> GetStatsAsync(
        string branch,
        int topN,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        using var connection = _openConnection();

        var totalNodes = await CountAsync(connection, StatsTotalNodesSql, branch, ct).ConfigureAwait(false);
        var totalEdges = await CountAsync(connection, StatsTotalEdgesSql, branch, ct).ConfigureAwait(false);

        var nodesByKind = new List<KindCount>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = StatsNodesByKindSql;
            command.Parameters.AddWithValue("$branch", branch);
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                nodesByKind.Add(new KindCount(reader.GetString(0), reader.GetInt64(1)));
            }
        }

        var edgesByType = new List<EdgeTypeCount>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = StatsEdgesByTypeSql;
            command.Parameters.AddWithValue("$branch", branch);
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                edgesByType.Add(new EdgeTypeCount(reader.GetString(0), reader.GetInt64(1)));
            }
        }

        var godNodes = new List<DegreeHit>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = StatsGodNodesSql;
            command.Parameters.AddWithValue("$branch", branch);
            command.Parameters.AddWithValue("$topN", topN);
            using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                godNodes.Add(new DegreeHit(ReadHit(reader), reader.GetInt64(6)));
            }
        }

        return new GraphStatsResult(branch, totalNodes, totalEdges, nodesByKind, edgesByType, godNodes);
    }

    /// <summary>Composes the get_relationships SQL for a direction/depth: depth 1 unions the
    /// out/in neighbor blocks; depth &gt; 1 (single edge type, guaranteed by the service) runs
    /// a recursive reach set per requested direction and unions them. Only the direction, the
    /// presence of the edge-type filter, and the (service-validated) depth shape the text —
    /// every VALUE is bound.</summary>
    private static string BuildRelationshipsSql(
        RelationshipDirection direction, bool hasEdgeType, int depth)
    {
        if (depth <= 1)
        {
            var filter = hasEdgeType ? " AND e.type = $edgeType" : string.Empty;
            var inner = direction switch
            {
                RelationshipDirection.Out => string.Format(RelationshipsOutBlock, filter),
                RelationshipDirection.In => string.Format(RelationshipsInBlock, filter),
                _ => string.Format(RelationshipsOutBlock, filter)
                    + "\nUNION\n"
                    + string.Format(RelationshipsInBlock, filter),
            };

            return string.Format(RelationshipsWrapper, inner);
        }

        var ctes = new List<string>(2);
        var selects = new List<string>(2);
        if (direction is RelationshipDirection.Out or RelationshipDirection.Both)
        {
            ctes.Add(RelationshipsReachOutCte);
            selects.Add(RelationshipsReachOutSelect);
        }

        if (direction is RelationshipDirection.In or RelationshipDirection.Both)
        {
            ctes.Add(RelationshipsReachInCte);
            selects.Add(RelationshipsReachInSelect);
        }

        return "WITH RECURSIVE\n"
            + string.Join(",\n", ctes)
            + "\n"
            + string.Format(RelationshipsWrapper, string.Join("\nUNION\n", selects));
    }

    /// <summary>Escapes <c>\</c>, <c>%</c> and <c>_</c> so the user's query is a literal
    /// substring inside the surrounding <c>%…%</c> pattern (SQLite LIKE is
    /// case-insensitive for ASCII, matching the reader contract's semantics).</summary>
    private static string EscapeLikePattern(string query) =>
        query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    // columns: id(0) name(1) kind(2) sourceDoc(3) isComponent(4) routes(5)
    private static SymbolHit ReadHit(SqliteDataReader r) => new(
        r.GetString(0),
        r.GetString(1),
        r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.GetInt64(4) != 0,
        r.IsDBNull(5) ? null : JsonSerializer.Deserialize<List<string>>(r.GetString(5)));

    private static async Task<IReadOnlyList<SymbolHit>> ReadHitsAsync(
        SqliteCommand command, CancellationToken ct)
    {
        var hits = new List<SymbolHit>();
        using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            hits.Add(ReadHit(reader));
        }

        return hits;
    }

    /// <summary>Projects the listed ids to their nodes under <paramref name="branch"/>, keyed
    /// by id. The IN-list is built from numbered <c>$p0, $p1, …</c> parameters — ids are never
    /// concatenated into the SQL text.</summary>
    private static async Task<Dictionary<string, SymbolHit>> FetchNodesByIdsAsync(
        SqliteConnection connection,
        string branch,
        IReadOnlyList<string> ids,
        CancellationToken ct)
    {
        var nodes = new Dictionary<string, SymbolHit>(ids.Count, StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            return nodes;
        }

        using var command = connection.CreateCommand();
        var placeholders = new string[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            var name = "$p" + i;
            placeholders[i] = name;
            command.Parameters.AddWithValue(name, ids[i]);
        }

        command.CommandText = string.Format(FetchNodesByIdsSql, string.Join(", ", placeholders));
        command.Parameters.AddWithValue("$branch", branch);

        using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var hit = ReadHit(reader);
            nodes[hit.Id] = hit;
        }

        return nodes;
    }

    private static async Task<long> CountAsync(
        SqliteConnection connection, string sql, string branch, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$branch", branch);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(result);
    }
}
