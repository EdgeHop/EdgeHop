using System.Diagnostics;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// The unified <c>edgehop</c> command (the single .NET global tool, project EdgeHop.Tool)
/// is a thin dispatcher over the three heads. These tests drive the real built executable
/// and assert only that each verb ROUTES to the right head — the heads' own behavior is
/// covered by <see cref="ExtractorVerbTests"/>, the query-service tests and the stdio tests.
/// Every case is hermetic: sqlite backend at a temp path, a unique GUID branch, no shared
/// state touched.
/// </summary>
public sealed class DispatcherVerbTests
{
    [Fact]
    public async Task No_arguments_prints_unified_usage_and_exits_zero()
    {
        var (exitCode, stdout, _) = await RunAsync();
        Assert.Equal(0, exitCode);
        Assert.Contains("Usage: edgehop", stdout);
        // Usage spans all three heads.
        Assert.Contains("index", stdout);
        Assert.Contains("find-symbol", stdout);
        Assert.Contains("mcp", stdout);
    }

    [Fact]
    public async Task Unknown_command_is_reported_and_exits_one()
    {
        var (exitCode, _, stderr) = await RunAsync("frobnicate");
        Assert.Equal(1, exitCode);
        Assert.Contains("unknown command", stderr);
    }

    [Fact]
    public async Task Version_flag_prints_a_version_and_exits_zero()
    {
        var (exitCode, stdout, _) = await RunAsync("--version");
        Assert.Equal(0, exitCode);
        // Nerdbank.GitVersioning stamps at least a "major.minor.patch" informational version.
        Assert.Matches(@"\d+\.\d+\.\d+", stdout);
    }

    [Fact]
    public async Task Index_verb_routes_to_the_indexer_head()
    {
        // A missing target is rejected by the indexer before any backend work — the "not
        // found" message is proof the token reached ExtractorApp rather than the dispatcher.
        var (exitCode, _, stderr) = await RunAsync("index", "no-such-solution.sln", "--dry-run");
        Assert.Equal(1, exitCode);
        Assert.Contains("not found", stderr);
        Assert.DoesNotContain("unknown command", stderr);
    }

    [Fact]
    public async Task FindSymbol_verb_routes_to_the_query_head()
    {
        // Empty temp store + GUID branch: the query head runs and reports zero matches
        // (exit 0). That it searched at all proves the token reached CliApp.
        var (exitCode, stdout, stderr) = await RunAsync("find-symbol", "nothing-here-xyz");
        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("unknown command", stderr);
        Assert.Contains("match", stdout + stderr, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------- helpers --

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = LocateToolExecutable(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        // Hermetic sqlite store + unique branch — never touches a real branch or shared store.
        psi.Environment["EDGEHOP_BACKEND"] = "sqlite";
        psi.Environment["EDGEHOP_SQLITE_PATH"] =
            Path.Combine(Path.GetTempPath(), $"edgehop-dispatch-{Guid.NewGuid():N}.db");
        psi.Environment["EDGEHOP_BRANCH"] = $"dispatch-test-{Guid.NewGuid():N}";

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the edgehop tool.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var result = (process.ExitCode, await stdoutTask, await stderrTask);

        var storePath = psi.Environment["EDGEHOP_SQLITE_PATH"]!;
        if (File.Exists(storePath))
        {
            File.Delete(storePath);
        }

        return result;
    }

    private static string LocateToolExecutable()
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
            .Select(c => Path.Combine(
                rootPath, "EdgeHop.Tool", "bin", c, "net10.0", "EdgeHop.Tool.exe"))
            .ToList();

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException(
                "EdgeHop.Tool.exe not found — build the solution first (dotnet build EdgeHop.sln). "
                + $"Looked in: {string.Join("; ", candidates)}");
    }
}
