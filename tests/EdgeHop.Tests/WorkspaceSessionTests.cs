using EdgeHop.Core;
using EdgeHop.Roslyn;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// Pins the in-memory watch-cycle workspace refresh (<see cref="WorkspaceSession"/>):
/// a batch of modified known documents refreshes the kept solution without reloading
/// MSBuild, membership changes (new/deleted files) fall back to a full load, and a
/// refreshed <c>.razor</c> AdditionalDocument re-runs the Razor source generator so the
/// extracted graph reflects the edit. Each test copies its fixture solution to a temp
/// directory — the checked-in fixtures are never mutated.
/// </summary>
public sealed class WorkspaceSessionTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public WorkspaceSessionTests() => FixtureTestSupport.EnsureMsBuildRegistered();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; MSBuild build hosts may briefly hold handles.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task Modified_known_document_refreshes_in_memory_and_reextracts()
    {
        var sln = await CopyAndRestoreFixtureAsync("TinyFixture", "TinyFixture.sln");
        var session = new WorkspaceSession();

        var initial = await session.LoadOrRefreshAsync(sln, null);
        Assert.Empty(initial.FailureDiagnostics);
        Assert.Equal("load", session.LastCycleDescription);
        var baseline = await SymbolGraphExtractor.ExtractAsync(initial.Solution, "main");
        Assert.DoesNotContain(baseline.Nodes, n => n.Name == "int Greeter.Extra()");

        // Edit an existing document on disk, then refresh through the session.
        var greeter = Path.Combine(Path.GetDirectoryName(sln)!, "Greeter.cs");
        File.WriteAllText(greeter, """
            namespace TinyFixture;

            public class Greeter : IGreeter
            {
                public virtual string Greet(string name) => "Hello, " + name;

                public int Extra() => 1;
            }
            """);
        var refreshed = await session.LoadOrRefreshAsync(sln, [greeter]);
        Assert.StartsWith("refresh (1 doc", session.LastCycleDescription, StringComparison.Ordinal);
        Assert.Empty(refreshed.FailureDiagnostics);

        var extraction = await SymbolGraphExtractor.ExtractAsync(refreshed.Solution, "main");
        Assert.Contains(extraction.Nodes, n => n.Name == "int Greeter.Extra()");
        Assert.Equal(baseline.Nodes.Count + 1, extraction.Nodes.Count);
    }

    [Fact]
    public async Task Membership_changes_fall_back_to_a_full_load()
    {
        var sln = await CopyAndRestoreFixtureAsync("TinyFixture", "TinyFixture.sln");
        var session = new WorkspaceSession();
        await session.LoadOrRefreshAsync(sln, null);

        // A NEW file is not a known document → the session must full-load, and the
        // reload must pick the file up (it is on disk before the cycle).
        var newFile = Path.Combine(Path.GetDirectoryName(sln)!, "Newcomer.cs");
        File.WriteAllText(newFile, """
            namespace TinyFixture;

            public class Newcomer
            {
            }
            """);
        var reloaded = await session.LoadOrRefreshAsync(sln, [newFile]);
        Assert.Equal("load", session.LastCycleDescription);
        var extraction = await SymbolGraphExtractor.ExtractAsync(reloaded.Solution, "main");
        Assert.Contains(extraction.Nodes, n => n.Name == "Newcomer");

        // A batch naming a since-deleted path must also full-load.
        File.Delete(newFile);
        await session.LoadOrRefreshAsync(sln, [newFile]);
        Assert.Equal("load", session.LastCycleDescription);

        // A null changedPaths (how the watch loop represents an overflow) must full-load.
        await session.LoadOrRefreshAsync(sln, null);
        Assert.Equal("load", session.LastCycleDescription);
    }

    [Fact]
    public async Task Razor_document_refresh_reruns_the_generator()
    {
        var sln = await CopyAndRestoreFixtureAsync("BlazorFixture", "BlazorFixture.sln");
        var session = new WorkspaceSession();

        var initial = await session.LoadOrRefreshAsync(sln, null);
        Assert.Empty(initial.FailureDiagnostics);
        var baseline = await SymbolGraphExtractor.ExtractAsync(initial.Solution, "main");
        var home = Assert.Single(baseline.Nodes, n => n.Kind == SymbolKinds.NamedType && n.Name == "Home");
        Assert.Equal(new[] { "/" }, home.Routes);

        // Add a second @page directive: routes come from the GENERATED [Route]
        // attributes, so seeing it after a refresh proves WithAdditionalDocumentText
        // invalidated and re-ran the Razor source generator.
        var homeRazor = Path.Combine(Path.GetDirectoryName(sln)!, "Pages", "Home.razor");
        File.WriteAllText(homeRazor, "@page \"/\"\n@page \"/extra\"\n" + string.Join(
            '\n', File.ReadAllLines(homeRazor).Skip(1)));
        var refreshed = await session.LoadOrRefreshAsync(sln, [homeRazor]);
        Assert.StartsWith("refresh (1 doc", session.LastCycleDescription, StringComparison.Ordinal);

        var extraction = await SymbolGraphExtractor.ExtractAsync(refreshed.Solution, "main");
        var updated = Assert.Single(extraction.Nodes, n => n.Kind == SymbolKinds.NamedType && n.Name == "Home");
        Assert.Equal(new[] { "/", "/extra" }, updated.Routes);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    /// <summary>Copies <c>fixtures\{name}</c> (sans build artifacts) to a temp directory
    /// and restores it there, returning the temp solution path.</summary>
    private async Task<string> CopyAndRestoreFixtureAsync(string fixtureDirectory, string solutionFileName)
    {
        var source = Path.GetDirectoryName(
            FixtureTestSupport.LocateFixtureSolution(fixtureDirectory, solutionFileName))!;
        var target = Path.Combine(
            Path.GetTempPath(), $"edgehop-session-{fixtureDirectory}-{Guid.NewGuid():N}");
        _tempDirs.Add(target);
        CopyTree(source, target);

        var sln = Path.Combine(target, solutionFileName);
        await FixtureTestSupport.RestoreFixtureSolutionAsync(sln);
        return sln;
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (name.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || name.Equals("bin", StringComparison.OrdinalIgnoreCase))
            {
                continue; // build artifacts reference absolute paths; restore recreates them.
            }

            CopyTree(dir, Path.Combine(target, name));
        }
    }
}
