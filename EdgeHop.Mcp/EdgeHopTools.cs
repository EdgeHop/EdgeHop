using System.ComponentModel;
using System.Text.Json.Serialization;
using EdgeHop.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace EdgeHop.Mcp;

/// <summary>
/// The five MCP tools: <c>find_symbol</c>, <c>get_callers</c>, <c>get_relationships</c>,
/// <c>get_path</c> and <c>graph_stats</c> — a pure transport adapter over
/// <see cref="EdgeHopQueryService"/>, the shared front-end-neutral backend that also
/// powers the <c>edgehop</c> CLI. The adapter owns nothing but (a) the pinned
/// wire shapes below, (b) translating the service's BCL validation exceptions into
/// <see cref="McpException"/> so the client sees actionable messages, and (c) the
/// per-call branch resolution. An instance is constructed per tool invocation by the MCP
/// hosting layer, with <paramref name="service"/> resolved from the service provider.
/// </summary>
[McpServerToolType]
public sealed class EdgeHopTools(EdgeHopQueryService service)
{
    /// <summary>
    /// BRANCH SEAM — resolved PER TOOL CALL via the shared <see cref="BranchResolver"/>:
    /// <c>EDGEHOP_BRANCH</c> env var (the test-injection seam) &gt; current git branch
    /// of <c>EDGEHOP_REPO</c> (set in <c>.mcp.json</c>; two file reads, effectively
    /// free) &gt; <c>"main"</c>. Per-call resolution means a mid-session
    /// <c>git switch</c> is picked up by the very next tool call with no caching or
    /// invalidation machinery. This property is the ONLY place the branch is chosen — do
    /// not scatter branch literals elsewhere. (The CLI front end additionally honors an
    /// explicit <c>--branch</c> flag ahead of the same resolution chain.)
    /// </summary>
    private static string Branch
    {
        get
        {
            var branch = BranchResolver.Resolve(explicitBranch: null, pathHint: null);
            // Stderr only — stdout is the JSON-RPC channel and must stay protocol-clean.
            Console.Error.WriteLine($"edgehop: branch '{branch}'");
            return branch;
        }
    }

    [McpServerTool(Name = "find_symbol", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Find C# symbols in the code graph by name. Case-insensitive SUBSTRING match on the " +
        "symbol's short name, optionally filtered by kind. NO wildcards or regex: '*' and '?' " +
        "are matched as literal characters, so never pass glob patterns — every search already " +
        "behaves like *query* (e.g. 'greet' matches Greet, Greeting and LoudGreeter). Returns " +
        "up to 25 matches ordered by name, each with the stable symbol id that get_callers " +
        "takes as input. 'truncated' is true when the result was cut off at the limit " +
        "(narrow the query).")]
    public async Task<FindSymbolResult> FindSymbolAsync(
        [Description("Name text to search for (case-insensitive substring). No wildcards — '*' and '?' are matched literally, so pass plain text like 'Greet', never 'Greet*'. Empty returns no matches.")]
        string query,
        [Description("Optional exact kind filter: NamedType, Method, Property, Field, Event or Namespace.")]
        string? kind = null,
        CancellationToken cancellationToken = default)
    {
        // All search behavior (empty-query semantics, limit clamp, exact truncation
        // detection) lives in the shared service so the CLI behaves identically.
        var result = await service
            .FindSymbolsAsync(Branch, query, kind, EdgeHopQueryService.DefaultLimit, cancellationToken)
            .ConfigureAwait(false);

        return new FindSymbolResult(
            Hits: result.Hits.Select(SymbolResult.From).ToArray(),
            Truncated: result.Truncated);
    }

    [McpServerTool(Name = "get_callers", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Find every symbol that calls the given symbol through CALLS, HTTP_CALLS, JS_CALLS or " +
        "JS_INVOKES edges, up to 'depth' hops (depth 1 = direct callers; higher depths add " +
        "transitive callers). HTTP_CALLS covers cross-tier HTTP clients: callers of an API " +
        "endpoint-registration method include the Web-side client methods whose route " +
        "matches one it registers. JS_CALLS/JS_INVOKES cover cross-tier JS interop in both " +
        "directions: callers of a JavaScript function include the C# methods that invoke it via " +
        "IJSRuntime/IJSObjectReference (JS_CALLS), and callers of a C# [JSInvokable] method " +
        "include the JavaScript that invokes it via DotNet.invokeMethod* (JS_INVOKES). Use " +
        "find_symbol first to obtain the target's stable symbol id. The target itself is never " +
        "included in the results.")]
    public async Task<GetCallersResult> GetCallersAsync(
        [Description("Stable symbol id of the callee, exactly as returned by find_symbol (e.g. 'Method:string TinyFixture.Greeter.Greet(string)').")]
        string symbolId,
        [Description("Maximum call-chain depth to traverse, between 1 and 10. Default 1 (direct callers only).")]
        int depth = 1,
        CancellationToken cancellationToken = default)
    {
        CallerSearchResult result;
        try
        {
            result = await service
                .GetCallersAsync(Branch, symbolId, depth, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            // The service throws plain BCL exceptions (Core must stay MCP-free); rethrow
            // as McpException so the client sees the actionable message instead of the
            // generic error produced by an unexpected exception type. The shared Core
            // CleanMessage strips the "(Parameter '…')" decoration so the wire text stays
            // stable and identical to the CLI's stderr text.
            throw new McpException(EdgeHopQueryService.CleanMessage(ex.Message));
        }

        return new GetCallersResult(
            TargetId: result.TargetId,
            Depth: result.Depth,
            Callers: result.Callers.Select(SymbolResult.From).ToArray());
    }

    [McpServerTool(Name = "get_relationships", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Find symbols related to the given symbol by a graph edge, in either direction. Edge " +
        "types are CONTAINS (a type/namespace holds a member), CALLS, IMPLEMENTS, INHERITS, " +
        "REFERENCES, OVERRIDES, RENDERS (a component renders another), HTTP_CALLS " +
        "(cross-tier HTTP client to endpoint), JS_CALLS (C# to JS interop) and JS_INVOKES " +
        "(JS to C# interop). 'direction' is " +
        "'out' (edges FROM the symbol — " +
        "default), 'in' (edges INTO it) or 'both'. Pass 'edgeType' to keep just one type. " +
        "'depth' 1-10 (default 1) walks transitively, but any depth above 1 REQUIRES a single " +
        "'edgeType' — multi-hop mixed-type traversal is not supported. Use find_symbol first to " +
        "obtain the anchor's stable symbol id. The anchor itself is never included; each hit " +
        "carries the edge type that reached it and the direction traversed. 'truncated' is true " +
        "when the fan-out cap was hit (restrict with an edge type).")]
    public async Task<GetRelationshipsResult> GetRelationshipsAsync(
        [Description("Stable symbol id of the anchor, exactly as returned by find_symbol (e.g. 'Method:string TinyFixture.Greeter.Greet(string)').")]
        string symbolId,
        [Description("Traversal direction: 'out' (edges from the symbol — default), 'in' (edges into it) or 'both'.")]
        string direction = "out",
        [Description("Optional exact edge-type filter: CONTAINS, CALLS, IMPLEMENTS, INHERITS, REFERENCES, OVERRIDES, RENDERS, HTTP_CALLS, JS_CALLS or JS_INVOKES. Required when depth is above 1.")]
        string? edgeType = null,
        [Description("Maximum traversal depth, between 1 and 10. Default 1 (direct neighbors only). Any value above 1 requires a single edgeType.")]
        int depth = 1,
        CancellationToken cancellationToken = default)
    {
        if (!RelationshipDirections.TryParse(direction, out var parsedDirection))
        {
            // Not a service exception — the service takes the parsed enum — so surface the
            // usage error directly. Message shape matches the service's actionable voice.
            throw new McpException(
                $"direction must be 'out', 'in' or 'both'; got '{direction}'.");
        }

        RelationshipSearchResult result;
        try
        {
            result = await service
                .GetRelationshipsAsync(Branch, symbolId, parsedDirection, edgeType, depth, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            // Same translation as get_callers: the service throws plain BCL exceptions, so
            // rethrow as McpException through the shared CleanMessage so the wire text stays
            // stable and identical to the CLI's stderr text.
            throw new McpException(EdgeHopQueryService.CleanMessage(ex.Message));
        }

        return new GetRelationshipsResult(
            TargetId: result.TargetId,
            Direction: result.Direction,
            EdgeType: result.EdgeType,
            Depth: result.Depth,
            Hits: result.Hits.Select(RelationshipResult.From).ToArray(),
            Truncated: result.Truncated);
    }

    [McpServerTool(Name = "get_path", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Find one shortest directed path from one symbol to another, following outgoing edges " +
        "up to 'maxLength' hops (1-15, default 10) and optionally restricted to a single " +
        "'edgeType' (CONTAINS, CALLS, IMPLEMENTS, INHERITS, REFERENCES, OVERRIDES, RENDERS, " +
        "HTTP_CALLS, JS_CALLS or JS_INVOKES). Use find_symbol first to obtain both stable symbol ids. 'found' is false " +
        "with no nodes when the target is unreachable within the bound; fromId equal to toId is " +
        "a found single-node path of length 0. Each node past the first carries the edge type " +
        "linking it to the previous node.")]
    public async Task<GetPathResult> GetPathAsync(
        [Description("Stable symbol id of the start node, exactly as returned by find_symbol.")]
        string fromId,
        [Description("Stable symbol id of the goal node, exactly as returned by find_symbol.")]
        string toId,
        [Description("Optional exact edge-type filter: CONTAINS, CALLS, IMPLEMENTS, INHERITS, REFERENCES, OVERRIDES, RENDERS, HTTP_CALLS, JS_CALLS or JS_INVOKES. Default: follow any edge type.")]
        string? edgeType = null,
        [Description("Maximum path length in hops, between 1 and 15. Default 10.")]
        int maxLength = IGraphReader.DefaultPathLength,
        CancellationToken cancellationToken = default)
    {
        PathResult result;
        try
        {
            result = await service
                .GetPathAsync(Branch, fromId, toId, edgeType, maxLength, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw new McpException(EdgeHopQueryService.CleanMessage(ex.Message));
        }

        return new GetPathResult(
            FromId: result.FromId,
            ToId: result.ToId,
            Found: result.Found,
            Nodes: result.Nodes.Select(PathNodeResult.From).ToArray());
    }

    [McpServerTool(Name = "graph_stats", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Summarize the current branch's graph for orientation: total node and edge counts, node " +
        "counts by kind, edge counts by type, and the top 'topN' god nodes (the highest-degree " +
        "symbols). God-node degree EXCLUDES CONTAINS edges, so containers like namespaces and " +
        "types do not dominate — the ranking surfaces the genuinely most-connected members. " +
        "'topN' is 1-50 (default 10) and is clamped into range rather than rejected.")]
    public async Task<GraphStatsResult> GraphStatsAsync(
        [Description("Number of god nodes to return, between 1 and 50. Default 10. Out-of-range values are clamped.")]
        int topN = 10,
        CancellationToken cancellationToken = default)
    {
        GraphStatsResult result;
        try
        {
            var stats = await service
                .GetStatsAsync(Branch, topN, cancellationToken)
                .ConfigureAwait(false);

            result = new GraphStatsResult(
                Branch: stats.Branch,
                TotalNodes: stats.TotalNodes,
                TotalEdges: stats.TotalEdges,
                NodesByKind: stats.NodesByKind.Select(KindCountResult.From).ToArray(),
                EdgesByType: stats.EdgesByType.Select(EdgeTypeCountResult.From).ToArray(),
                GodNodes: stats.GodNodes.Select(DegreeResult.From).ToArray());
        }
        catch (ArgumentException ex)
        {
            throw new McpException(EdgeHopQueryService.CleanMessage(ex.Message));
        }

        return result;
    }
}

/// <summary>One symbol in a tool result. JSON property names are pinned explicitly so the
/// wire shape (id/name/kind/sourceDoc/isComponent/routes) is independent of serializer casing
/// policy. <c>isComponent</c> is always emitted; <c>routes</c> is omitted when null (the
/// symbol is not a routable component, or the store predates the metadata and needs a
/// re-index).</summary>
public sealed record SymbolResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("sourceDoc")] string? SourceDoc,
    [property: JsonPropertyName("isComponent")] bool IsComponent,
    [property: JsonPropertyName("routes")] IReadOnlyList<string>? Routes)
{
    internal static SymbolResult From(SymbolHit hit) =>
        new(hit.Id, hit.Name, hit.Kind, hit.SourceDoc, hit.IsComponent, hit.Routes);
}

/// <summary>Result of <c>find_symbol</c>: the matching symbols plus a flag indicating the
/// result was cut off at the 25-hit limit (i.e. more matches exist — narrow the query).</summary>
public sealed record FindSymbolResult(
    [property: JsonPropertyName("hits")] IReadOnlyList<SymbolResult> Hits,
    [property: JsonPropertyName("truncated")] bool Truncated);

/// <summary>Result of <c>get_callers</c>: echoes the resolved target id and depth, plus the
/// distinct callers found within that many CALLS/HTTP_CALLS/JS_CALLS/JS_INVOKES hops (the target
/// itself excluded).</summary>
public sealed record GetCallersResult(
    [property: JsonPropertyName("targetId")] string TargetId,
    [property: JsonPropertyName("depth")] int Depth,
    [property: JsonPropertyName("callers")] IReadOnlyList<SymbolResult> Callers);

/// <summary>One related symbol in a <c>get_relationships</c> result: the neighbor plus the
/// edge type that reached it and the direction traversed ('out'/'in').</summary>
public sealed record RelationshipResult(
    [property: JsonPropertyName("symbol")] SymbolResult Symbol,
    [property: JsonPropertyName("edgeType")] string EdgeType,
    [property: JsonPropertyName("direction")] string Direction)
{
    internal static RelationshipResult From(RelationshipHit hit) =>
        new(SymbolResult.From(hit.Symbol), hit.EdgeType, hit.Direction);
}

/// <summary>Result of <c>get_relationships</c>: echoes the anchor id, direction, edge-type
/// filter (null when unfiltered) and depth, plus the related hits and a flag that is true when
/// the result was cut off at the fan-out cap (more relations exist — restrict with an edge
/// type).</summary>
public sealed record GetRelationshipsResult(
    [property: JsonPropertyName("targetId")] string TargetId,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("edgeType")] string? EdgeType,
    [property: JsonPropertyName("depth")] int Depth,
    [property: JsonPropertyName("hits")] IReadOnlyList<RelationshipResult> Hits,
    [property: JsonPropertyName("truncated")] bool Truncated);

/// <summary>One node on a <c>get_path</c> result (index 0 = fromId). <c>edgeTypeFromPrev</c>
/// is null for the first node, else the edge type linking the previous node to this one.</summary>
public sealed record PathNodeResult(
    [property: JsonPropertyName("symbol")] SymbolResult Symbol,
    [property: JsonPropertyName("edgeTypeFromPrev")] string? EdgeTypeFromPrev)
{
    internal static PathNodeResult From(PathNode node) =>
        new(SymbolResult.From(node.Symbol), node.EdgeTypeFromPrev);
}

/// <summary>Result of <c>get_path</c>: the endpoints echoed back, whether a path was found,
/// and the ordered nodes (empty when not found; a single node for a zero-length path).</summary>
public sealed record GetPathResult(
    [property: JsonPropertyName("fromId")] string FromId,
    [property: JsonPropertyName("toId")] string ToId,
    [property: JsonPropertyName("found")] bool Found,
    [property: JsonPropertyName("nodes")] IReadOnlyList<PathNodeResult> Nodes);

/// <summary>Node count for one kind in a <c>graph_stats</c> result.</summary>
public sealed record KindCountResult(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("count")] long Count)
{
    internal static KindCountResult From(KindCount count) => new(count.Kind, count.Count);
}

/// <summary>Edge count for one type in a <c>graph_stats</c> result.</summary>
public sealed record EdgeTypeCountResult(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("count")] long Count)
{
    internal static EdgeTypeCountResult From(EdgeTypeCount count) => new(count.Type, count.Count);
}

/// <summary>One god node in a <c>graph_stats</c> result: a symbol and its degree (edges
/// excluding CONTAINS).</summary>
public sealed record DegreeResult(
    [property: JsonPropertyName("symbol")] SymbolResult Symbol,
    [property: JsonPropertyName("degree")] long Degree)
{
    internal static DegreeResult From(DegreeHit hit) => new(SymbolResult.From(hit.Symbol), hit.Degree);
}

/// <summary>Result of <c>graph_stats</c>: the branch and per-branch totals, node counts by
/// kind, edge counts by type, and the highest-degree god nodes (degree excludes CONTAINS).</summary>
public sealed record GraphStatsResult(
    [property: JsonPropertyName("branch")] string Branch,
    [property: JsonPropertyName("totalNodes")] long TotalNodes,
    [property: JsonPropertyName("totalEdges")] long TotalEdges,
    [property: JsonPropertyName("nodesByKind")] IReadOnlyList<KindCountResult> NodesByKind,
    [property: JsonPropertyName("edgesByType")] IReadOnlyList<EdgeTypeCountResult> EdgesByType,
    [property: JsonPropertyName("godNodes")] IReadOnlyList<DegreeResult> GodNodes);
