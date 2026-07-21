using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EdgeHop.Core;
using Neo4j.Driver;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// Class fixture for <see cref="McpServerStdioTests"/>: seeds a unique GUID test branch
/// with a TinyFixture-shaped graph (plus truncation fillers), launches the built
/// <c>EdgeHop.Mcp</c> executable ONCE for the class over raw stdio JSON-RPC — exactly
/// as the repo-root <c>.mcp.json</c> launches it — with <c>EDGEHOP_BRANCH</c> injected
/// into the child's environment so the server queries the seeded branch. This
/// simultaneously proves the branch-injection seam end-to-end: every assertion depends on
/// rows that exist ONLY under the injected branch, so a server that ignored the env var
/// (or still pinned <c>"main"</c>) fails every data test. Shuts the server down by
/// closing stdin, then deletes ONLY the seeded branch.
/// <para>
/// The test project deliberately has NO project reference to <c>EdgeHop.Mcp</c> (the
/// dependency set is pinned and csproj files are frozen), so the tool layer is exercised
/// end-to-end through the real server process instead: Program.cs host wiring, stdout
/// hygiene, tool registration, argument binding, per-call branch resolution, validation
/// and truncation behavior are all on the tested path.
/// </para>
/// <para>
/// When Neo4j is not configured the fixture no-ops entirely (all tests in the class are
/// <see cref="Neo4jFactAttribute"/> and get skipped). NEO4J_* credentials are inherited
/// by the child process from this process's environment and are never read, logged or
/// printed here — stderr is password-redacted defensively before it can appear in any
/// failure message.
/// </para>
/// </summary>
public class McpServerFixture : IAsyncLifetime
{
    private const int DefaultTimeoutMs = 30_000;

    /// <summary>Number of extra symbols (names containing 'a') seeded so a broad query
    /// exceeds the 25-hit limit and provably truncates.</summary>
    private const int FillerCount = 30;

    private readonly List<string> _stdoutLines = [];
    private readonly object _gate = new();

    private Process? _proc;
    private Task<string>? _stderrTask;
    private IDriver? _driver;
    private int _nextRequestId = 10;

    /// <summary>The unique branch this run seeds and the server under test must query.</summary>
    public string TestBranch { get; } = $"test-mcp-{Guid.NewGuid():N}";

    /// <summary>Every non-empty line the server has written to stdout so far (JSON-RPC
    /// frames if the stdio hygiene invariant holds — asserted by a dedicated test).</summary>
    public IReadOnlyList<string> StdoutLinesSnapshot()
    {
        lock (_gate)
        {
            return _stdoutLines.ToList();
        }
    }

    public async Task InitializeAsync()
    {
        if (!Neo4jSettings.IsConfigured)
        {
            // All tests in the class are [Neo4jFact] and will be skipped; the fixture
            // must not fail construction just because no database is reachable.
            return;
        }

        await SeedTestBranchAsync();

        var exePath = LocateServerExecutable(out var repoRoot);

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            // NEO4J_* env vars are inherited from this process automatically; they are
            // intentionally never enumerated or copied here.
        };

        // This fixture tests the server over Neo4j; since Handoff 3 the default backend
        // is sqlite, so the child is pinned explicitly.
        psi.Environment["EDGEHOP_BACKEND"] = "neo4j";

        // Deterministic branch environment for the child regardless of anything another
        // test may have set process-wide: both EDGEHOP_* vars are explicitly
        // overwritten/removed on the snapshot the child actually receives.
        ConfigureBranchEnvironment(psi);

        _proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start MCP server process '{exePath}'.");
        _proc.StandardInput.AutoFlush = true;

        // Drain stderr continuously so the child can never block on a full stderr pipe.
        _stderrTask = _proc.StandardError.ReadToEndAsync();

        var initResponse = await SendRequestAsync(
            "initialize",
            new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "edgehop-tests", version = "0.0.1" },
            },
            timeoutMs: 60_000);

        if (initResponse.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException(
                $"MCP initialize failed: {error.GetRawText()}{Environment.NewLine}{RedactedStderr()}");
        }

        var serverName = initResponse.GetProperty("result").GetProperty("serverInfo")
            .GetProperty("name").GetString();
        if (serverName != "edgehop")
        {
            throw new InvalidOperationException(
                $"Unexpected serverInfo.name '{serverName}' (expected 'edgehop').");
        }

        await SendNotificationAsync("notifications/initialized");
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (_proc is not null)
            {
                try
                {
                    // Closing stdin ends the stdio session; the host shuts down gracefully.
                    try
                    {
                        _proc.StandardInput.Close();
                    }
                    catch
                    {
                        // Stream may already be closed if the process died mid-test.
                    }

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    try
                    {
                        await _proc.WaitForExitAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        try
                        {
                            _proc.Kill(entireProcessTree: true);
                        }
                        catch
                        {
                            // Already exited between the timeout and the kill.
                        }
                    }
                }
                finally
                {
                    _proc.Dispose();
                }
            }
        }
        finally
        {
            await CleanupAsync();
        }
    }

    /// <summary>
    /// Points the child server at the seeded branch. The base fixture injects
    /// <c>EDGEHOP_BRANCH</c> directly (the pin seam); derived fixtures may exercise
    /// other rungs of the resolution chain. Both EDGEHOP_* variables are always set or
    /// removed explicitly so nothing leaks in from this process's environment.
    /// </summary>
    protected virtual void ConfigureBranchEnvironment(ProcessStartInfo psi)
    {
        psi.Environment["EDGEHOP_BRANCH"] = TestBranch;
        psi.Environment.Remove("EDGEHOP_REPO");
    }

    /// <summary>Hook for derived fixtures to clean up extra resources.</summary>
    protected virtual Task DisposeExtraAsync() => Task.CompletedTask;

    private async Task SeedTestBranchAsync()
    {
        var settings = Neo4jSettings.FromEnvironment();
        _driver = GraphDatabase.Driver(settings.Uri, AuthTokens.Basic(settings.User, settings.Password));
        await Neo4jSchema.ApplyAsync(_driver, settings.Database);

        var writer = new Neo4jGraphWriter(_driver, settings.Database);
        await writer.UpsertNodesAsync(BuildSeedNodes());
        await writer.UpsertEdgesAsync(BuildSeedEdges());
    }

    private async Task CleanupAsync()
    {
        try
        {
            if (_driver is not null)
            {
                var settings = Neo4jSettings.FromEnvironment();
                var writer = new Neo4jGraphWriter(_driver, settings.Database);
                await writer.DeleteBranchAsync(TestBranch); // branch-scoped; only the GUID branch.
                await _driver.DisposeAsync();
            }
        }
        finally
        {
            await DisposeExtraAsync();
        }
    }

    /// <summary>
    /// The TinyFixture-shaped rows the stdio tests assert against (ids mirror
    /// EXPECTED-GRAPH.md), stamped with <see cref="TestBranch"/>, plus
    /// <see cref="FillerCount"/> filler methods whose names contain 'a' so the broad
    /// truncation query provably exceeds the 25-hit limit without depending on any other
    /// branch's data, plus two Razor component nodes (one routable, one not) so the
    /// query-surface tools can assert isComponent/routes surfacing and a RENDERS edge.
    /// </summary>
    private IReadOnlyList<NodeRow> BuildSeedNodes()
    {
        var nodes = new List<NodeRow>
        {
            new(TestBranch, "NamedType:TinyFixture.IGreeter", "IGreeter", SymbolKinds.NamedType, "IGreeter.cs", "TinyFixture", true),
            new(TestBranch, "NamedType:TinyFixture.Greeter", "Greeter", SymbolKinds.NamedType, "Greeter.cs", "TinyFixture", false),
            new(TestBranch, "NamedType:TinyFixture.LoudGreeter", "LoudGreeter", SymbolKinds.NamedType, "LoudGreeter.cs", "TinyFixture", false),
            new(TestBranch, "NamedType:TinyFixture.PartialThing", "PartialThing", SymbolKinds.NamedType, "PartialThing.Part1.cs", "TinyFixture", false),
            new(TestBranch, McpServerStdioTests.IGreeterGreetId, "string IGreeter.Greet(string name)", SymbolKinds.Method, "IGreeter.cs", "TinyFixture", true),
            new(TestBranch, McpServerStdioTests.GreeterGreetId, "string Greeter.Greet(string name)", SymbolKinds.Method, "Greeter.cs", "TinyFixture", false),
            new(TestBranch, McpServerStdioTests.LoudGreeterGreetId, "string LoudGreeter.Greet(string name)", SymbolKinds.Method, "LoudGreeter.cs", "TinyFixture", false),
            new(TestBranch, McpServerStdioTests.CallerCallGreetId, "string Caller.CallGreet()", SymbolKinds.Method, "Caller.cs", "TinyFixture", false),
            new(TestBranch, McpServerStdioTests.DecoratorDecorateId, "string Decorator.Decorate()", SymbolKinds.Method, "Sub/Decorator.cs", "TinyFixture", false),
            new(TestBranch, McpServerStdioTests.AppRunId, "string App.Run()", SymbolKinds.Method, "App.cs", "TinyFixture", false),
            new(TestBranch, "Method:int TinyFixture.PartialThing.PartOneValue()", "int PartialThing.PartOneValue()", SymbolKinds.Method, "PartialThing.Part1.cs", "TinyFixture", false),
            new(TestBranch, "Method:int TinyFixture.PartialThing.PartTwoValue()", "int PartialThing.PartTwoValue()", SymbolKinds.Method, "PartialThing.Part2.cs", "TinyFixture", false),

            // Two Razor components: HomePage is routable (two route templates), ChildWidget
            // is not (routes null) — they exercise the SymbolHit isComponent/routes surfacing
            // and a RENDERS edge (HomePage -> ChildWidget) for get_relationships.
            new(TestBranch, McpServerStdioTests.HomePageId, "HomePage", SymbolKinds.NamedType, "HomePage.razor", "TinyFixture", false, IsComponent: true, Routes: ["/", "/home"]),
            new(TestBranch, McpServerStdioTests.ChildWidgetId, "ChildWidget", SymbolKinds.NamedType, "ChildWidget.razor", "TinyFixture", false, IsComponent: true),
        };

        for (var i = 0; i < FillerCount; i++)
        {
            nodes.Add(new NodeRow(
                TestBranch,
                $"Method:string TinyFixture.Filler{i:D2}.Alpha()",
                $"string Filler{i:D2}.Alpha()",
                SymbolKinds.Method, "Fillers.cs", "TinyFixture", false));
        }

        return nodes;
    }

    /// <summary>
    /// The seeded edges. The four CALLS edges reproduce the TinyFixture call graph the
    /// get_callers tests depend on (App.Run -&gt; Caller.CallGreet -&gt; Greeter.Greet, plus
    /// LoudGreeter/Decorator -&gt; Greeter.Greet). The remaining rows add one of each typed
    /// edge the query-surface tools traverse — IMPLEMENTS, INHERITS, OVERRIDES, CONTAINS and
    /// RENDERS — none of which are CALLS/HTTP_CALLS, so the get_callers assertions are
    /// unaffected. NOTE the OVERRIDES/CONTAINS edges also make Greeter.Greet the single
    /// highest-degree god node once CONTAINS is excluded (degree 4: three CALLS + one
    /// OVERRIDES into it), which graph_stats asserts.
    /// </summary>
    private IReadOnlyList<EdgeRow> BuildSeedEdges() =>
    [
        new(TestBranch, McpServerStdioTests.LoudGreeterGreetId, McpServerStdioTests.GreeterGreetId, EdgeTypes.Calls, "LoudGreeter.cs"),
        new(TestBranch, McpServerStdioTests.CallerCallGreetId, McpServerStdioTests.GreeterGreetId, EdgeTypes.Calls, "Caller.cs"),
        new(TestBranch, McpServerStdioTests.DecoratorDecorateId, McpServerStdioTests.GreeterGreetId, EdgeTypes.Calls, "Sub/Decorator.cs"),
        new(TestBranch, McpServerStdioTests.AppRunId, McpServerStdioTests.CallerCallGreetId, EdgeTypes.Calls, "App.cs"),

        // Typed edges for get_relationships / get_path (all non-CALLS, so get_callers is unaffected).
        new(TestBranch, McpServerStdioTests.GreeterTypeId, McpServerStdioTests.IGreeterTypeId, EdgeTypes.Implements, "Greeter.cs"),
        new(TestBranch, McpServerStdioTests.LoudGreeterTypeId, McpServerStdioTests.GreeterTypeId, EdgeTypes.Inherits, "LoudGreeter.cs"),
        new(TestBranch, McpServerStdioTests.LoudGreeterGreetId, McpServerStdioTests.GreeterGreetId, EdgeTypes.Overrides, "LoudGreeter.cs"),
        new(TestBranch, McpServerStdioTests.GreeterTypeId, McpServerStdioTests.GreeterGreetId, EdgeTypes.Contains, "Greeter.cs"),
        new(TestBranch, McpServerStdioTests.HomePageId, McpServerStdioTests.ChildWidgetId, EdgeTypes.Renders, "HomePage.razor"),
    ];

    /// <summary>Sends a JSON-RPC request and reads stdout frames (recording every one)
    /// until the response with the matching id arrives.</summary>
    public async Task<JsonElement> SendRequestAsync(
        string method, object? parameters = null, int timeoutMs = DefaultTimeoutMs)
    {
        var proc = _proc ?? throw new InvalidOperationException(
            "MCP server is not running — [Neo4jFact] tests should have been skipped.");

        var id = _nextRequestId++;
        var request = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters ?? new { },
        });
        await proc.StandardInput.WriteLineAsync(request);

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            if (proc.HasExited)
            {
                throw new InvalidOperationException(
                    $"MCP server exited (code {proc.ExitCode}) while waiting for response id {id}." +
                    $"{Environment.NewLine}{RedactedStderr()}");
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException($"Timed out waiting for JSON-RPC response id {id} to '{method}'.");
            }

            string? line;
            try
            {
                line = await proc.StandardOutput.ReadLineAsync().WaitAsync(remaining);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"Timed out waiting for JSON-RPC response id {id} to '{method}'.");
            }

            if (line is null)
            {
                throw new InvalidOperationException(
                    $"MCP server stdout closed (EOF) while waiting for response id {id}." +
                    $"{Environment.NewLine}{RedactedStderr()}");
            }

            if (line.Trim().Length == 0)
            {
                continue;
            }

            lock (_gate)
            {
                _stdoutLines.Add(line);
            }

            JsonElement message;
            try
            {
                using var doc = JsonDocument.Parse(line);
                message = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Not JSON at all — recorded above; the stdout-hygiene test will fail on it.
                continue;
            }

            if (message.TryGetProperty("id", out var idProp)
                && idProp.ValueKind == JsonValueKind.Number
                && idProp.GetInt32() == id)
            {
                return message;
            }

            // Server notification or unrelated frame — recorded, keep reading.
        }
    }

    /// <summary>Sends a JSON-RPC notification (no id, no response expected).</summary>
    public async Task SendNotificationAsync(string method)
    {
        var proc = _proc ?? throw new InvalidOperationException("MCP server is not running.");
        var notification = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        });
        await proc.StandardInput.WriteLineAsync(notification);
    }

    /// <summary>Invokes an MCP tool via <c>tools/call</c> and returns the full JSON-RPC
    /// response message (error handling is up to the caller's assertions).</summary>
    public Task<JsonElement> CallToolAsync(string toolName, IDictionary<string, object?> arguments)
        => SendRequestAsync("tools/call", new Dictionary<string, object?>
        {
            ["name"] = toolName,
            ["arguments"] = arguments,
        });

    /// <summary>
    /// Resolves the built server executable next to this repo's other build outputs,
    /// mirroring the test assembly's own configuration (Debug/Release), falling back to
    /// whichever configuration exists.
    /// </summary>
    private static string LocateServerExecutable(out string repoRoot)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? configuration = null;
        DirectoryInfo? root = null;

        for (var d = dir; d is not null; d = d.Parent)
        {
            // ...\EdgeHop.Tests\bin\{Configuration}\net10.0\ — remember the segment
            // directly under 'bin' as the active configuration.
            if (configuration is null
                && d.Parent is { } parent
                && string.Equals(parent.Name, "bin", StringComparison.OrdinalIgnoreCase))
            {
                configuration = d.Name;
            }

            if (File.Exists(Path.Combine(d.FullName, "EdgeHop.sln")))
            {
                root = d;
                break;
            }
        }

        var rootPath = (root ?? throw new InvalidOperationException(
            $"Could not locate EdgeHop.sln above '{AppContext.BaseDirectory}'.")).FullName;
        repoRoot = rootPath;

        var candidates = new[] { configuration ?? "Debug", "Debug", "Release" }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(c => Path.Combine(rootPath, "EdgeHop.Mcp", "bin", c, "net10.0", "EdgeHop.Mcp.exe"))
            .ToList();

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException(
                "EdgeHop.Mcp.exe not found — build the solution first (dotnet build EdgeHop.sln). " +
                $"Looked in: {string.Join("; ", candidates)}");
    }

    /// <summary>Server stderr for failure diagnostics, with the Neo4j password value
    /// defensively redacted (it should never appear there, but nothing sensitive may leak
    /// into a test failure message either).</summary>
    private string RedactedStderr()
    {
        if (_stderrTask is null || !_stderrTask.Wait(TimeSpan.FromSeconds(2)))
        {
            return "server stderr: <unavailable>";
        }

        var text = _stderrTask.Result;
        var password = Environment.GetEnvironmentVariable("NEO4J_PASSWORD");
        if (!string.IsNullOrEmpty(password))
        {
            text = text.Replace(password, "***REDACTED***");
        }

        return $"server stderr (redacted): {text}";
    }
}

/// <summary>
/// Phase 3 checkpoints 6–7 (and the Handoff 5 query-surface expansion) as automated
/// regression tests AT THE TOOL LAYER: the real <c>EdgeHop.Mcp</c> server process is
/// driven over stdio JSON-RPC and all five tools are validated against a TinyFixture-shaped
/// graph the fixture seeds under a unique GUID branch, injected into the server via
/// <c>EDGEHOP_BRANCH</c>. This is the automated anchor for regressions the Core-level
/// reader tests cannot see: the branch-injection seam, tools/list registration, find_symbol
/// argument wiring and the truncated flag (plus isComponent/routes surfacing),
/// get_callers/get_relationships depth defaults and McpException validation paths,
/// get_path shortest-path shape, graph_stats totals/god-node ordering, and the
/// stdout-is-JSON-RPC-only invariant. Node/edge shapes mirror
/// <c>fixtures/TinyFixture/EXPECTED-GRAPH.md</c>; no test depends on data under any
/// other branch. Skipped automatically when NEO4J_* environment variables are not set.
/// </summary>
public sealed class McpServerStdioTests : IClassFixture<McpServerFixture>
{
    // Stable ids (EXPECTED-GRAPH.md identity column + kind prefix), seeded by the fixture
    // under its GUID branch.
    internal const string IGreeterGreetId = "Method:string TinyFixture.IGreeter.Greet(string)";
    internal const string GreeterGreetId = "Method:string TinyFixture.Greeter.Greet(string)";
    internal const string LoudGreeterGreetId = "Method:string TinyFixture.LoudGreeter.Greet(string)";
    internal const string CallerCallGreetId = "Method:string TinyFixture.Caller.CallGreet()";
    internal const string DecoratorDecorateId = "Method:string TinyFixture.Decorator.Decorate()";
    internal const string AppRunId = "Method:string TinyFixture.App.Run()";

    // Type-level ids (kind prefix + declaring type), reached by the typed-edge tools.
    internal const string GreeterTypeId = "NamedType:TinyFixture.Greeter";
    internal const string IGreeterTypeId = "NamedType:TinyFixture.IGreeter";
    internal const string LoudGreeterTypeId = "NamedType:TinyFixture.LoudGreeter";

    // Razor component ids seeded so a find_symbol hit can surface isComponent/routes and a
    // RENDERS edge exists for get_relationships. HomePage is routable; ChildWidget is not.
    internal const string HomePageId = "NamedType:TinyFixture.HomePage";
    internal const string ChildWidgetId = "NamedType:TinyFixture.ChildWidget";

    private const int FindSymbolLimit = 25;

    private readonly McpServerFixture _fx;

    public McpServerStdioTests(McpServerFixture fx) => _fx = fx;

    // ----------------------------------------------------------------- tools/list --

    [Neo4jFact]
    public async Task ToolsList_ExposesExactlyTheFiveQueryTools()
    {
        var response = await _fx.SendRequestAsync("tools/list", new { });

        var names = response.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        // Handoff 5 contract: EXACTLY these five tools, nothing else (Phase 3's two-tool
        // rule is superseded by owner direction). Ordinal-sorted for a stable comparison.
        Assert.Equal(
            new[] { "find_symbol", "get_callers", "get_path", "get_relationships", "graph_stats" },
            names);
    }

    [Neo4jFact]
    public async Task ToolsList_FindSymbolDescription_DocumentsNoWildcardSubstringSemantics()
    {
        var response = await _fx.SendRequestAsync("tools/list", new { });

        var findSymbol = response.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "find_symbol");

        // Claude reads this description to decide HOW to call the tool: the
        // case-insensitive-substring / no-wildcards semantics must reach the client and
        // must not silently regress out of the description text.
        var description = findSymbol.GetProperty("description").GetString();
        Assert.NotNull(description);
        Assert.Contains("substring", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wildcard", description, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------- find_symbol (checkpoint 6) --

    [Neo4jFact]
    public async Task FindSymbol_Greet_ReturnsTheFixtureGreetMethods_WithStableIds()
    {
        var payload = GetToolPayload(await _fx.CallToolAsync(
            "find_symbol", new Dictionary<string, object?> { ["query"] = "Greet" }));

        var hits = payload.GetProperty("hits").EnumerateArray().ToList();
        var ids = hits.Select(h => h.GetProperty("id").GetString()).ToList();

        // README checkpoint 6: the three Greet methods, distinct ids, names all matching.
        Assert.Contains(IGreeterGreetId, ids);
        Assert.Contains(GreeterGreetId, ids);
        Assert.Contains(LoudGreeterGreetId, ids);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(hits, h => Assert.Contains(
            "greet", h.GetProperty("name").GetString(), StringComparison.OrdinalIgnoreCase));

        // Wire shape: id/name/kind/sourceDoc round-trip for a known node (proves the
        // reader→tool argument wiring: a swapped query/kind could not produce this).
        var greeterGreet = Assert.Single(hits, h => h.GetProperty("id").GetString() == GreeterGreetId);
        Assert.Equal("string Greeter.Greet(string name)", greeterGreet.GetProperty("name").GetString());
        Assert.Equal(SymbolKinds.Method, greeterGreet.GetProperty("kind").GetString());
        Assert.Equal("Greeter.cs", greeterGreet.GetProperty("sourceDoc").GetString());
    }

    [Neo4jFact]
    public async Task FindSymbol_KindFilter_ReturnsOnlyNamedTypes()
    {
        // If the query and kind arguments were ever swapped in the FindSymbolsAsync call,
        // this returns nothing (no name contains 'NamedType'; no kind equals 'greeter').
        var payload = GetToolPayload(await _fx.CallToolAsync("find_symbol",
            new Dictionary<string, object?> { ["query"] = "greeter", ["kind"] = SymbolKinds.NamedType }));

        var hits = payload.GetProperty("hits").EnumerateArray().ToList();
        var ids = hits.Select(h => h.GetProperty("id").GetString()).ToList();

        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal(SymbolKinds.NamedType, h.GetProperty("kind").GetString()));
        Assert.Contains("NamedType:TinyFixture.IGreeter", ids);
        Assert.Contains("NamedType:TinyFixture.Greeter", ids);
        Assert.Contains("NamedType:TinyFixture.LoudGreeter", ids);
    }

    [Neo4jFact]
    public async Task FindSymbol_NarrowQuery_IsNotTruncated_AndProvesInjectedBranchIsUsed()
    {
        // PartialThing rows exist ONLY under the fixture's GUID branch (type + its two
        // methods): exactly three hits proves the server resolved EDGEHOP_BRANCH —
        // a server still pinning 'main' (or ignoring the env var) returns nothing here.
        var payload = GetToolPayload(await _fx.CallToolAsync(
            "find_symbol", new Dictionary<string, object?> { ["query"] = "PartialThing" }));

        var hits = payload.GetProperty("hits").EnumerateArray().ToList();
        Assert.Equal(3, hits.Count);
        Assert.Contains("NamedType:TinyFixture.PartialThing",
            hits.Select(h => h.GetProperty("id").GetString()));
        Assert.False(payload.GetProperty("truncated").GetBoolean());
    }

    [Neo4jFact]
    public async Task FindSymbol_BroadQuery_TruncatesAtTheLimit_AndSetsTheFlag()
    {
        // The fixture seeds 30 filler methods (names contain 'a') beside the fixture
        // rows, so a single-letter query matches more than 25 names on the injected
        // branch alone: the result must be cut at exactly 25 with truncated=true (the
        // flag means 'really cut off', not 'landed on the limit').
        var payload = GetToolPayload(await _fx.CallToolAsync(
            "find_symbol", new Dictionary<string, object?> { ["query"] = "a" }));

        Assert.Equal(FindSymbolLimit, payload.GetProperty("hits").GetArrayLength());
        Assert.True(payload.GetProperty("truncated").GetBoolean());
    }

    // ---------------------------------------------------- get_callers (checkpoint 7) --

    [Neo4jFact]
    public async Task GetCallers_Depth1_ReturnsExactlyTheThreeDirectCallers()
    {
        var payload = GetToolPayload(await _fx.CallToolAsync("get_callers",
            new Dictionary<string, object?> { ["symbolId"] = GreeterGreetId, ["depth"] = 1 }));

        Assert.Equal(GreeterGreetId, payload.GetProperty("targetId").GetString());
        Assert.Equal(1, payload.GetProperty("depth").GetInt32());

        var ids = payload.GetProperty("callers").EnumerateArray()
            .Select(c => c.GetProperty("id").GetString())
            .ToHashSet(StringComparer.Ordinal);

        // README checkpoint 7 / EXPECTED-GRAPH.md: exactly the three direct callers.
        Assert.Equal(3, ids.Count);
        Assert.Contains(LoudGreeterGreetId, ids);
        Assert.Contains(CallerCallGreetId, ids);
        Assert.Contains(DecoratorDecorateId, ids);
        Assert.DoesNotContain(AppRunId, ids);
        Assert.DoesNotContain(GreeterGreetId, ids);
    }

    [Neo4jFact]
    public async Task GetCallers_Depth2_AddsExactlyTheTransitiveCaller()
    {
        var payload = GetToolPayload(await _fx.CallToolAsync("get_callers",
            new Dictionary<string, object?> { ["symbolId"] = GreeterGreetId, ["depth"] = 2 }));

        Assert.Equal(2, payload.GetProperty("depth").GetInt32());

        var ids = payload.GetProperty("callers").EnumerateArray()
            .Select(c => c.GetProperty("id").GetString())
            .ToHashSet(StringComparer.Ordinal);

        // README checkpoint 7: the depth-1 three plus App.Run via App.Run -> CallGreet.
        Assert.Equal(4, ids.Count);
        Assert.Contains(LoudGreeterGreetId, ids);
        Assert.Contains(CallerCallGreetId, ids);
        Assert.Contains(DecoratorDecorateId, ids);
        Assert.Contains(AppRunId, ids);
        Assert.DoesNotContain(GreeterGreetId, ids);
    }

    [Neo4jFact]
    public async Task GetCallers_OmittedDepth_DefaultsToOne()
    {
        // Pins default-parameter binding through the MCP layer, not just in the reader.
        var payload = GetToolPayload(await _fx.CallToolAsync("get_callers",
            new Dictionary<string, object?> { ["symbolId"] = GreeterGreetId }));

        Assert.Equal(1, payload.GetProperty("depth").GetInt32());
        Assert.Equal(3, payload.GetProperty("callers").GetArrayLength());
    }

    // ------------------------------------------------ isComponent / routes (Handoff 5) --

    [Neo4jFact]
    public async Task FindSymbol_ComponentNode_SurfacesIsComponentAndRoutes()
    {
        // The routable component: isComponent true and both route templates round-trip in
        // declaration order. A store indexed before Handoff 5 would show isComponent=false
        // and omit routes, so this pins the surfacing end-to-end through the tool layer.
        var homePayload = GetToolPayload(await _fx.CallToolAsync(
            "find_symbol", new Dictionary<string, object?> { ["query"] = "HomePage" }));
        var home = Assert.Single(
            homePayload.GetProperty("hits").EnumerateArray().ToList(),
            h => h.GetProperty("id").GetString() == HomePageId);
        Assert.True(home.GetProperty("isComponent").GetBoolean());
        Assert.Equal(
            new[] { "/", "/home" },
            home.GetProperty("routes").EnumerateArray().Select(r => r.GetString()!).ToArray());

        // The non-routable component: isComponent true, but routes is null so it is OMITTED
        // from the wire shape (WhenWritingNull), not emitted as an empty array.
        var childPayload = GetToolPayload(await _fx.CallToolAsync(
            "find_symbol", new Dictionary<string, object?> { ["query"] = "ChildWidget" }));
        var child = Assert.Single(childPayload.GetProperty("hits").EnumerateArray().ToList());
        Assert.True(child.GetProperty("isComponent").GetBoolean());
        Assert.True(
            !child.TryGetProperty("routes", out var childRoutes)
            || childRoutes.ValueKind == JsonValueKind.Null);

        // A plain method: isComponent false and routes omitted — the bool discriminates.
        var methodPayload = GetToolPayload(await _fx.CallToolAsync(
            "find_symbol", new Dictionary<string, object?> { ["query"] = "Decorate" }));
        var method = Assert.Single(
            methodPayload.GetProperty("hits").EnumerateArray().ToList(),
            h => h.GetProperty("id").GetString() == DecoratorDecorateId);
        Assert.False(method.GetProperty("isComponent").GetBoolean());
        Assert.True(
            !method.TryGetProperty("routes", out var methodRoutes)
            || methodRoutes.ValueKind == JsonValueKind.Null);
    }

    // --------------------------------------------------------------- get_relationships --

    [Neo4jFact]
    public async Task GetRelationships_OutDirection_ReturnsTypedNeighbors_WithEdgeAndDirection()
    {
        // Greeter (the type) has two outgoing typed edges: IMPLEMENTS -> IGreeter and
        // CONTAINS -> Greeter.Greet. Each hit carries the edge type that reached it and the
        // traversed direction ('out'); the anchor itself is never a hit.
        var payload = GetToolPayload(await _fx.CallToolAsync("get_relationships",
            new Dictionary<string, object?> { ["symbolId"] = GreeterTypeId }));

        Assert.Equal(GreeterTypeId, payload.GetProperty("targetId").GetString());
        Assert.Equal("out", payload.GetProperty("direction").GetString());
        Assert.Equal(1, payload.GetProperty("depth").GetInt32());
        Assert.False(payload.GetProperty("truncated").GetBoolean());
        // No edge-type filter was passed, so 'edgeType' is null and omitted from the wire.
        Assert.True(
            !payload.TryGetProperty("edgeType", out var edgeTypeProp)
            || edgeTypeProp.ValueKind == JsonValueKind.Null);

        var byId = RelationshipsById(payload);
        Assert.Equal(2, byId.Count);
        Assert.Equal((EdgeTypes.Implements, "out"), byId[IGreeterTypeId]);
        Assert.Equal((EdgeTypes.Contains, "out"), byId[GreeterGreetId]);
        Assert.DoesNotContain(GreeterTypeId, byId.Keys);
    }

    [Neo4jFact]
    public async Task GetRelationships_InDirection_ReturnsIncomingEdge()
    {
        // Only LoudGreeter -> Greeter (INHERITS) points INTO the Greeter type.
        var payload = GetToolPayload(await _fx.CallToolAsync("get_relationships",
            new Dictionary<string, object?> { ["symbolId"] = GreeterTypeId, ["direction"] = "in" }));

        Assert.Equal("in", payload.GetProperty("direction").GetString());
        var byId = RelationshipsById(payload);
        Assert.Equal((EdgeTypes.Inherits, "in"), Assert.Contains(LoudGreeterTypeId, byId));
        Assert.Single(byId);
    }

    [Neo4jFact]
    public async Task GetRelationships_EdgeTypeFilter_KeepsOnlyThatType()
    {
        // The same 'out' walk restricted to IMPLEMENTS drops the CONTAINS neighbor.
        var payload = GetToolPayload(await _fx.CallToolAsync("get_relationships",
            new Dictionary<string, object?>
            {
                ["symbolId"] = GreeterTypeId,
                ["direction"] = "out",
                ["edgeType"] = EdgeTypes.Implements,
            }));

        Assert.Equal(EdgeTypes.Implements, payload.GetProperty("edgeType").GetString());
        var byId = RelationshipsById(payload);
        Assert.Equal((EdgeTypes.Implements, "out"), Assert.Contains(IGreeterTypeId, byId));
        Assert.Single(byId);
    }

    [Neo4jFact]
    public async Task GetRelationships_Depth2_SingleEdgeType_FollowsTheChainTransitively()
    {
        // App.Run --CALLS--> Caller.CallGreet --CALLS--> Greeter.Greet: a depth-2 CALLS walk
        // from App.Run reaches both. depth>1 is only allowed with a single explicit edgeType.
        var payload = GetToolPayload(await _fx.CallToolAsync("get_relationships",
            new Dictionary<string, object?>
            {
                ["symbolId"] = AppRunId,
                ["direction"] = "out",
                ["edgeType"] = EdgeTypes.Calls,
                ["depth"] = 2,
            }));

        Assert.Equal(2, payload.GetProperty("depth").GetInt32());
        var byId = RelationshipsById(payload);
        Assert.Equal(2, byId.Count);
        Assert.Contains(CallerCallGreetId, byId.Keys);
        Assert.Contains(GreeterGreetId, byId.Keys);
        Assert.DoesNotContain(AppRunId, byId.Keys);
        Assert.All(byId.Values, v => Assert.Equal(EdgeTypes.Calls, v.EdgeType));
    }

    [Neo4jFact]
    public async Task GetRelationships_DepthAboveOneWithoutEdgeType_SurfacesActionableError_AndServerSurvives()
    {
        var response = await _fx.CallToolAsync("get_relationships",
            new Dictionary<string, object?> { ["symbolId"] = GreeterTypeId, ["depth"] = 2 });

        var errorText = GetToolErrorText(response);
        Assert.NotNull(errorText);
        // Pin the exact human-authored sentence AND that CleanMessage stripped the BCL
        // "(Parameter 'edgeType')" decoration before the McpException reached the wire.
        Assert.Contains(
            "depth > 1 requires a single --edge-type; multi-hop mixed-type traversal is not supported.",
            errorText);
        Assert.DoesNotContain("(Parameter", errorText);

        var listResponse = await _fx.SendRequestAsync("tools/list", new { });
        Assert.Equal(5, listResponse.GetProperty("result").GetProperty("tools").GetArrayLength());
    }

    // ------------------------------------------------------------------------ get_path --

    [Neo4jFact]
    public async Task GetPath_ReturnsTheShortestDirectedPath_WithTypedEdges()
    {
        // App.Run --CALLS--> Caller.CallGreet --CALLS--> Greeter.Greet is the only directed
        // route; get_path returns the three ordered nodes with the edge that reached each.
        var payload = GetToolPayload(await _fx.CallToolAsync("get_path",
            new Dictionary<string, object?> { ["fromId"] = AppRunId, ["toId"] = GreeterGreetId }));

        Assert.Equal(AppRunId, payload.GetProperty("fromId").GetString());
        Assert.Equal(GreeterGreetId, payload.GetProperty("toId").GetString());
        Assert.True(payload.GetProperty("found").GetBoolean());

        var nodes = payload.GetProperty("nodes").EnumerateArray().ToList();
        Assert.Equal(3, nodes.Count);
        Assert.Equal(AppRunId, NodeId(nodes[0]));
        Assert.Equal(CallerCallGreetId, NodeId(nodes[1]));
        Assert.Equal(GreeterGreetId, NodeId(nodes[2]));

        // First node has no incoming edge; the rest carry the CALLS edge from the previous.
        Assert.True(
            !nodes[0].TryGetProperty("edgeTypeFromPrev", out var first)
            || first.ValueKind == JsonValueKind.Null);
        Assert.Equal(EdgeTypes.Calls, nodes[1].GetProperty("edgeTypeFromPrev").GetString());
        Assert.Equal(EdgeTypes.Calls, nodes[2].GetProperty("edgeTypeFromPrev").GetString());
    }

    [Neo4jFact]
    public async Task GetPath_SameNode_IsAFoundZeroLengthPath()
    {
        var payload = GetToolPayload(await _fx.CallToolAsync("get_path",
            new Dictionary<string, object?> { ["fromId"] = GreeterGreetId, ["toId"] = GreeterGreetId }));

        Assert.True(payload.GetProperty("found").GetBoolean());
        var node = Assert.Single(payload.GetProperty("nodes").EnumerateArray().ToList());
        Assert.Equal(GreeterGreetId, NodeId(node));
    }

    [Neo4jFact]
    public async Task GetPath_Unreachable_ReturnsNotFoundWithNoNodes()
    {
        // Greeter.Greet has no outgoing edges, so App.Run is unreachable from it.
        var payload = GetToolPayload(await _fx.CallToolAsync("get_path",
            new Dictionary<string, object?> { ["fromId"] = GreeterGreetId, ["toId"] = AppRunId }));

        Assert.False(payload.GetProperty("found").GetBoolean());
        Assert.Empty(payload.GetProperty("nodes").EnumerateArray());
    }

    [Neo4jFact]
    public async Task GetPath_MaxLengthOutOfRange_SurfacesActionableError()
    {
        var response = await _fx.CallToolAsync("get_path",
            new Dictionary<string, object?>
            {
                ["fromId"] = AppRunId,
                ["toId"] = GreeterGreetId,
                ["maxLength"] = 99,
            });

        var errorText = GetToolErrorText(response);
        Assert.NotNull(errorText);
        Assert.Contains("maxLength must be between 1 and 15; got 99.", errorText);
        Assert.DoesNotContain("(Parameter", errorText);
    }

    // --------------------------------------------------------------------- graph_stats --

    [Neo4jFact]
    public async Task GraphStats_ReportsBranchTotals_AndGodNodesExcludeContains()
    {
        var payload = GetToolPayload(await _fx.CallToolAsync(
            "graph_stats", new Dictionary<string, object?> { ["topN"] = 5 }));

        // Per-branch totals: everything the fixture seeded under this GUID branch and nothing
        // else (12 named symbols + 30 fillers + 2 components = 44 nodes; 9 edges).
        Assert.Equal(_fx.TestBranch, payload.GetProperty("branch").GetString());
        Assert.Equal(44, payload.GetProperty("totalNodes").GetInt64());
        Assert.Equal(9, payload.GetProperty("totalEdges").GetInt64());

        var nodesByKind = payload.GetProperty("nodesByKind").EnumerateArray()
            .ToDictionary(k => k.GetProperty("kind").GetString()!, k => k.GetProperty("count").GetInt64());
        Assert.Equal(6, nodesByKind[SymbolKinds.NamedType]); // 4 tiny types + 2 components
        Assert.Equal(38, nodesByKind[SymbolKinds.Method]);   // 8 tiny methods + 30 fillers

        var edgesByType = payload.GetProperty("edgesByType").EnumerateArray()
            .ToDictionary(e => e.GetProperty("type").GetString()!, e => e.GetProperty("count").GetInt64());
        Assert.Equal(4, edgesByType[EdgeTypes.Calls]);
        Assert.Equal(1, edgesByType[EdgeTypes.Contains]); // counted in edge totals ...

        // ... but EXCLUDED from god-node degree: Greeter.Greet is the single highest-degree
        // node at 4 (three CALLS + one OVERRIDES into it); the CONTAINS edge into it does not
        // count. A degree of 5 here would prove CONTAINS leaked into the ranking.
        var godNodes = payload.GetProperty("godNodes").EnumerateArray().ToList();
        Assert.NotEmpty(godNodes);
        Assert.Equal(GreeterGreetId, NodeId(godNodes[0]));
        Assert.Equal(4, godNodes[0].GetProperty("degree").GetInt64());
    }

    // --------------------------------------------------------------- validation paths --

    [Neo4jFact]
    public async Task GetCallers_DepthOutOfRange_SurfacesActionableError_AndServerSurvives()
    {
        foreach (var badDepth in new[] { 0, 99 })
        {
            var response = await _fx.CallToolAsync("get_callers",
                new Dictionary<string, object?> { ["symbolId"] = GreeterGreetId, ["depth"] = badDepth });

            var errorText = GetToolErrorText(response);
            Assert.NotNull(errorText);
            // Pin the exact human-authored sentence AND that CleanMessage stripped the
            // BCL decoration — neither "(Parameter 'depth')" nor the "Actual value was N."
            // second line may reach the wire.
            Assert.Contains($"depth must be between 1 and 10; got {badDepth}.", errorText);
            Assert.DoesNotContain("(Parameter", errorText);
            Assert.DoesNotContain("Actual value", errorText);
        }

        // The server must stay responsive after rejected calls.
        var listResponse = await _fx.SendRequestAsync("tools/list", new { });
        Assert.Equal(5, listResponse.GetProperty("result").GetProperty("tools").GetArrayLength());
    }

    [Neo4jFact]
    public async Task GetCallers_WhitespaceSymbolId_SurfacesActionableError()
    {
        var response = await _fx.CallToolAsync("get_callers",
            new Dictionary<string, object?> { ["symbolId"] = "   ", ["depth"] = 1 });

        var errorText = GetToolErrorText(response);
        Assert.NotNull(errorText);
        Assert.Contains("symbolId", errorText, StringComparison.OrdinalIgnoreCase);
        // CleanMessage must have stripped the "(Parameter 'symbolId')" decoration.
        Assert.DoesNotContain("(Parameter", errorText);
    }

    // ----------------------------------------------------------------- stdio hygiene --

    [Neo4jFact]
    public async Task Stdout_CarriesOnlyJsonRpcFrames()
    {
        // Provoke at least one full round-trip, then check EVERY stdout line seen so far
        // is a JSON-RPC 2.0 frame — a single stray log/diagnostic line on stdout corrupts
        // the protocol stream (Program.cs routes all logging to stderr).
        await _fx.SendRequestAsync("tools/list", new { });

        var lines = _fx.StdoutLinesSnapshot();
        Assert.NotEmpty(lines);
        Assert.All(lines, line =>
        {
            using var doc = JsonDocument.Parse(line);
            Assert.Equal("2.0", doc.RootElement.GetProperty("jsonrpc").GetString());
        });
    }

    // ---------------------------------------------------------------------- helpers --

    /// <summary>Projects a <c>get_relationships</c> payload into a map from neighbor id to
    /// the <c>(edgeType, direction)</c> pair the hit carried, for order-independent
    /// assertions.</summary>
    private static Dictionary<string, (string EdgeType, string Direction)> RelationshipsById(
        JsonElement payload) =>
        payload.GetProperty("hits").EnumerateArray().ToDictionary(
            h => h.GetProperty("symbol").GetProperty("id").GetString()!,
            h => (h.GetProperty("edgeType").GetString()!, h.GetProperty("direction").GetString()!),
            StringComparer.Ordinal);

    /// <summary>The stable id of the symbol carried by a node/god-node wrapper element
    /// (a <c>{ symbol, ... }</c> shape).</summary>
    private static string? NodeId(JsonElement wrapper) =>
        wrapper.GetProperty("symbol").GetProperty("id").GetString();

    /// <summary>Extracts the tool result payload, preferring <c>structuredContent</c> and
    /// falling back to the JSON in the first text content item. Fails the test if the
    /// call errored.</summary>
    private static JsonElement GetToolPayload(JsonElement response)
    {
        var errorText = GetToolErrorText(response);
        Assert.True(errorText is null, $"Tool call failed: {errorText}");

        var result = response.GetProperty("result");
        if (result.TryGetProperty("structuredContent", out var structured)
            && structured.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            return structured;
        }

        foreach (var item in result.GetProperty("content").EnumerateArray())
        {
            if (item.GetProperty("type").GetString() == "text")
            {
                using var doc = JsonDocument.Parse(item.GetProperty("text").GetString()!);
                return doc.RootElement.Clone();
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"Tool response contained neither structuredContent nor text content: {result.GetRawText()}");
    }

    /// <summary>Returns the human-readable error of a failed tool call — whether surfaced
    /// as a JSON-RPC error object or a CallToolResult with <c>isError = true</c> — or null
    /// when the call succeeded.</summary>
    private static string? GetToolErrorText(JsonElement response)
    {
        if (response.TryGetProperty("error", out var error))
        {
            return $"jsonrpc-error: {error.GetProperty("message").GetString()}";
        }

        var result = response.GetProperty("result");
        if (result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
        {
            var texts = result.GetProperty("content").EnumerateArray()
                .Where(i => i.GetProperty("type").GetString() == "text")
                .Select(i => i.GetProperty("text").GetString());
            return $"tool-isError: {string.Join(" | ", texts)}";
        }

        return null;
    }
}
