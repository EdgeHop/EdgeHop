using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EdgeHop.Core;
using Neo4j.Driver;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// Class fixture for <see cref="CliTests"/>: seeds a throwaway GUID branch with the
/// TinyFixture CALLS topology (same shape as <see cref="ReaderGraphFixture"/>) and locates
/// the built <c>edgehop</c> CLI executable. Each test launches the REAL exe — argument
/// parsing, env-var wiring, exit codes and stdout/stderr discipline are all on the tested
/// path, mirroring how <see cref="McpServerFixture"/> exercises the real MCP server.
/// The test project deliberately has NO project reference to <c>EdgeHop.Cli</c>
/// (csproj files are frozen); the process boundary is the tested surface.
/// Deletes ONLY the GUID branch afterwards; nothing here can touch branch 'main'.
/// No-ops when Neo4j is not configured (all tests are <see cref="Neo4jFactAttribute"/>).
/// <para>
/// BRANCH-ISOLATION LENS (mirrors <see cref="ReaderGraphFixture"/>'s id design): branch
/// 'main' holds the REAL TinyFixture graph with the UNSUFFIXED versions of these ids and
/// the identical CALLS topology. Every id seeded here therefore carries this run's GUID
/// suffix (<see cref="IdSuffix"/>) so no assertion can be satisfied by branch 'main': a
/// CLI head that ignored <c>--branch</c> and queried 'main' would return the unsuffixed
/// ids (get-callers) or main's much larger 'greet' match set (find-symbol) and fail.
/// </para>
/// </summary>
public sealed class CliProcessFixture : IAsyncLifetime
{
    // Ids as they exist on branch 'main' (EXPECTED-GRAPH.md shape). Seeded ONLY with the
    // per-run GUID suffix appended — see the branch-isolation lens in the class doc.
    private const string MainGreeterTypeId = "NamedType:TinyFixture.Greeter";
    private const string MainGreeterGreetId = "Method:string TinyFixture.Greeter.Greet(string)";
    private const string MainLoudGreeterGreetId = "Method:string TinyFixture.LoudGreeter.Greet(string)";
    private const string MainCallerCallGreetId = "Method:string TinyFixture.Caller.CallGreet()";
    private const string MainDecoratorDecorateId = "Method:string TinyFixture.Decorator.Decorate()";
    private const string MainAppRunId = "Method:string TinyFixture.App.Run()";

    // Extra topology for get-relationships / get-path / stats. Deliberately named so no name
    // contains "greet" (case-insensitive): the find-symbol 'greet' assertions above pin a
    // 4-match set, and these nodes must not perturb it.
    private const string MainSalutationTypeId = "NamedType:TinyFixture.ISalutation";
    private const string MainHomeComponentId = "NamedType:TinyFixture.Home";
    private const string MainChildComponentId = "NamedType:TinyFixture.Child";

    /// <summary>Suffix baked into every seeded id so test-branch rows can never be
    /// id-identical to branch 'main' — an ignored <c>--branch</c> becomes observable.</summary>
    public string IdSuffix => $"|{Branch}";

    public string GreeterTypeId => MainGreeterTypeId + IdSuffix;
    public string GreeterGreetId => MainGreeterGreetId + IdSuffix;
    public string LoudGreeterGreetId => MainLoudGreeterGreetId + IdSuffix;
    public string CallerCallGreetId => MainCallerCallGreetId + IdSuffix;
    public string DecoratorDecorateId => MainDecoratorDecorateId + IdSuffix;
    public string AppRunId => MainAppRunId + IdSuffix;

    public string SalutationTypeId => MainSalutationTypeId + IdSuffix;
    public string HomeComponentId => MainHomeComponentId + IdSuffix;
    public string ChildComponentId => MainChildComponentId + IdSuffix;

    /// <summary>Route templates seeded on <see cref="HomeComponentId"/> (a routable Razor
    /// component) — pins the --json routes-array parity with the MCP wire, and the
    /// null-routes omission on the plain and route-less-component symbols.</summary>
    public IReadOnlyList<string> HomeRoutes { get; } = new[] { "/", "/home" };

    /// <summary>A node whose <c>sourceDoc</c> is NULL (like every Namespace node the
    /// extractor emits) — pins the --json null-omission parity with the MCP wire.</summary>
    public string NullDocNamespaceId => "Namespace:TinyFixture" + IdSuffix;

    private IDriver? _driver;
    private string? _exePath;

    public string Branch { get; } = $"test-cli-{Guid.NewGuid():N}";

    public string Database { get; private set; } = "neo4j";

    public async Task InitializeAsync()
    {
        if (!Neo4jSettings.IsConfigured)
        {
            return;
        }

        _exePath = LocateCliExecutable();

        var settings = Neo4jSettings.FromEnvironment();
        Database = settings.Database;
        _driver = GraphDatabase.Driver(settings.Uri, AuthTokens.Basic(settings.User, settings.Password));

        await Neo4jSchema.ApplyAsync(_driver, Database);

        var writer = new Neo4jGraphWriter(_driver, Database);
        await writer.UpsertNodesAsync(new[]
        {
            new NodeRow(Branch, GreeterTypeId, "Greeter", SymbolKinds.NamedType,
                "Greeter.cs", "TinyFixture", IsAbstract: false),
            new NodeRow(Branch, GreeterGreetId, "string Greeter.Greet(string name)", SymbolKinds.Method,
                "Greeter.cs", "TinyFixture", IsAbstract: false),
            new NodeRow(Branch, LoudGreeterGreetId, "string LoudGreeter.Greet(string name)", SymbolKinds.Method,
                "LoudGreeter.cs", "TinyFixture", IsAbstract: false),
            new NodeRow(Branch, CallerCallGreetId, "string Caller.CallGreet()", SymbolKinds.Method,
                "Caller.cs", "TinyFixture", IsAbstract: false),
            new NodeRow(Branch, DecoratorDecorateId, "string Decorator.Decorate()", SymbolKinds.Method,
                "Sub/Decorator.cs", "TinyFixture", IsAbstract: false),
            new NodeRow(Branch, AppRunId, "string App.Run()", SymbolKinds.Method,
                "App.cs", "TinyFixture", IsAbstract: false),
            // sourceDoc NULL on purpose (extractor behavior for Namespace nodes): the
            // --json output must OMIT the property, exactly like the MCP serializer.
            new NodeRow(Branch, NullDocNamespaceId, "TinyFixture", SymbolKinds.Namespace,
                SourceDoc: null, "TinyFixture", IsAbstract: false),
            // The interface Greeter implements — exercises IMPLEMENTS in get-relationships.
            new NodeRow(Branch, SalutationTypeId, "ISalutation", SymbolKinds.NamedType,
                "ISalutation.cs", "TinyFixture", IsAbstract: true),
            // A routable Razor component: isComponent + routes must round-trip through --json
            // exactly like the MCP wire (routes present as an array).
            new NodeRow(Branch, HomeComponentId, "Home", SymbolKinds.NamedType,
                "Home.razor", "TinyFixture", IsAbstract: false, IsComponent: true, Routes: HomeRoutes),
            // A component with NO routes: isComponent true, routes omitted from --json.
            new NodeRow(Branch, ChildComponentId, "Child", SymbolKinds.NamedType,
                "Child.razor", "TinyFixture", IsAbstract: false, IsComponent: true),
        });
        await writer.UpsertEdgesAsync(new[]
        {
            new EdgeRow(Branch, LoudGreeterGreetId, GreeterGreetId, EdgeTypes.Calls, "LoudGreeter.cs"),
            new EdgeRow(Branch, CallerCallGreetId, GreeterGreetId, EdgeTypes.Calls, "Caller.cs"),
            new EdgeRow(Branch, DecoratorDecorateId, GreeterGreetId, EdgeTypes.Calls, "Sub/Decorator.cs"),
            new EdgeRow(Branch, AppRunId, CallerCallGreetId, EdgeTypes.Calls, "App.cs"),
            // Non-CALLS edge types so get-relationships / stats see the full palette. The
            // CONTAINS edge is here to prove stats' god-node degree EXCLUDES it (Greeter.Greet
            // stays at degree 4, not 5). get-callers still sees exactly its CALLS callers —
            // these edge types are invisible to it.
            new EdgeRow(Branch, GreeterTypeId, SalutationTypeId, EdgeTypes.Implements, "Greeter.cs"),
            new EdgeRow(Branch, GreeterTypeId, GreeterGreetId, EdgeTypes.Contains, "Greeter.cs"),
            new EdgeRow(Branch, LoudGreeterGreetId, GreeterGreetId, EdgeTypes.Overrides, "LoudGreeter.cs"),
            new EdgeRow(Branch, HomeComponentId, ChildComponentId, EdgeTypes.Renders, "Home.razor"),
        });
    }

    public async Task DisposeAsync()
    {
        if (_driver is null)
        {
            return;
        }

        try
        {
            var session = _driver.AsyncSession(o => o.WithDatabase(Database));
            try
            {
                await session.ExecuteWriteAsync(async tx =>
                {
                    var cursor = await tx.RunAsync(
                        "MATCH (s:Symbol {branch: $branch}) DETACH DELETE s",
                        new Dictionary<string, object> { ["branch"] = Branch });
                    return await cursor.ConsumeAsync();
                });
            }
            finally
            {
                await session.CloseAsync();
            }
        }
        finally
        {
            await _driver.DisposeAsync();
        }
    }

    /// <summary>Strips all four NEO4J_* variables from the child environment (the
    /// missing-configuration path).</summary>
    public Task<CliResult> RunAsync(bool stripNeo4jEnv, params string[] args)
        => RunAsync(
            stripNeo4jEnv
                ? new Dictionary<string, string?>
                {
                    ["NEO4J_URI"] = null,
                    ["NEO4J_USER"] = null,
                    ["NEO4J_PASSWORD"] = null,
                    ["NEO4J_DATABASE"] = null,
                }
                : null,
            args);

    /// <summary>Runs the built edgehop.exe with the given arguments and returns exit
    /// code plus captured stdout/stderr. NEO4J_* env vars are inherited from this process;
    /// <paramref name="envOverrides"/> can replace (value) or remove (null) individual
    /// variables in the child environment. Stderr is password-redacted defensively before
    /// it can reach a failure message.</summary>
    public async Task<CliResult> RunAsync(
        IDictionary<string, string?>? envOverrides, params string[] args)
    {
        var exePath = _exePath ?? throw new InvalidOperationException(
            "CLI exe not located — [Neo4jFact] tests should have been skipped.");

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        // This class tests the CLI over Neo4j; since Handoff 3 the default backend is
        // sqlite, so the child is pinned explicitly (envOverrides may still override).
        psi.Environment["EDGEHOP_BACKEND"] = "neo4j";

        if (envOverrides is not null)
        {
            foreach (var (name, value) in envOverrides)
            {
                if (value is null)
                {
                    psi.Environment.Remove(name);
                }
                else
                {
                    psi.Environment[name] = value;
                }
            }
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start CLI process '{exePath}'.");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // Exited between the timeout and the kill.
            }
            throw new TimeoutException($"edgehop {string.Join(' ', args)} did not exit within 30s.");
        }

        return new CliResult(proc.ExitCode, await stdoutTask, Redact(await stderrTask));
    }

    public Task<CliResult> RunAsync(params string[] args)
        => RunAsync((IDictionary<string, string?>?)null, args);

    private static string Redact(string text)
    {
        var password = Environment.GetEnvironmentVariable("NEO4J_PASSWORD");
        return string.IsNullOrEmpty(password) ? text : text.Replace(password, "***REDACTED***");
    }

    /// <summary>Resolves the built CLI executable, mirroring the test assembly's own
    /// configuration (Debug/Release) — same strategy as <see cref="McpServerFixture"/>.</summary>
    private static string LocateCliExecutable()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? configuration = null;
        DirectoryInfo? root = null;

        for (var d = dir; d is not null; d = d.Parent)
        {
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

        var candidates = new[] { configuration ?? "Debug", "Debug", "Release" }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(c => Path.Combine(rootPath, "EdgeHop.Cli", "bin", c, "net10.0", "edgehop.exe"))
            .ToList();

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException(
                "edgehop.exe not found — build the solution first (dotnet build EdgeHop.sln). " +
                $"Looked in: {string.Join("; ", candidates)}");
    }
}

/// <summary>Outcome of one CLI invocation.</summary>
public sealed record CliResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// End-to-end tests for the <c>edgehop</c> CLI front end, driving the real executable.
/// The query behavior itself is owned by <see cref="EdgeHopQueryService"/> (tested
/// separately); these tests pin what the CLI head owns: argument parsing, exit codes,
/// stdout/stderr discipline, --json wire-shape parity with the MCP tools, human output,
/// and env-var configuration handling.
/// </summary>
public sealed class CliTests : IClassFixture<CliProcessFixture>
{
    private readonly CliProcessFixture _fx;

    public CliTests(CliProcessFixture fx) => _fx = fx;

    // ------------------------------------------------------------------ find-symbol --

    [Neo4jFact]
    public async Task FindSymbol_IsCaseInsensitiveSubstring()
    {
        var result = await _fx.RunAsync("find-symbol", "greet", "--branch", _fx.Branch);

        Assert.Equal(0, result.ExitCode);
        // 'greet' (lowercase) matches the three Greet methods AND the Greeter type.
        Assert.Contains("string Greeter.Greet(string name)", result.Stdout);
        Assert.Contains("string LoudGreeter.Greet(string name)", result.Stdout);
        Assert.Contains("string Caller.CallGreet()", result.Stdout);
        Assert.Contains(_fx.GreeterTypeId, result.Stdout);
        Assert.Contains("4 matches", result.Stdout);
    }

    [Neo4jFact]
    public async Task FindSymbol_KindFilter_ReturnsOnlyThatKind()
    {
        var result = await _fx.RunAsync(
            "find-symbol", "greet", "--kind", SymbolKinds.NamedType, "--branch", _fx.Branch);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(_fx.GreeterTypeId, result.Stdout);
        Assert.DoesNotContain(_fx.GreeterGreetId, result.Stdout);
        Assert.Contains("1 match", result.Stdout);
    }

    [Neo4jFact]
    public async Task FindSymbol_WildcardIsLiteral_FindsNothing()
    {
        // The documented semantics: '*' is a literal character, not a glob.
        var result = await _fx.RunAsync("find-symbol", "greet*", "--branch", _fx.Branch);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("0 matches", result.Stdout);
    }

    [Neo4jFact]
    public async Task FindSymbol_Json_MatchesTheMcpWireShape()
    {
        var result = await _fx.RunAsync("find-symbol", "greet", "--json", "--branch", _fx.Branch);

        Assert.Equal(0, result.ExitCode);

        // Stdout must be EXACTLY one JSON document (pipeable).
        using var doc = JsonDocument.Parse(result.Stdout);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("truncated").GetBoolean());
        var hits = root.GetProperty("hits").EnumerateArray().ToList();
        Assert.Equal(4, hits.Count);

        // Wire property names identical to the MCP tools: id/name/kind/sourceDoc.
        var greeterGreet = Assert.Single(
            hits, h => h.GetProperty("id").GetString() == _fx.GreeterGreetId);
        Assert.Equal("string Greeter.Greet(string name)", greeterGreet.GetProperty("name").GetString());
        Assert.Equal(SymbolKinds.Method, greeterGreet.GetProperty("kind").GetString());
        Assert.Equal("Greeter.cs", greeterGreet.GetProperty("sourceDoc").GetString());
    }

    [Neo4jFact]
    public async Task FindSymbol_Json_NullSourceDoc_OmitsThePropertyLikeTheMcpWire()
    {
        // The MCP SDK's default serializer omits null properties (WhenWritingNull), so a
        // hit with no sourceDoc — every Namespace node the extractor emits — must have NO
        // "sourceDoc" key in --json output either, not "sourceDoc": null.
        var result = await _fx.RunAsync(
            "find-symbol", "TinyFixture", "--kind", SymbolKinds.Namespace,
            "--json", "--branch", _fx.Branch);

        Assert.Equal(0, result.ExitCode);

        using var doc = JsonDocument.Parse(result.Stdout);
        var hit = Assert.Single(doc.RootElement.GetProperty("hits").EnumerateArray());
        Assert.Equal(_fx.NullDocNamespaceId, hit.GetProperty("id").GetString());
        Assert.False(hit.TryGetProperty("sourceDoc", out _),
            "a null sourceDoc must be omitted from --json, matching the MCP wire shape");
    }

    [Neo4jFact]
    public async Task FindSymbol_LimitFlag_TruncatesAtThatLimit()
    {
        // 'greet' matches 4 seeded symbols; --limit 2 must cut the result to exactly 2
        // with truncated=true. A CLI that silently ignored --limit returns 4 and false.
        var result = await _fx.RunAsync(
            "find-symbol", "greet", "--limit", "2", "--json", "--branch", _fx.Branch);

        Assert.Equal(0, result.ExitCode);

        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.Equal(2, doc.RootElement.GetProperty("hits").GetArrayLength());
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Neo4jFact]
    public async Task FindSymbol_ZeroMatches_StillExitsZero()
    {
        var result = await _fx.RunAsync("find-symbol", "zzz-no-such-symbol-zzz", "--branch", _fx.Branch);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("0 matches", result.Stdout);
    }

    // ------------------------------------------------------------------ get-callers --

    [Neo4jFact]
    public async Task GetCallers_Depth1_ReturnsExactlyTheThreeDirectCallers()
    {
        var result = await _fx.RunAsync(
            "get-callers", _fx.GreeterGreetId, "--json", "--branch", _fx.Branch);

        Assert.Equal(0, result.ExitCode);

        using var doc = JsonDocument.Parse(result.Stdout);
        var root = doc.RootElement;
        Assert.Equal(_fx.GreeterGreetId, root.GetProperty("targetId").GetString());
        Assert.Equal(1, root.GetProperty("depth").GetInt32());

        var ids = root.GetProperty("callers").EnumerateArray()
            .Select(c => c.GetProperty("id").GetString())
            .ToHashSet(StringComparer.Ordinal);

        // Every expected id carries the GUID suffix: branch 'main' (which holds the
        // unsuffixed twins of this topology) cannot satisfy these assertions, so a CLI
        // that ignored --branch fails here.
        Assert.Equal(3, ids.Count);
        Assert.Contains(_fx.LoudGreeterGreetId, ids);
        Assert.Contains(_fx.CallerCallGreetId, ids);
        Assert.Contains(_fx.DecoratorDecorateId, ids);
        Assert.All(ids, id => Assert.EndsWith(_fx.IdSuffix, id));
    }

    [Neo4jFact]
    public async Task GetCallers_Depth2_AddsTheTransitiveCaller()
    {
        var result = await _fx.RunAsync(
            "get-callers", _fx.GreeterGreetId,
            "--depth", "2", "--json", "--branch", _fx.Branch);

        Assert.Equal(0, result.ExitCode);

        using var doc = JsonDocument.Parse(result.Stdout);
        var ids = doc.RootElement.GetProperty("callers").EnumerateArray()
            .Select(c => c.GetProperty("id").GetString())
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(4, ids.Count);
        Assert.Contains(_fx.AppRunId, ids);
        Assert.DoesNotContain(_fx.GreeterGreetId, ids);
        Assert.All(ids, id => Assert.EndsWith(_fx.IdSuffix, id));
    }

    [Neo4jFact]
    public async Task GetCallers_DepthOutOfRange_ExitsOneWithMessageOnStderr()
    {
        var result = await _fx.RunAsync(
            "get-callers", _fx.GreeterGreetId, "--depth", "11", "--branch", _fx.Branch);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("depth must be between 1 and 10; got 11.", result.Stderr);
        // CleanMessage must strip the BCL decoration: neither the "(Parameter 'depth')"
        // suffix nor the "Actual value was 11." second line may leak to stderr.
        Assert.DoesNotContain("(Parameter", result.Stderr);
        Assert.DoesNotContain("Actual value", result.Stderr);
        Assert.Equal(string.Empty, result.Stdout);
    }

    // ----------------------------------------------------------- get-relationships --

    [Neo4jFact]
    public async Task GetRelationships_HumanOutput_TagsEachNeighborWithEdgeAndDirection()
    {
        // Caller.CallGreet() has one outgoing CALLS edge (to Greeter.Greet); the default
        // direction is 'out', so that single neighbor is reported, tagged "CALLS out".
        var result = await _fx.RunAsync(
            "get-relationships", _fx.CallerCallGreetId, "--branch", _fx.Branch);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("direction out", result.Stdout);
        Assert.Contains($"{EdgeTypes.Calls} {RelationshipDirections.Out}", result.Stdout);
        Assert.Contains("string Greeter.Greet(string name)", result.Stdout);
        Assert.Contains(_fx.GreeterGreetId, result.Stdout);
        Assert.Contains("1 relationship", result.Stdout);
    }

    [Neo4jFact]
    public async Task GetRelationships_Json_MatchesTheMcpWireShape()
    {
        // Greeter (a NamedType) has two outgoing edges: IMPLEMENTS -> ISalutation and
        // CONTAINS -> Greeter.Greet. No --edge-type filter is given, so the wire 'edgeType'
        // is null and — exactly like the MCP serializer's WhenWritingNull — must be OMITTED.
        var result = await _fx.RunAsync(
            "get-relationships", _fx.GreeterTypeId, "--json", "--branch", _fx.Branch);

        Assert.Equal(0, result.ExitCode);

        // Stdout must be EXACTLY one JSON document (pipeable).
        using var doc = JsonDocument.Parse(result.Stdout);
        var root = doc.RootElement;

        Assert.Equal(_fx.GreeterTypeId, root.GetProperty("targetId").GetString());
        Assert.Equal(RelationshipDirections.Out, root.GetProperty("direction").GetString());
        Assert.Equal(1, root.GetProperty("depth").GetInt32());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.False(root.TryGetProperty("edgeType", out _),
            "an unfiltered relationship query must omit the null edgeType, matching the MCP wire shape");

        var hits = root.GetProperty("hits").EnumerateArray().ToList();
        Assert.Equal(2, hits.Count);

        // Each hit: { symbol: {id,name,kind,sourceDoc?,isComponent,routes?}, edgeType, direction }.
        var implementsHit = Assert.Single(
            hits, h => h.GetProperty("edgeType").GetString() == EdgeTypes.Implements);
        Assert.Equal(RelationshipDirections.Out, implementsHit.GetProperty("direction").GetString());

        var symbol = implementsHit.GetProperty("symbol");
        Assert.Equal(_fx.SalutationTypeId, symbol.GetProperty("id").GetString());
        Assert.Equal("ISalutation", symbol.GetProperty("name").GetString());
        Assert.Equal(SymbolKinds.NamedType, symbol.GetProperty("kind").GetString());
        // A non-component symbol: isComponent is always emitted (false), routes omitted.
        Assert.False(symbol.GetProperty("isComponent").GetBoolean());
        Assert.False(symbol.TryGetProperty("routes", out _),
            "a symbol with no routes must omit the routes key, matching the MCP wire shape");

        var containsHit = Assert.Single(
            hits, h => h.GetProperty("edgeType").GetString() == EdgeTypes.Contains);
        Assert.Equal(_fx.GreeterGreetId, containsHit.GetProperty("symbol").GetProperty("id").GetString());
    }

    [Neo4jFact]
    public async Task GetRelationships_Json_SurfacesComponentAndRoutesLikeTheMcpWire()
    {
        // Home RENDERS Child. Home is a routable component (isComponent + routes); Child is a
        // component with no routes. Both facets must round-trip through --json exactly as the
        // MCP wire carries them: isComponent always present, routes only when non-null.
        var outward = await _fx.RunAsync(
            "get-relationships", _fx.HomeComponentId,
            "--direction", RelationshipDirections.Out, "--json", "--branch", _fx.Branch);

        Assert.Equal(0, outward.ExitCode);
        using (var doc = JsonDocument.Parse(outward.Stdout))
        {
            var hit = Assert.Single(doc.RootElement.GetProperty("hits").EnumerateArray());
            Assert.Equal(EdgeTypes.Renders, hit.GetProperty("edgeType").GetString());

            var child = hit.GetProperty("symbol");
            Assert.Equal(_fx.ChildComponentId, child.GetProperty("id").GetString());
            Assert.True(child.GetProperty("isComponent").GetBoolean());
            Assert.False(child.TryGetProperty("routes", out _),
                "a component with no routes must omit the routes key, matching the MCP wire shape");
        }

        var inward = await _fx.RunAsync(
            "get-relationships", _fx.ChildComponentId,
            "--direction", RelationshipDirections.In, "--json", "--branch", _fx.Branch);

        Assert.Equal(0, inward.ExitCode);
        using (var doc = JsonDocument.Parse(inward.Stdout))
        {
            var hit = Assert.Single(doc.RootElement.GetProperty("hits").EnumerateArray());
            Assert.Equal(RelationshipDirections.In, hit.GetProperty("direction").GetString());

            var home = hit.GetProperty("symbol");
            Assert.Equal(_fx.HomeComponentId, home.GetProperty("id").GetString());
            Assert.True(home.GetProperty("isComponent").GetBoolean());
            var routes = home.GetProperty("routes").EnumerateArray().Select(r => r.GetString()!).ToArray();
            Assert.Equal(_fx.HomeRoutes, routes);
        }
    }

    [Neo4jFact]
    public async Task GetRelationships_Direction_SelectsIncomingOutgoingOrBoth()
    {
        // Caller.CallGreet(): out -> Greeter.Greet (it calls), in <- App.Run() (calls it).
        var outward = await _fx.RunAsync(
            "get-relationships", _fx.CallerCallGreetId,
            "--direction", RelationshipDirections.Out, "--json", "--branch", _fx.Branch);
        Assert.Equal(0, outward.ExitCode);
        using (var doc = JsonDocument.Parse(outward.Stdout))
        {
            Assert.Equal(RelationshipDirections.Out, doc.RootElement.GetProperty("direction").GetString());
            var hit = Assert.Single(doc.RootElement.GetProperty("hits").EnumerateArray());
            Assert.Equal(_fx.GreeterGreetId, hit.GetProperty("symbol").GetProperty("id").GetString());
            Assert.Equal(RelationshipDirections.Out, hit.GetProperty("direction").GetString());
        }

        var inward = await _fx.RunAsync(
            "get-relationships", _fx.CallerCallGreetId,
            "--direction", RelationshipDirections.In, "--json", "--branch", _fx.Branch);
        Assert.Equal(0, inward.ExitCode);
        using (var doc = JsonDocument.Parse(inward.Stdout))
        {
            Assert.Equal(RelationshipDirections.In, doc.RootElement.GetProperty("direction").GetString());
            var hit = Assert.Single(doc.RootElement.GetProperty("hits").EnumerateArray());
            Assert.Equal(_fx.AppRunId, hit.GetProperty("symbol").GetProperty("id").GetString());
            Assert.Equal(RelationshipDirections.In, hit.GetProperty("direction").GetString());
        }

        var both = await _fx.RunAsync(
            "get-relationships", _fx.CallerCallGreetId,
            "--direction", RelationshipDirections.Both, "--json", "--branch", _fx.Branch);
        Assert.Equal(0, both.ExitCode);
        using (var doc = JsonDocument.Parse(both.Stdout))
        {
            Assert.Equal(RelationshipDirections.Both, doc.RootElement.GetProperty("direction").GetString());
            var ids = doc.RootElement.GetProperty("hits").EnumerateArray()
                .Select(h => h.GetProperty("symbol").GetProperty("id").GetString())
                .ToHashSet(StringComparer.Ordinal);
            // Both endpoints reached; the GUID suffix means branch 'main' cannot satisfy this.
            Assert.Equal(2, ids.Count);
            Assert.Contains(_fx.GreeterGreetId, ids);
            Assert.Contains(_fx.AppRunId, ids);
            Assert.All(ids, id => Assert.EndsWith(_fx.IdSuffix, id));
        }
    }

    [Neo4jFact]
    public async Task GetRelationships_EdgeTypeFilter_ReturnsOnlyThatType()
    {
        // Greeter has both IMPLEMENTS and CONTAINS outgoing edges; --edge-type keeps just one.
        var result = await _fx.RunAsync(
            "get-relationships", _fx.GreeterTypeId,
            "--edge-type", EdgeTypes.Implements, "--json", "--branch", _fx.Branch);

        Assert.Equal(0, result.ExitCode);

        using var doc = JsonDocument.Parse(result.Stdout);
        var root = doc.RootElement;
        // The applied filter is echoed on the wire (present, not omitted).
        Assert.Equal(EdgeTypes.Implements, root.GetProperty("edgeType").GetString());

        var hit = Assert.Single(root.GetProperty("hits").EnumerateArray());
        Assert.Equal(EdgeTypes.Implements, hit.GetProperty("edgeType").GetString());
        Assert.Equal(_fx.SalutationTypeId, hit.GetProperty("symbol").GetProperty("id").GetString());
    }

    [Neo4jFact]
    public async Task GetRelationships_DepthAboveOneWithoutEdgeType_ExitsOneWithMessageOnStderr()
    {
        // Multi-hop mixed-type traversal is meaningless: depth > 1 REQUIRES a single
        // --edge-type. The shared service throws; the CLI surfaces exit 1 + the sentence.
        var result = await _fx.RunAsync(
            "get-relationships", _fx.GreeterGreetId, "--depth", "2", "--branch", _fx.Branch);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(
            "depth > 1 requires a single --edge-type; multi-hop mixed-type traversal is not supported.",
            result.Stderr);
        // CleanMessage must strip the BCL "(Parameter 'edgeType')" decoration.
        Assert.DoesNotContain("(Parameter", result.Stderr);
        Assert.Equal(string.Empty, result.Stdout);
    }

    // ------------------------------------------------------------------- get-path --

    [Neo4jFact]
    public async Task GetPath_Json_ReturnsOrderedNodesWithTypedEdges()
    {
        // App.Run() --CALLS--> Caller.CallGreet() --CALLS--> Greeter.Greet(): a 2-hop path.
        var result = await _fx.RunAsync(
            "get-path", _fx.AppRunId, _fx.GreeterGreetId, "--json", "--branch", _fx.Branch);

        Assert.Equal(0, result.ExitCode);

        using var doc = JsonDocument.Parse(result.Stdout);
        var root = doc.RootElement;
        Assert.Equal(_fx.AppRunId, root.GetProperty("fromId").GetString());
        Assert.Equal(_fx.GreeterGreetId, root.GetProperty("toId").GetString());
        Assert.True(root.GetProperty("found").GetBoolean());

        var nodes = root.GetProperty("nodes").EnumerateArray().ToList();
        Assert.Equal(3, nodes.Count);

        // Node 0 is fromId; it has no incoming edge, so edgeTypeFromPrev is null and OMITTED.
        Assert.Equal(_fx.AppRunId, nodes[0].GetProperty("symbol").GetProperty("id").GetString());
        Assert.False(nodes[0].TryGetProperty("edgeTypeFromPrev", out _),
            "the first path node has no edgeTypeFromPrev; null is omitted on the wire");

        Assert.Equal(_fx.CallerCallGreetId, nodes[1].GetProperty("symbol").GetProperty("id").GetString());
        Assert.Equal(EdgeTypes.Calls, nodes[1].GetProperty("edgeTypeFromPrev").GetString());

        Assert.Equal(_fx.GreeterGreetId, nodes[2].GetProperty("symbol").GetProperty("id").GetString());
        Assert.Equal(EdgeTypes.Calls, nodes[2].GetProperty("edgeTypeFromPrev").GetString());
    }

    [Neo4jFact]
    public async Task GetPath_Unreachable_ReturnsFoundFalseWithNoNodes()
    {
        // Greeter.Greet() has no outgoing edges, so App.Run() is unreachable FROM it.
        var result = await _fx.RunAsync(
            "get-path", _fx.GreeterGreetId, _fx.AppRunId, "--json", "--branch", _fx.Branch);

        Assert.Equal(0, result.ExitCode);

        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.False(doc.RootElement.GetProperty("found").GetBoolean());
        Assert.Empty(doc.RootElement.GetProperty("nodes").EnumerateArray());
    }

    [Neo4jFact]
    public async Task GetPath_HumanOutput_ShowsTheArrowedChain()
    {
        var result = await _fx.RunAsync(
            "get-path", _fx.AppRunId, _fx.GreeterGreetId, "--branch", _fx.Branch);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("(2 hops)", result.Stdout);
        Assert.Contains(
            "string App.Run() --CALLS--> string Caller.CallGreet() --CALLS--> string Greeter.Greet(string name)",
            result.Stdout);
    }

    // ---------------------------------------------------------------------- stats --

    [Neo4jFact]
    public async Task Stats_Json_ReportsTotalsKindsTypesAndGodNodes()
    {
        var result = await _fx.RunAsync("stats", "--json", "--branch", _fx.Branch);

        Assert.Equal(0, result.ExitCode);

        using var doc = JsonDocument.Parse(result.Stdout);
        var root = doc.RootElement;
        Assert.Equal(_fx.Branch, root.GetProperty("branch").GetString());
        // The GUID branch holds exactly the seeded graph: 10 nodes, 8 edges.
        Assert.Equal(10L, root.GetProperty("totalNodes").GetInt64());
        Assert.Equal(8L, root.GetProperty("totalEdges").GetInt64());

        var kinds = root.GetProperty("nodesByKind").EnumerateArray()
            .ToDictionary(k => k.GetProperty("kind").GetString()!, k => k.GetProperty("count").GetInt64());
        Assert.Equal(4L, kinds[SymbolKinds.NamedType]);
        Assert.Equal(5L, kinds[SymbolKinds.Method]);
        Assert.Equal(1L, kinds[SymbolKinds.Namespace]);

        var types = root.GetProperty("edgesByType").EnumerateArray()
            .ToDictionary(t => t.GetProperty("type").GetString()!, t => t.GetProperty("count").GetInt64());
        Assert.Equal(4L, types[EdgeTypes.Calls]);
        Assert.Equal(1L, types[EdgeTypes.Contains]);
        Assert.Equal(1L, types[EdgeTypes.Implements]);
        Assert.Equal(1L, types[EdgeTypes.Overrides]);
        Assert.Equal(1L, types[EdgeTypes.Renders]);

        // Greeter.Greet() is the most-connected node: 3 CALLS + 1 OVERRIDES = degree 4. Its
        // incoming CONTAINS edge from Greeter is EXCLUDED, so the count is 4 (not 5) and the
        // container never dominates the god-node ranking.
        var top = root.GetProperty("godNodes").EnumerateArray().First();
        Assert.Equal(_fx.GreeterGreetId, top.GetProperty("symbol").GetProperty("id").GetString());
        Assert.Equal(4L, top.GetProperty("degree").GetInt64());
    }

    // ------------------------------------------------------- usage / configuration --

    [Neo4jFact]
    public async Task NoArguments_ExitsOneWithUsageOnStderr()
    {
        var result = await _fx.RunAsync();

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("usage:", result.Stderr);
        Assert.Equal(string.Empty, result.Stdout);
    }

    [Neo4jFact]
    public async Task UnknownOption_ExitsOneWithUsageOnStderr()
    {
        var result = await _fx.RunAsync("find-symbol", "greet", "--frobnicate");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Unknown option '--frobnicate'", result.Stderr);
        Assert.Contains("usage:", result.Stderr);
    }

    [Neo4jFact]
    public async Task Help_DocumentsTheNoWildcardsSemantics()
    {
        var result = await _fx.RunAsync("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("find-symbol", result.Stdout);
        Assert.Contains("get-callers", result.Stdout);
        Assert.Contains("NO wildcards", result.Stdout);
        Assert.Contains("substring", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Neo4jFact]
    public async Task MalformedUri_ExitsOneWithCleanMessageAndNoStackTrace()
    {
        var result = await _fx.RunAsync(
            new Dictionary<string, string?> { ["NEO4J_URI"] = "not a uri" },
            "find-symbol", "greet");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("NEO4J_URI", result.Stderr);
        Assert.DoesNotContain("   at ", result.Stderr);
        Assert.Equal(string.Empty, result.Stdout);
    }

    [Neo4jFact]
    public async Task MissingEnvVars_ExitsOneNamingTheVariables()
    {
        // Valid arguments, but the child process gets no NEO4J_* configuration: the
        // settings error (which names variables, never values) goes to stderr, exit 1.
        var result = await _fx.RunAsync(stripNeo4jEnv: true, "find-symbol", "greet");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("NEO4J_URI", result.Stderr);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.DoesNotContain("   at ", result.Stderr);
    }
}
