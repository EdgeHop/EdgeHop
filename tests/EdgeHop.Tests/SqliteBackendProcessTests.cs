using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EdgeHop.Core;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// Handoff 3 Phase D — the <c>EDGEHOP_BACKEND=sqlite</c> surface exercised through the
/// REAL executables (extractor, CLI, MCP server), with every <c>NEO4J_*</c> variable
/// STRIPPED from the child environment: these tests prove the definition-of-done claim
/// that the whole loop — index, branches, prune, CLI queries, both MCP tools — runs with
/// no Neo4j server at all. Each test writes only to a throwaway store file under a
/// unique temp directory; the developer's real store path (<c>EDGEHOP_SQLITE_PATH</c>)
/// is never inherited because the variable is explicitly overridden.
/// All tests live in one class so the two MSBuild-loading index runs never race.
/// </summary>
public sealed class SqliteBackendProcessTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _storePath;

    public SqliteBackendProcessTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"edgehop-sqlite-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _storePath = Path.Combine(_tempDir, "store.db");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // A straggling WAL handle on a crashed run; the OS temp cleaner owns it.
        }
    }

    /// <summary>Child environment: sqlite backend on this test's throwaway store, all
    /// NEO4J_* removed (no Neo4j exists as far as the child knows), EDGEHOP_BRANCH /
    /// EDGEHOP_REPO removed so nothing ambient leaks into branch resolution.</summary>
    private Dictionary<string, string?> SqliteEnv(string? branchVar = null) => new()
    {
        ["EDGEHOP_BACKEND"] = "sqlite",
        ["EDGEHOP_SQLITE_PATH"] = _storePath,
        ["NEO4J_URI"] = null,
        ["NEO4J_USER"] = null,
        ["NEO4J_PASSWORD"] = null,
        ["NEO4J_DATABASE"] = null,
        ["EDGEHOP_BRANCH"] = branchVar,
        ["EDGEHOP_REPO"] = null,
    };

    // ------------------------------------------------------------ backend selection --

    [Fact]
    public async Task Unknown_backend_value_is_a_clear_startup_error_on_every_head()
    {
        var env = SqliteEnv();
        env["EDGEHOP_BACKEND"] = "martian";

        var extractor = await RunToolAsync(ExtractorExe, env, "branches");
        Assert.Equal(1, extractor.ExitCode);
        Assert.Contains("EDGEHOP_BACKEND", extractor.Stderr);
        Assert.Contains("martian", extractor.Stderr);
        Assert.Contains("sqlite", extractor.Stderr); // names the valid values

        var cli = await RunToolAsync(CliExe, env, "find-symbol", "anything");
        Assert.Equal(1, cli.ExitCode);
        Assert.Contains("EDGEHOP_BACKEND", cli.Stderr);
        Assert.Contains("martian", cli.Stderr);
    }

    [Fact]
    public async Task Unset_backend_defaults_to_sqlite()
    {
        // Owner decision 2026-07-17: sqlite is the default when EDGEHOP_BACKEND is
        // unset. NEO4J_* are stripped too — the default must need no server at all.
        var env = SqliteEnv();
        env["EDGEHOP_BACKEND"] = null;

        var result = await RunToolAsync(ExtractorExe, env, "branches");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Backend: sqlite", result.Stderr);
        Assert.Contains(_storePath, result.Stderr);
    }

    [Fact]
    public async Task Default_store_path_is_derived_per_repo_from_EDGEHOP_REPO()
    {
        // Fabricated git working tree (a .git\HEAD is all the detector needs).
        var repoName = $"fake-repo-{Guid.NewGuid():N}";
        var repo = Path.Combine(_tempDir, repoName);
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/main\n");

        var env = SqliteEnv();
        env["EDGEHOP_SQLITE_PATH"] = null; // let the derivation run
        env["EDGEHOP_REPO"] = repo;

        try
        {
            var result = await RunToolAsync(ExtractorExe, env, "branches");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Backend: sqlite", result.Stderr);
            Assert.Contains(Path.Combine("EdgeHop", "stores", repoName + "-"), result.Stderr);
            Assert.Contains("The graph is empty: no branches.", result.Stdout);
        }
        finally
        {
            // The derived store lives under the real %LOCALAPPDATA%\EdgeHop\stores;
            // the GUID repo name makes the file unique — remove this test's artifacts.
            var storesDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EdgeHop", "stores");
            if (Directory.Exists(storesDir))
            {
                foreach (var file in Directory.GetFiles(storesDir, repoName + "-*"))
                {
                    File.Delete(file);
                }
            }
        }
    }

    [Fact]
    public async Task Branches_on_a_fresh_sqlite_store_reports_empty_and_names_the_backend()
    {
        var result = await RunToolAsync(ExtractorExe, SqliteEnv(), "branches");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("The graph is empty: no branches.", result.Stdout);
        Assert.Contains("Backend: sqlite", result.Stderr);
        Assert.Contains(_storePath, result.Stderr);
    }

    // ------------------------------------------------- fixture contract end to end --

    [Fact]
    public async Task TinyFixture_full_loop_index_query_reindex_prune_with_no_neo4j()
    {
        var branch = $"test-sqlite-e2e-{Guid.NewGuid():N}";
        var sln = FixtureTestSupport.LocateFixtureSolution("TinyFixture", "TinyFixture.sln");
        await FixtureTestSupport.RestoreFixtureSolutionAsync(sln);

        // ---- index: the pinned 19/28 TinyFixture contract on the sqlite backend. ----
        var index = await RunToolAsync(
            ExtractorExe, SqliteEnv(), timeoutSeconds: 300,
            "index", sln, "--branch", branch, "--no-worktree");
        Assert.True(index.ExitCode == 0, $"index failed:\n{index.Stderr}\n{index.Stdout}");
        Assert.Contains("Nodes: 19", index.Stdout);
        Assert.Contains("Edges: 28", index.Stdout);
        Assert.Contains($"Reconciled branch '{branch}' at sqlite", index.Stdout);
        Assert.Contains("Backend: sqlite", index.Stderr);

        // ---- branches lists it. ----
        var branches = await RunToolAsync(ExtractorExe, SqliteEnv(), "branches");
        Assert.Equal(0, branches.ExitCode);
        Assert.Contains($"{branch}: 19 nodes", branches.Stdout);

        // ---- CLI find-symbol --json: MCP wire shape from the sqlite store. ----
        var find = await RunToolAsync(
            CliExe, SqliteEnv(), "find-symbol", "CallGreet", "--branch", branch, "--json");
        Assert.Equal(0, find.ExitCode);
        using (var doc = JsonDocument.Parse(find.Stdout))
        {
            var hit = Assert.Single(doc.RootElement.GetProperty("hits").EnumerateArray().ToList());
            Assert.Equal("Method:string TinyFixture.Caller.CallGreet()", hit.GetProperty("id").GetString());
            Assert.Equal("Caller.cs", hit.GetProperty("sourceDoc").GetString());
            Assert.False(doc.RootElement.GetProperty("truncated").GetBoolean());
        }

        // ---- CLI get-callers --json depth 2: the EXPECTED-GRAPH.md caller set. ----
        var callers = await RunToolAsync(
            CliExe, SqliteEnv(),
            "get-callers", "Method:string TinyFixture.Greeter.Greet(string)",
            "--depth", "2", "--branch", branch, "--json");
        Assert.Equal(0, callers.ExitCode);
        using (var doc = JsonDocument.Parse(callers.Stdout))
        {
            var ids = doc.RootElement.GetProperty("callers").EnumerateArray()
                .Select(c => c.GetProperty("id").GetString())
                .ToHashSet(StringComparer.Ordinal);
            Assert.Equal(
                new HashSet<string?>
                {
                    "Method:string TinyFixture.LoudGreeter.Greet(string)",
                    "Method:string TinyFixture.Caller.CallGreet()",
                    "Method:string TinyFixture.Decorator.Decorate()",
                    "Method:string TinyFixture.App.Run()",
                },
                ids);
        }

        // ---- re-index: reconcile is a no-op on an unchanged solution. ----
        var reindex = await RunToolAsync(
            ExtractorExe, SqliteEnv(), timeoutSeconds: 300,
            "index", sln, "--branch", branch, "--no-worktree");
        Assert.Equal(0, reindex.ExitCode);
        Assert.Contains("pruned 0 stale nodes / 0 stale edges", reindex.Stdout);

        // ---- prune: refused without --yes, deletes with it. ----
        var refused = await RunToolAsync(ExtractorExe, SqliteEnv(), "prune", "--branch", branch);
        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("19 nodes", refused.Stdout);
        Assert.Contains("Nothing was deleted", refused.Stdout);

        var pruned = await RunToolAsync(ExtractorExe, SqliteEnv(), "prune", "--branch", branch, "--yes");
        Assert.Equal(0, pruned.ExitCode);
        Assert.Contains("deleted 19 nodes", pruned.Stdout);

        var after = await RunToolAsync(ExtractorExe, SqliteEnv(), "branches");
        Assert.DoesNotContain(branch, after.Stdout);
    }

    [Fact]
    public async Task BlazorFixture_contract_holds_on_the_sqlite_backend()
    {
        var branch = $"test-sqlite-e2e-{Guid.NewGuid():N}";
        var sln = FixtureTestSupport.LocateFixtureSolution("BlazorFixture", "BlazorFixture.sln");
        await FixtureTestSupport.RestoreFixtureSolutionAsync(sln);

        var index = await RunToolAsync(
            ExtractorExe, SqliteEnv(), timeoutSeconds: 300,
            "index", sln, "--branch", branch, "--no-worktree");
        Assert.True(index.ExitCode == 0, $"index failed:\n{index.Stderr}\n{index.Stdout}");
        Assert.Contains("Nodes: 19", index.Stdout);
        Assert.Contains("Edges: 22", index.Stdout);

        // The Handoff 2 promise on the new backend: a handler bound in markup is reached
        // through get_callers (BuildRenderTree → HandleClick is a CALLS edge), and the
        // component symbol's sourceDoc is the authored .razor file.
        var find = await RunToolAsync(
            CliExe, SqliteEnv(), "find-symbol", "HandleClick", "--branch", branch, "--json");
        Assert.Equal(0, find.ExitCode);
        string handlerId;
        using (var doc = JsonDocument.Parse(find.Stdout))
        {
            var hit = Assert.Single(doc.RootElement.GetProperty("hits").EnumerateArray().ToList());
            handlerId = hit.GetProperty("id").GetString()!;
            Assert.Equal("Pages/Home.razor", hit.GetProperty("sourceDoc").GetString());
        }

        var callers = await RunToolAsync(
            CliExe, SqliteEnv(), "get-callers", handlerId, "--branch", branch, "--json");
        Assert.Equal(0, callers.ExitCode);
        using (var doc = JsonDocument.Parse(callers.Stdout))
        {
            var caller = Assert.Single(doc.RootElement.GetProperty("callers").EnumerateArray().ToList());
            Assert.Contains("BuildRenderTree", caller.GetProperty("id").GetString());
            Assert.Equal("Pages/Home.razor", caller.GetProperty("sourceDoc").GetString());
        }
    }

    // ------------------------------------------------------------------ MCP server --

    [Fact]
    public async Task Mcp_server_serves_both_tools_from_a_sqlite_store_with_no_neo4j()
    {
        // Seed the throwaway store in-process (the store releases all file handles per
        // call, so the child process can open the same file — WAL two-process behavior).
        var branch = $"test-sqlite-mcp-{Guid.NewGuid():N}";
        var greetId = "Method:string TinyFixture.Greeter.Greet(string)";
        var callerId = $"Method:string TinyFixture.Caller.CallGreet()|{branch}";
        await using (var store = new SqliteGraphStore(new SqliteSettings(_storePath)))
        {
            await store.Writer.UpsertNodesAsync(
            [
                new NodeRow(branch, greetId, "string Greeter.Greet(string name)",
                    SymbolKinds.Method, "Greeter.cs", "Tiny", false),
                new NodeRow(branch, callerId, "string Caller.CallGreet()",
                    SymbolKinds.Method, "Caller.cs", "Tiny", false),
            ]);
            await store.Writer.UpsertEdgesAsync(
                [new EdgeRow(branch, callerId, greetId, EdgeTypes.Calls, "Caller.cs")]);
        }

        var psi = new ProcessStartInfo
        {
            FileName = McpExe,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var (name, value) in SqliteEnv(branchVar: branch))
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

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start EdgeHop.Mcp.");
        var stderrTask = proc.StandardError.ReadToEndAsync();
        proc.StandardInput.AutoFlush = true;

        try
        {
            var init = await RpcAsync(proc, 1, "initialize", new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "edgehop-tests", version = "0.0.1" },
            });
            Assert.Equal("edgehop",
                init.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
            await proc.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","method":"notifications/initialized"}""");

            var find = await RpcAsync(proc, 2, "tools/call", new
            {
                name = "find_symbol",
                arguments = new { query = "CallGreet" },
            });
            var findJson = ToolResultJson(find);
            var hit = Assert.Single(
                findJson.RootElement.GetProperty("hits").EnumerateArray().ToList());
            Assert.Equal(callerId, hit.GetProperty("id").GetString());

            var callers = await RpcAsync(proc, 3, "tools/call", new
            {
                name = "get_callers",
                arguments = new { symbolId = greetId, depth = 1 },
            });
            var callersJson = ToolResultJson(callers);
            var caller = Assert.Single(
                callersJson.RootElement.GetProperty("callers").EnumerateArray().ToList());
            Assert.Equal(callerId, caller.GetProperty("id").GetString());
        }
        finally
        {
            try
            {
                proc.StandardInput.Close();
                if (!proc.WaitForExit(8000))
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Already exited.
            }
        }

        var stderr = await stderrTask;
        Assert.Contains("backend sqlite", stderr);
    }

    /// <summary>Sends one JSON-RPC request over stdin and reads stdout frames until the
    /// response with the matching id arrives.</summary>
    private static async Task<JsonElement> RpcAsync(Process proc, int id, string method, object @params)
    {
        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params,
        });
        await proc.StandardInput.WriteLineAsync(request);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            var line = await proc.StandardOutput.ReadLineAsync(cts.Token)
                ?? throw new InvalidOperationException($"MCP server closed stdout awaiting response {id}.");
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var element = JsonSerializer.Deserialize<JsonElement>(line);
            if (element.TryGetProperty("id", out var responseId)
                && responseId.ValueKind == JsonValueKind.Number
                && responseId.GetInt32() == id)
            {
                return element;
            }
        }
    }

    /// <summary>Extracts the tool result's structured JSON payload (the MCP SDK also
    /// mirrors it as text content; structuredContent is authoritative here).</summary>
    private static JsonDocument ToolResultJson(JsonElement response)
    {
        var result = response.GetProperty("result");
        if (result.TryGetProperty("structuredContent", out var structured))
        {
            return JsonDocument.Parse(structured.GetRawText());
        }

        var text = result.GetProperty("content").EnumerateArray().First()
            .GetProperty("text").GetString()!;
        return JsonDocument.Parse(text);
    }

    // ---------------------------------------------------------------------- helpers --

    private static string ExtractorExe => LocateExe("EdgeHop.Indexer", "edgehop-extract.exe");
    private static string CliExe => LocateExe("EdgeHop.Cli", "edgehop.exe");
    private static string McpExe => LocateExe("EdgeHop.Mcp", "EdgeHop.Mcp.exe");

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunToolAsync(
        string exePath, IDictionary<string, string?> env, params string[] args)
        => await RunToolAsync(exePath, env, timeoutSeconds: 60, args);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunToolAsync(
        string exePath, IDictionary<string, string?> env, int timeoutSeconds, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var (name, value) in env)
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

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{exePath}'.");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
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

            throw new TimeoutException(
                $"{Path.GetFileName(exePath)} {string.Join(' ', args)} did not exit within {timeoutSeconds}s.");
        }

        return (proc.ExitCode, await stdoutTask, await stderrTask);
    }

    /// <summary>Locates a built executable next to this test assembly's configuration —
    /// same walk-up strategy as <see cref="ExtractorVerbTests"/>.</summary>
    private static string LocateExe(string projectName, string exeName)
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
            .Select(c => Path.Combine(rootPath, projectName, "bin", c, "net10.0", exeName))
            .ToList();

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException(
                $"{exeName} not found — build the solution first (dotnet build EdgeHop.sln). "
                + $"Looked in: {string.Join("; ", candidates)}");
    }
}
