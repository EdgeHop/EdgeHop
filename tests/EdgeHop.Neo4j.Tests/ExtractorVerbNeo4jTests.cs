using System.Diagnostics;
using EdgeHop.Core;
using Neo4j.Driver;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// Phase 6 checkpoint — the Neo4j-backed <c>edgehop-extract prune</c> verb, exercised
/// through the real executable. Seeds and deletes only a throwaway GUID branch. The
/// database-free usage-error tests for the same verb surface live in
/// <c>ExtractorVerbTests</c> in <c>EdgeHop.Tests</c>. Skipped automatically when NEO4J_*
/// environment variables are not set.
/// </summary>
public sealed class ExtractorVerbNeo4jTests
{
    [Neo4jFact]
    public async Task Prune_without_yes_only_reports_and_with_yes_deletes_the_branch()
    {
        var settings = Neo4jSettings.FromEnvironment();
        var driver = GraphDatabase.Driver(settings.Uri, AuthTokens.Basic(settings.User, settings.Password));
        await using (driver.ConfigureAwait(false))
        {
            await Neo4jSchema.ApplyAsync(driver, settings.Database);
            var writer = new Neo4jGraphWriter(driver, settings.Database);
            var snapshot = new Neo4jGraphSnapshotReader(driver, settings.Database);
            var branch = $"test-verb-{Guid.NewGuid():N}";

            try
            {
                await writer.UpsertNodesAsync(new[]
                {
                    new NodeRow(branch, "Method:Verb.A()", "A()", SymbolKinds.Method, "V.cs", "T", false),
                    new NodeRow(branch, "Method:Verb.B()", "B()", SymbolKinds.Method, "V.cs", "T", false),
                });

                // Without --yes: refused (exit 1), counts reported, nothing deleted.
                var refused = await RunExtractorAsync("prune", "--branch", branch);
                Assert.Equal(1, refused.ExitCode);
                Assert.Contains("2 nodes", refused.Stdout);
                Assert.Contains("Nothing was deleted", refused.Stdout);
                Assert.Equal(2, (await snapshot.GetNodeIdsAsync(branch)).Count);

                // With --yes: deleted, exit 0.
                var pruned = await RunExtractorAsync("prune", "--branch", branch, "--yes");
                Assert.Equal(0, pruned.ExitCode);
                Assert.Contains("deleted 2 nodes", pruned.Stdout);
                Assert.Empty(await snapshot.GetNodeIdsAsync(branch));
            }
            finally
            {
                await writer.DeleteBranchAsync(branch);
            }
        }
    }

    // ---------------------------------------------------------------------- helpers --
    // Replicated from ExtractorVerbTests (a private helper there) so the moved prune test
    // stays self-contained without exposing new public API on EdgeHop.Tests.

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
