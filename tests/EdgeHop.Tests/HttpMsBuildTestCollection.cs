using System.Runtime.CompilerServices;
using EdgeHop.Core;
using EdgeHop.Roslyn;
using Microsoft.CodeAnalysis;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// xUnit collection definition for tests over the HttpFixture solution. Mirrors
/// <see cref="BlazorMsBuildTestCollection"/>: one shared <see cref="HttpMsBuildFixture"/>
/// so the fixture is restored, loaded, and extracted exactly once per run. No database.
/// </summary>
[CollectionDefinition(HttpMsBuildTestCollection.Name)]
public sealed class HttpMsBuildTestCollection : ICollectionFixture<HttpMsBuildFixture>
{
    /// <summary>Collection name referenced by <c>[Collection(...)]</c> on test classes.</summary>
    public const string Name = "Http MSBuildWorkspace tests";
}

/// <summary>
/// Shared fixture for the <c>fixtures\HttpFixture</c> solution — the regression anchor
/// for the HTTP pass (HTTP_CALLS edges, endpoint routes stamping, template matching
/// across the Web→Api project boundary). Same lifecycle as
/// <see cref="BlazorMsBuildFixture"/>.
/// </summary>
public sealed class HttpMsBuildFixture : IAsyncLifetime
{
    /// <summary>Branch stamped on every extracted row; in-memory only, never written.</summary>
    public const string Branch = "main";

    private WorkspaceLoadResult? _loadResult;
    private ExtractionResult? _extraction;
    private Exception? _extractionFailure;

    /// <summary>Absolute path of the HttpFixture solution file.</summary>
    public string SolutionPath { get; private set; } = string.Empty;

    /// <summary>The workspace load result (solution + split diagnostics).</summary>
    public WorkspaceLoadResult LoadResult =>
        _loadResult ?? throw new InvalidOperationException("HttpMsBuildFixture was not initialized.");

    /// <summary>The loaded Roslyn solution.</summary>
    public Solution Solution => LoadResult.Solution;

    /// <summary>
    /// The extraction result produced once during fixture initialization; rethrows the
    /// original failure (with workspace diagnostics attached) when extraction failed.
    /// </summary>
    public ExtractionResult Extraction =>
        _extraction
        ?? throw _extractionFailure
        ?? new InvalidOperationException("HttpMsBuildFixture was not initialized.");

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        FixtureTestSupport.EnsureMsBuildRegistered();
        SolutionPath = FixtureTestSupport.LocateFixtureSolution("HttpFixture", "HttpFixture.sln");
        await FixtureTestSupport.RestoreFixtureSolutionAsync(SolutionPath);
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
            _extractionFailure = new InvalidOperationException(
                "SymbolGraphExtractor.ExtractAsync failed against the HttpFixture solution. "
                + $"Workspace Failure diagnostics ({_loadResult.FailureDiagnostics.Count}):"
                + Environment.NewLine
                + string.Join(Environment.NewLine, _loadResult.FailureDiagnostics),
                ex);
        }
    }
}
