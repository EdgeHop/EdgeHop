using System.Text.Json;
using System.Text.Json.Serialization;
using EdgeHop.Core;

namespace EdgeHop.Cli;

/// <summary>
/// Argument parsing, environment wiring and rendering for the <c>edgehop</c> CLI.
/// Query behavior lives entirely in <see cref="EdgeHopQueryService"/> (shared with the
/// MCP server); this class must never reimplement validation, clamping or truncation.
/// </summary>
internal static class CliApp
{
    /// <summary>
    /// Branch when <c>--branch</c> is not given: the shared <see cref="BranchResolver"/>
    /// chain (<c>EDGEHOP_BRANCH</c> &gt; git branch of <c>EDGEHOP_REPO</c> &gt; git
    /// branch of the current directory &gt; <c>"main"</c>) — the same rule the MCP
    /// server applies per call, with the current directory as the natural path hint so
    /// running <c>edgehop</c> from inside a repo queries that repo's checked-out
    /// branch. The explicit flag stays first because local use (and the test suite)
    /// legitimately queries throwaway branches.
    /// </summary>
    private static string ResolveBranch(string? explicitBranch) =>
        BranchResolver.Resolve(explicitBranch, Environment.CurrentDirectory);

    /// <summary>CamelCase serialization reproduces the MCP wire property names exactly
    /// (hits/truncated, targetId/depth/callers, id/name/kind/sourceDoc), and
    /// WhenWritingNull mirrors the MCP SDK's default serializer, which OMITS null
    /// properties — a hit with no sourceDoc (namespace/metadata symbols) has no
    /// "sourceDoc" key on either surface, keeping --json byte-shape-compatible.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            WriteRootUsage(Console.Error);
            return 1;
        }

        if (args[0] is "--help" or "-h" or "help")
        {
            WriteRootUsage(Console.Out);
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "find-symbol" => await RunFindSymbolAsync(args[1..]).ConfigureAwait(false),
                "get-callers" => await RunGetCallersAsync(args[1..]).ConfigureAwait(false),
                "get-relationships" => await RunGetRelationshipsAsync(args[1..]).ConfigureAwait(false),
                "get-path" => await RunGetPathAsync(args[1..]).ConfigureAwait(false),
                "stats" => await RunStatsAsync(args[1..]).ConfigureAwait(false),
                _ => UsageError($"Unknown command '{args[0]}'.", WriteRootUsage),
            };
        }
        catch (ArgumentException ex)
        {
            // Validation errors from the shared service (bad depth, empty symbolId, …):
            // the human-authored sentence only, no stack trace. The shared Core
            // CleanMessage keeps this text identical to the MCP wire error text.
            return Error(EdgeHopQueryService.CleanMessage(ex.Message));
        }
        catch (GraphStoreException ex)
        {
            // The active backend's runtime failure, translated to a Core type at the read
            // boundary so this CLI references no driver: a Neo4j connectivity/auth/query
            // error or a SQLite locked/corrupt/unwritable-store error — environment
            // problems, not bugs, so message only. (Driver messages never contain secrets.)
            return Error($"Backend error: {EdgeHopQueryService.CleanMessage(ex.Message)}");
        }
    }

    // ------------------------------------------------------------------ find-symbol --

    private static async Task<int> RunFindSymbolAsync(string[] args)
    {
        string? query = null;
        string? kind = null;
        string? branch = null;
        var limit = EdgeHopQueryService.DefaultLimit;
        var json = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h":
                    WriteFindSymbolUsage(Console.Out);
                    return 0;
                case "--json":
                    json = true;
                    break;
                case "--kind":
                    if (!TryTakeValue(args, ref i, out kind))
                    {
                        return UsageError("--kind requires a value.", WriteFindSymbolUsage);
                    }
                    break;
                case "--branch":
                    if (!TryTakeValue(args, ref i, out branch!))
                    {
                        return UsageError("--branch requires a value.", WriteFindSymbolUsage);
                    }
                    break;
                case "--limit":
                    if (!TryTakeValue(args, ref i, out var limitText) ||
                        !int.TryParse(limitText, out limit))
                    {
                        return UsageError("--limit requires an integer value.", WriteFindSymbolUsage);
                    }
                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        return UsageError($"Unknown option '{args[i]}' for find-symbol.", WriteFindSymbolUsage);
                    }
                    if (query is not null)
                    {
                        return UsageError("find-symbol takes exactly one <query> argument.", WriteFindSymbolUsage);
                    }
                    query = args[i];
                    break;
            }
        }

        if (query is null)
        {
            return UsageError("find-symbol requires a <query> argument.", WriteFindSymbolUsage);
        }

        if (!TryCreateStore(out var store, out var exitCode))
        {
            return exitCode;
        }

        var resolvedBranch = ResolveBranch(branch);
        await using (store.ConfigureAwait(false))
        {
            var service = new EdgeHopQueryService(store.Reader);
            var result = await service.FindSymbolsAsync(resolvedBranch, query, kind, limit).ConfigureAwait(false);

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                return 0;
            }

            if (result.Hits.Count == 0)
            {
                Console.WriteLine($"0 matches for '{query}' on branch '{resolvedBranch}'." +
                    " (Search is a case-insensitive substring match; wildcards like '*' are matched literally.)");
                return 0;
            }

            foreach (var hit in result.Hits)
            {
                WriteHit(hit);
            }

            var noun = result.Hits.Count == 1 ? "match" : "matches";
            Console.WriteLine(result.Truncated
                ? $"{result.Hits.Count} {noun} on branch '{resolvedBranch}' — TRUNCATED, more exist (narrow the query or raise --limit)."
                : $"{result.Hits.Count} {noun} on branch '{resolvedBranch}'.");
            return 0;
        }
    }

    // ------------------------------------------------------------------ get-callers --

    private static async Task<int> RunGetCallersAsync(string[] args)
    {
        string? symbolId = null;
        string? branch = null;
        var depth = 1;
        var json = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h":
                    WriteGetCallersUsage(Console.Out);
                    return 0;
                case "--json":
                    json = true;
                    break;
                case "--branch":
                    if (!TryTakeValue(args, ref i, out branch!))
                    {
                        return UsageError("--branch requires a value.", WriteGetCallersUsage);
                    }
                    break;
                case "--depth":
                    if (!TryTakeValue(args, ref i, out var depthText) ||
                        !int.TryParse(depthText, out depth))
                    {
                        return UsageError("--depth requires an integer value.", WriteGetCallersUsage);
                    }
                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        return UsageError($"Unknown option '{args[i]}' for get-callers.", WriteGetCallersUsage);
                    }
                    if (symbolId is not null)
                    {
                        return UsageError("get-callers takes exactly one <symbolId> argument.", WriteGetCallersUsage);
                    }
                    symbolId = args[i];
                    break;
            }
        }

        if (symbolId is null)
        {
            return UsageError("get-callers requires a <symbolId> argument (as returned by find-symbol).", WriteGetCallersUsage);
        }

        if (!TryCreateStore(out var store, out var exitCode))
        {
            return exitCode;
        }

        var resolvedBranch = ResolveBranch(branch);
        await using (store.ConfigureAwait(false))
        {
            var service = new EdgeHopQueryService(store.Reader);

            // Depth/symbolId validation happens inside the shared service; its
            // ArgumentException surfaces via RunAsync's catch as exit 1 + message.
            var result = await service.GetCallersAsync(resolvedBranch, symbolId, depth).ConfigureAwait(false);

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                return 0;
            }

            Console.WriteLine($"Callers of {result.TargetId} (depth <= {result.Depth}, branch '{resolvedBranch}'):");
            Console.WriteLine();

            foreach (var caller in result.Callers)
            {
                WriteHit(caller);
            }

            Console.WriteLine(result.Callers.Count == 1 ? "1 caller." : $"{result.Callers.Count} callers.");
            return 0;
        }
    }

    // ----------------------------------------------------------- get-relationships --

    private static async Task<int> RunGetRelationshipsAsync(string[] args)
    {
        string? symbolId = null;
        string? branch = null;
        string? edgeType = null;
        var direction = RelationshipDirection.Out;
        var depth = 1;
        var json = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h":
                    WriteGetRelationshipsUsage(Console.Out);
                    return 0;
                case "--json":
                    json = true;
                    break;
                case "--direction":
                    if (!TryTakeValue(args, ref i, out var directionText) ||
                        !RelationshipDirections.TryParse(directionText, out direction))
                    {
                        return UsageError("--direction requires a value of out, in or both.", WriteGetRelationshipsUsage);
                    }
                    break;
                case "--edge-type":
                    if (!TryTakeValue(args, ref i, out edgeType))
                    {
                        return UsageError("--edge-type requires a value.", WriteGetRelationshipsUsage);
                    }
                    break;
                case "--branch":
                    if (!TryTakeValue(args, ref i, out branch!))
                    {
                        return UsageError("--branch requires a value.", WriteGetRelationshipsUsage);
                    }
                    break;
                case "--depth":
                    if (!TryTakeValue(args, ref i, out var depthText) ||
                        !int.TryParse(depthText, out depth))
                    {
                        return UsageError("--depth requires an integer value.", WriteGetRelationshipsUsage);
                    }
                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        return UsageError($"Unknown option '{args[i]}' for get-relationships.", WriteGetRelationshipsUsage);
                    }
                    if (symbolId is not null)
                    {
                        return UsageError("get-relationships takes exactly one <symbolId> argument.", WriteGetRelationshipsUsage);
                    }
                    symbolId = args[i];
                    break;
            }
        }

        if (symbolId is null)
        {
            return UsageError("get-relationships requires a <symbolId> argument (as returned by find-symbol).", WriteGetRelationshipsUsage);
        }

        if (!TryCreateStore(out var store, out var exitCode))
        {
            return exitCode;
        }

        var resolvedBranch = ResolveBranch(branch);
        await using (store.ConfigureAwait(false))
        {
            var service = new EdgeHopQueryService(store.Reader);

            // Direction/edge-type/depth validation happens inside the shared service; its
            // ArgumentException surfaces via RunAsync's catch as exit 1 + message.
            var result = await service
                .GetRelationshipsAsync(resolvedBranch, symbolId, direction, edgeType, depth)
                .ConfigureAwait(false);

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                return 0;
            }

            var filter = result.EdgeType is null ? "any type" : result.EdgeType;
            Console.WriteLine($"Relationships of {result.TargetId} (direction {result.Direction}, {filter}, depth <= {result.Depth}, branch '{resolvedBranch}'):");
            Console.WriteLine();

            foreach (var hit in result.Hits)
            {
                Console.WriteLine($"{hit.EdgeType} {hit.Direction}");
                WriteHit(hit.Symbol);
            }

            var noun = result.Hits.Count == 1 ? "relationship" : "relationships";
            Console.WriteLine(result.Truncated
                ? $"{result.Hits.Count} {noun} on branch '{resolvedBranch}' — TRUNCATED, more exist (narrow with --edge-type)."
                : $"{result.Hits.Count} {noun} on branch '{resolvedBranch}'.");
            return 0;
        }
    }

    // --------------------------------------------------------------------- get-path --

    private static async Task<int> RunGetPathAsync(string[] args)
    {
        string? fromId = null;
        string? toId = null;
        string? branch = null;
        string? edgeType = null;
        var maxLength = 10;
        var json = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h":
                    WriteGetPathUsage(Console.Out);
                    return 0;
                case "--json":
                    json = true;
                    break;
                case "--edge-type":
                    if (!TryTakeValue(args, ref i, out edgeType))
                    {
                        return UsageError("--edge-type requires a value.", WriteGetPathUsage);
                    }
                    break;
                case "--branch":
                    if (!TryTakeValue(args, ref i, out branch!))
                    {
                        return UsageError("--branch requires a value.", WriteGetPathUsage);
                    }
                    break;
                case "--max-length":
                    if (!TryTakeValue(args, ref i, out var maxLengthText) ||
                        !int.TryParse(maxLengthText, out maxLength))
                    {
                        return UsageError("--max-length requires an integer value.", WriteGetPathUsage);
                    }
                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        return UsageError($"Unknown option '{args[i]}' for get-path.", WriteGetPathUsage);
                    }
                    if (fromId is null)
                    {
                        fromId = args[i];
                    }
                    else if (toId is null)
                    {
                        toId = args[i];
                    }
                    else
                    {
                        return UsageError("get-path takes exactly two arguments: <fromId> <toId>.", WriteGetPathUsage);
                    }
                    break;
            }
        }

        if (fromId is null || toId is null)
        {
            return UsageError("get-path requires two arguments: <fromId> <toId> (as returned by find-symbol).", WriteGetPathUsage);
        }

        if (!TryCreateStore(out var store, out var exitCode))
        {
            return exitCode;
        }

        var resolvedBranch = ResolveBranch(branch);
        await using (store.ConfigureAwait(false))
        {
            var service = new EdgeHopQueryService(store.Reader);

            // fromId/toId/edge-type/max-length validation happens inside the shared service.
            var result = await service
                .GetPathAsync(resolvedBranch, fromId, toId, edgeType, maxLength)
                .ConfigureAwait(false);

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                return 0;
            }

            if (!result.Found)
            {
                Console.WriteLine($"No path from {result.FromId} to {result.ToId} on branch '{resolvedBranch}'.");
                return 0;
            }

            var hops = result.Nodes.Count - 1;
            var noun = hops == 1 ? "hop" : "hops";
            Console.WriteLine($"Path from {result.FromId} to {result.ToId} on branch '{resolvedBranch}' ({hops} {noun}):");
            Console.WriteLine();

            var arrow = new System.Text.StringBuilder();
            for (var i = 0; i < result.Nodes.Count; i++)
            {
                if (i > 0)
                {
                    arrow.Append($" --{result.Nodes[i].EdgeTypeFromPrev}--> ");
                }
                arrow.Append(result.Nodes[i].Symbol.Name);
            }
            Console.WriteLine(arrow.ToString());
            Console.WriteLine();

            foreach (var node in result.Nodes)
            {
                WriteHit(node.Symbol);
            }

            return 0;
        }
    }

    // ------------------------------------------------------------------------ stats --

    private static async Task<int> RunStatsAsync(string[] args)
    {
        string? branch = null;
        var topN = 10;
        var json = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h":
                    WriteStatsUsage(Console.Out);
                    return 0;
                case "--json":
                    json = true;
                    break;
                case "--branch":
                    if (!TryTakeValue(args, ref i, out branch!))
                    {
                        return UsageError("--branch requires a value.", WriteStatsUsage);
                    }
                    break;
                case "--top":
                    if (!TryTakeValue(args, ref i, out var topText) ||
                        !int.TryParse(topText, out topN))
                    {
                        return UsageError("--top requires an integer value.", WriteStatsUsage);
                    }
                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        return UsageError($"Unknown option '{args[i]}' for stats.", WriteStatsUsage);
                    }
                    return UsageError("stats takes no positional arguments.", WriteStatsUsage);
            }
        }

        if (!TryCreateStore(out var store, out var exitCode))
        {
            return exitCode;
        }

        var resolvedBranch = ResolveBranch(branch);
        await using (store.ConfigureAwait(false))
        {
            var service = new EdgeHopQueryService(store.Reader);

            // topN is clamped (not rejected) inside the shared service.
            var result = await service.GetStatsAsync(resolvedBranch, topN).ConfigureAwait(false);

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                return 0;
            }

            Console.WriteLine($"Graph stats for branch '{result.Branch}':");
            Console.WriteLine($"  {result.TotalNodes} nodes, {result.TotalEdges} edges");
            Console.WriteLine();

            Console.WriteLine("Nodes by kind:");
            foreach (var kind in result.NodesByKind)
            {
                Console.WriteLine($"  {kind.Kind,-12} {kind.Count}");
            }
            Console.WriteLine();

            Console.WriteLine("Edges by type:");
            foreach (var type in result.EdgesByType)
            {
                Console.WriteLine($"  {type.Type,-12} {type.Count}");
            }
            Console.WriteLine();

            var noun = result.GodNodes.Count == 1 ? "node" : "nodes";
            Console.WriteLine($"God {noun} (top by degree, excluding CONTAINS):");
            Console.WriteLine();
            foreach (var god in result.GodNodes)
            {
                Console.WriteLine($"degree {god.Degree}");
                WriteHit(god.Symbol);
            }

            return 0;
        }
    }

    // ---------------------------------------------------------------------- helpers --

    /// <summary>Obtains the backend store from the shared factory. On missing
    /// configuration, prints the settings message (names the variables, never any value)
    /// and yields exit code 1. Store creation never opens a connection.</summary>
    private static bool TryCreateStore(out IGraphStore store, out int failureExitCode)
    {
        try
        {
            // The cwd is the CLI's natural path hint, exactly as in ResolveBranch: the
            // sqlite store-per-solution derivation follows the repo you run from.
            store = GraphStoreFactory.FromEnvironment(Environment.CurrentDirectory);
            // Stderr so --json stdout stays pipeable; makes a misconfigured session
            // (wrong EDGEHOP_BACKEND / store path) obvious on every run.
            Console.Error.WriteLine($"Backend: {store.Description}");
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            store = null!;
            failureExitCode = 1;
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or NotSupportedException)
        {
            // Malformed NEO4J_URI (bad scheme, unparseable value, …): message only — no
            // stack trace, and never the raw URI (it could embed userinfo credentials).
            Console.Error.WriteLine($"Invalid NEO4J_URI: {EdgeHopQueryService.CleanMessage(ex.Message)}");
            store = null!;
            failureExitCode = 1;
            return false;
        }

        failureExitCode = 0;
        return true;
    }

    /// <summary>Consumes the value token following a <c>--flag</c>; false when absent.</summary>
    private static bool TryTakeValue(string[] args, ref int i, out string? value)
    {
        if (i + 1 >= args.Length)
        {
            value = null;
            return false;
        }

        value = args[++i];
        return true;
    }

    private static void WriteHit(SymbolHit hit)
    {
        Console.WriteLine($"{hit.Kind,-10} {hit.Name}");
        Console.WriteLine($"           id:  {hit.Id}");
        if (hit.SourceDoc is not null)
        {
            Console.WriteLine($"           doc: {hit.SourceDoc}");
        }
        // Component-graph facets, appended only when present so a plain symbol's three
        // lines above stay byte-identical (existing CLI tests pin that output).
        if (hit.IsComponent)
        {
            Console.WriteLine("           component");
        }
        if (hit.Routes is { Count: > 0 })
        {
            Console.WriteLine($"           routes: {string.Join(", ", hit.Routes)}");
        }
        Console.WriteLine();
    }

    private static int Error(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static int UsageError(string message, Action<TextWriter> usage)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine();
        usage(Console.Error);
        return 1;
    }

    // ------------------------------------------------------------------------ usage --

    private static void WriteRootUsage(TextWriter w)
    {
        w.WriteLine("edgehop — query the EdgeHop code graph from the command line.");
        w.WriteLine();
        w.WriteLine("usage:");
        w.WriteLine("  edgehop find-symbol <query> [--kind <kind>] [--limit <n>] [--branch <name>] [--json]");
        w.WriteLine("  edgehop get-callers <symbolId> [--depth <1-10>] [--branch <name>] [--json]");
        w.WriteLine("  edgehop get-relationships <symbolId> [--direction <out|in|both>] [--edge-type <TYPE>] [--depth <1-10>] [--branch <name>] [--json]");
        w.WriteLine("  edgehop get-path <fromId> <toId> [--edge-type <TYPE>] [--max-length <1-15>] [--branch <name>] [--json]");
        w.WriteLine("  edgehop stats [--top <n>] [--branch <name>] [--json]");
        w.WriteLine("  edgehop <command> --help");
        w.WriteLine();
        w.WriteLine("Search semantics: case-insensitive SUBSTRING match — 'greet' finds Greet, Greeting");
        w.WriteLine("and LoudGreeter. NO wildcards or regex: '*' and '?' are matched as literal");
        w.WriteLine("characters, so 'greet*' finds nothing. Every search already behaves like *query*.");
        w.WriteLine();
        w.WriteLine("Connection comes from the NEO4J_URI, NEO4J_USER, NEO4J_PASSWORD and NEO4J_DATABASE");
        w.WriteLine("environment variables. When --branch is omitted the branch is resolved like the MCP");
        w.WriteLine("server's: EDGEHOP_BRANCH env var, else the current git branch of EDGEHOP_REPO,");
        w.WriteLine("else the current directory's git branch, else 'main'.");
        w.WriteLine("--json prints the MCP wire shape on stdout. Exit codes: 0 success (even with 0");
        w.WriteLine("matches); 1 usage, configuration or validation error.");
    }

    private static void WriteFindSymbolUsage(TextWriter w)
    {
        w.WriteLine("usage: edgehop find-symbol <query> [--kind <kind>] [--limit <n>] [--branch <name>] [--json]");
        w.WriteLine();
        w.WriteLine("Find symbols by name. Case-insensitive SUBSTRING match: 'greet' finds Greet,");
        w.WriteLine("Greeting and LoudGreeter. NO wildcards or regex — '*' and '?' are literal.");
        w.WriteLine();
        w.WriteLine("options:");
        w.WriteLine($"  --kind <kind>    Exact kind filter: {SymbolKinds.NamedType}, {SymbolKinds.Method}, {SymbolKinds.Property}, {SymbolKinds.Field}, {SymbolKinds.Event} or {SymbolKinds.Namespace}.");
        w.WriteLine($"  --limit <n>      Maximum matches to return (default {EdgeHopQueryService.DefaultLimit}, capped at {EdgeHopQueryService.MaxRequestLimit}).");
        w.WriteLine("  --branch <name>  Branch to query (default: resolved from env/git, else 'main').");
        w.WriteLine("  --json           Print the MCP wire shape ({\"hits\":[...],\"truncated\":...}) on stdout.");
    }

    private static void WriteGetCallersUsage(TextWriter w)
    {
        w.WriteLine("usage: edgehop get-callers <symbolId> [--depth <1-10>] [--branch <name>] [--json]");
        w.WriteLine();
        w.WriteLine("Find every symbol that calls <symbolId> through CALLS, HTTP_CALLS, JS_CALLS or JS_INVOKES edges, up to --depth hops.");
        w.WriteLine("Get the stable symbol id from find-symbol first. The target itself is never included.");
        w.WriteLine();
        w.WriteLine("options:");
        w.WriteLine("  --depth <n>      Maximum call-chain depth, 1-10 (default 1 = direct callers).");
        w.WriteLine("  --branch <name>  Branch to query (default: resolved from env/git, else 'main').");
        w.WriteLine("  --json           Print the MCP wire shape ({\"targetId\":...,\"depth\":...,\"callers\":[...]}) on stdout.");
    }

    private static void WriteGetRelationshipsUsage(TextWriter w)
    {
        w.WriteLine("usage: edgehop get-relationships <symbolId> [--direction <out|in|both>] [--edge-type <TYPE>] [--depth <1-10>] [--branch <name>] [--json]");
        w.WriteLine();
        w.WriteLine("Find symbols related to <symbolId> by graph edges, each tagged with the edge type");
        w.WriteLine("that reached it and the direction traversed. Get the stable symbol id from");
        w.WriteLine("find-symbol first. The anchor itself is never included.");
        w.WriteLine();
        w.WriteLine("options:");
        w.WriteLine($"  --direction <d>  Traversal direction: {RelationshipDirections.Out}, {RelationshipDirections.In} or {RelationshipDirections.Both} (default {RelationshipDirections.Out} = outgoing edges).");
        w.WriteLine($"  --edge-type <T>  Restrict to one edge type: {string.Join(", ", EdgeTypes.All)}.");
        w.WriteLine("  --depth <n>      Maximum hops, 1-10 (default 1). Depth > 1 requires a single --edge-type.");
        w.WriteLine("  --branch <name>  Branch to query (default: resolved from env/git, else 'main').");
        w.WriteLine("  --json           Print the MCP wire shape ({\"targetId\":...,\"direction\":...,\"hits\":[...],\"truncated\":...}) on stdout.");
    }

    private static void WriteGetPathUsage(TextWriter w)
    {
        w.WriteLine("usage: edgehop get-path <fromId> <toId> [--edge-type <TYPE>] [--max-length <1-15>] [--branch <name>] [--json]");
        w.WriteLine();
        w.WriteLine("Trace one shortest directed path from <fromId> to <toId> along outgoing edges,");
        w.WriteLine("optionally restricted to a single edge type. Get the stable symbol ids from");
        w.WriteLine("find-symbol first. fromId equal to toId yields a found zero-length path.");
        w.WriteLine();
        w.WriteLine("options:");
        w.WriteLine($"  --edge-type <T>  Restrict the path to one edge type: {string.Join(", ", EdgeTypes.All)}.");
        w.WriteLine("  --max-length <n> Maximum path length in hops, 1-15 (default 10).");
        w.WriteLine("  --branch <name>  Branch to query (default: resolved from env/git, else 'main').");
        w.WriteLine("  --json           Print the MCP wire shape ({\"fromId\":...,\"toId\":...,\"found\":...,\"nodes\":[...]}) on stdout.");
    }

    private static void WriteStatsUsage(TextWriter w)
    {
        w.WriteLine("usage: edgehop stats [--top <n>] [--branch <name>] [--json]");
        w.WriteLine();
        w.WriteLine("Show per-branch orientation stats: total node/edge counts, node counts by kind,");
        w.WriteLine("edge counts by type, and the highest-degree god nodes (degree excludes CONTAINS).");
        w.WriteLine();
        w.WriteLine("options:");
        w.WriteLine("  --top <n>        Number of god nodes to list, clamped to 1-50 (default 10).");
        w.WriteLine("  --branch <name>  Branch to query (default: resolved from env/git, else 'main').");
        w.WriteLine("  --json           Print the MCP wire shape ({\"branch\":...,\"totalNodes\":...,\"godNodes\":[...]}) on stdout.");
    }
}
