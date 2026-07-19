using System.Runtime.CompilerServices;
using EdgeHop.Core;
using EdgeHop.Roslyn;
using Microsoft.CodeAnalysis;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// xUnit collection definition for tests that run <c>MSBuildWorkspace</c> in-process.
/// Every test class in this collection shares a single <see cref="MsBuildFixture"/>
/// instance, so the MSBuild locator is registered, the fixture solution restored,
/// loaded, and extracted exactly once per test run. These tests need no Neo4j and
/// must never touch it.
/// </summary>
[CollectionDefinition(MsBuildTestCollection.Name)]
public sealed class MsBuildTestCollection : ICollectionFixture<MsBuildFixture>
{
    /// <summary>Collection name referenced by <c>[Collection(...)]</c> on test classes.</summary>
    public const string Name = "MSBuildWorkspace tests";
}

/// <summary>
/// Shared fixture for MSBuildWorkspace-based tests. On initialization it:
/// <list type="number">
/// <item><description>Calls <c>MsBuildBootstrap.EnsureRegistered()</c> exactly once per
/// process (guarded here with an interlocked flag on top of the bootstrap's own
/// <c>MSBuildLocator.IsRegistered</c> guard) — and does so <b>before</b> any method that
/// references workspace types executes, per the MSBuild JIT-ordering gotcha.</description></item>
/// <item><description>Locates <c>fixtures\TinyFixture\TinyFixture.sln</c> by walking up
/// from <see cref="AppContext.BaseDirectory"/> to the <c>EdgeHop.Tests</c> project
/// directory.</description></item>
/// <item><description>Runs <c>dotnet restore</c> on the fixture solution (asserting exit
/// code 0) so MSBuildWorkspace can design-time-build it.</description></item>
/// <item><description>Opens the solution via <c>WorkspaceLoader</c> and runs
/// <c>SymbolGraphExtractor</c> once with branch <see cref="Branch"/>, caching both
/// results for all tests in the collection.</description></item>
/// </list>
/// </summary>
public sealed class MsBuildFixture : IAsyncLifetime
{
    /// <summary>Branch stamped on every extracted row — "main", mirroring production.</summary>
    public const string Branch = "main";

    private WorkspaceLoadResult? _loadResult;
    private ExtractionResult? _extraction;
    private Exception? _extractionFailure;

    /// <summary>Absolute path of the TinyFixture solution file.</summary>
    public string SolutionPath { get; private set; } = string.Empty;

    /// <summary>The workspace load result (solution + split diagnostics).</summary>
    public WorkspaceLoadResult LoadResult =>
        _loadResult ?? throw new InvalidOperationException("MsBuildFixture was not initialized.");

    /// <summary>The loaded Roslyn solution.</summary>
    public Solution Solution => LoadResult.Solution;

    /// <summary>
    /// The extraction result produced once during fixture initialization. If extraction
    /// threw, the failure (with workspace diagnostics attached) is rethrown here so each
    /// dependent test reports the root cause instead of a null reference.
    /// </summary>
    public ExtractionResult Extraction =>
        _extraction
        ?? throw _extractionFailure
        ?? new InvalidOperationException("MsBuildFixture was not initialized.");

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        // (a) Register the MSBuild locator exactly once, before anything that touches
        // workspace types runs.
        FixtureTestSupport.EnsureMsBuildRegistered();

        // (b) Locate the fixture solution relative to the EdgeHop.Tests project directory.
        SolutionPath = FixtureTestSupport.LocateFixtureSolution("TinyFixture", "TinyFixture.sln");

        // (c) Restore it so the design-time build has assets on disk.
        await FixtureTestSupport.RestoreFixtureSolutionAsync(SolutionPath);

        // Load + extract behind a separate non-inlined method so no MSBuild-workspace
        // type token is resolved before RegisterDefaults() has run (see the MSBuild gotcha in README).
        await LoadAndExtractAsync();
    }

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task LoadAndExtractAsync()
    {
        _loadResult = await WorkspaceLoader.OpenSolutionAsync(SolutionPath);

        try
        {
            _extraction = await SymbolGraphExtractor.ExtractAsync(_loadResult.Solution, Branch);
        }
        catch (Exception ex)
        {
            // Keep the diagnostics test runnable and give every dependent test the
            // real failure plus whatever the workspace reported.
            _extractionFailure = new InvalidOperationException(
                "SymbolGraphExtractor.ExtractAsync failed against the TinyFixture solution. "
                + $"Workspace Failure diagnostics ({_loadResult.FailureDiagnostics.Count}):"
                + Environment.NewLine
                + string.Join(Environment.NewLine, _loadResult.FailureDiagnostics),
                ex);
        }
    }

}
