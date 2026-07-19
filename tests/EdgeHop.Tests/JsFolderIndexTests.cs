using System.Diagnostics;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// End-to-end proof of the directory-index entry point through the REAL executables: index a
/// bare JS/TS folder (no <c>.sln</c>) with <c>edgehop-extract index &lt;dir&gt;</c>, then read a
/// JS symbol back out with the <c>edgehop</c> query CLI — the exact "code-graph a non-.NET
/// project" flow. Hermetic and rule-abiding: the SQLite backend writes to a throwaway store file
/// (<c>EDGEHOP_SQLITE_PATH</c>) under a unique GUID branch (<c>EDGEHOP_BRANCH</c>), so no real
/// store or branch is ever touched; the temp store is deleted on the way out.
/// </summary>
public sealed class JsFolderIndexTests : IDisposable
{
    private readonly string _branch = "test-" + Guid.NewGuid().ToString("N");
    private readonly string _storeDir = Path.Combine(
        Path.GetTempPath(), "edgehop-jsfolder-" + Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_storeDir, "store.db");

    public JsFolderIndexTests() => Directory.CreateDirectory(_storeDir);

    [Fact]
    public async Task Index_of_a_bare_js_directory_is_queryable_end_to_end()
    {
        var fixtureDir = FixtureTestSupport.LocateFixtureDirectory("JsFolderFixture");

        // 1. Index the FOLDER (no solution anywhere) — the new entry point.
        var index = await RunAsync(
            LocateExecutable("EdgeHop.Indexer", "edgehop-extract.exe"),
            "index", fixtureDir);

        Assert.True(index.ExitCode == 0,
            $"index exited {index.ExitCode}.{Environment.NewLine}STDOUT:{index.Stdout}{Environment.NewLine}STDERR:{index.Stderr}");
        // The Roslyn C# extractor no-ops for a directory target; oxc graphs the tree.
        Assert.Contains("no solution file for this target", index.Stderr);
        Assert.Contains("upserted 5 nodes / 4 edges", index.Stdout);

        // 2. Query the same store with the edgehop CLI — the JS symbol is found.
        var query = await RunAsync(
            LocateExecutable("EdgeHop.Cli", "edgehop.exe"),
            "find-symbol", "greet", "--json");

        Assert.True(query.ExitCode == 0,
            $"find-symbol exited {query.ExitCode}.{Environment.NewLine}STDOUT:{query.Stdout}{Environment.NewLine}STDERR:{query.Stderr}");
        Assert.Contains("greet", query.Stdout);
        // JS ids carry the tier tag — proves the hit came from the folder-indexed JS graph.
        Assert.Contains("js|", query.Stdout);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_storeDir))
            {
                Directory.Delete(_storeDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // A transient WAL handle can linger a moment; a leftover temp file is harmless.
        }
    }

    // ---------------------------------------------------------------------- helpers --

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string exe, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        // Hermetic: explicit sqlite store file + GUID branch, both shared by the two child
        // processes so the query reads exactly what the index wrote — and nothing real is touched.
        psi.Environment["EDGEHOP_BACKEND"] = "sqlite";
        psi.Environment["EDGEHOP_SQLITE_PATH"] = StorePath;
        psi.Environment["EDGEHOP_BRANCH"] = _branch;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{exe}'.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    /// <summary>Locates a built EdgeHop executable by walking up to <c>EdgeHop.sln</c>, then
    /// into <c>{project}\bin\{config}\net10.0\{exe}</c> (same pattern as the verb tests).</summary>
    private static string LocateExecutable(string project, string exeName)
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
            .Select(c => Path.Combine(rootPath, project, "bin", c, "net10.0", exeName))
            .ToList();

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException(
                $"{exeName} not found — build the solution first (dotnet build EdgeHop.sln). "
                + $"Looked in: {string.Join("; ", candidates)}");
    }
}
