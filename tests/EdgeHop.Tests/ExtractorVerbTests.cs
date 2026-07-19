using System.Diagnostics;
using EdgeHop.Core;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// Phase 6 checkpoint — the <c>edgehop-extract</c> verb surface, exercised through the
/// real executable (usage errors need no database; the prune tests seed and delete only
/// a GUID branch).
/// </summary>
public sealed class ExtractorVerbTests
{
    // ------------------------------------------------------------------ usage errors --

    [Fact]
    public async Task No_arguments_prints_usage_and_exits_one()
    {
        var (exitCode, _, stderr) = await RunExtractorAsync();
        Assert.Equal(1, exitCode);
        Assert.Contains("Usage:", stderr);
        Assert.Contains("prune --branch", stderr);
    }

    [Fact]
    public async Task Unknown_option_is_a_usage_error()
    {
        var (exitCode, _, stderr) = await RunExtractorAsync("index", "whatever.sln", "--frobnicate");
        Assert.Equal(1, exitCode);
        Assert.Contains("Unknown option", stderr);
    }

    [Fact]
    public async Task Branch_flag_requires_a_value()
    {
        var (exitCode, _, stderr) = await RunExtractorAsync("index", "whatever.sln", "--branch");
        Assert.Equal(1, exitCode);
        Assert.Contains("--branch requires a value", stderr);
    }

    [Fact]
    public async Task Prune_requires_a_branch()
    {
        var (exitCode, _, stderr) = await RunExtractorAsync("prune");
        Assert.Equal(1, exitCode);
        Assert.Contains("--branch", stderr);
    }

    [Fact]
    public async Task Missing_solution_file_is_reported()
    {
        var (exitCode, _, stderr) = await RunExtractorAsync("index", "no-such-solution.sln", "--dry-run");
        Assert.Equal(1, exitCode);
        Assert.Contains("not found", stderr);
    }

    // ---------------------------------------------------------------------- helpers --

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunExtractorAsync(
        params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = LocateExtractorExecutable(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        // This class tests the verbs over Neo4j; since Handoff 3 the default backend is
        // sqlite, so the child is pinned explicitly.
        psi.Environment["EDGEHOP_BACKEND"] = "neo4j";

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start edgehop-extract.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string LocateExtractorExecutable()
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
                rootPath, "EdgeHop.Indexer", "bin", c, "net10.0", "edgehop-extract.exe"))
            .ToList();

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException(
                "edgehop-extract.exe not found — build the solution first (dotnet build EdgeHop.sln). "
                + $"Looked in: {string.Join("; ", candidates)}");
    }
}
