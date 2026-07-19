using System.Runtime.CompilerServices;
using EdgeHop.Core;
using EdgeHop.Oxc;
using EdgeHop.Roslyn;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// xUnit collection for the <c>EdgeHopExplorer.BlazorServer</c> sample — the comprehensive,
/// self-documenting regression anchor that exercises every node kind and every edge type from a
/// single Blazor Server tier (advanced C#, components, minimal-API/HttpClient HTTP_CALLS, and
/// bidirectional JS interop). One shared fixture so the MSBuild load + oxc run happen once per run.
/// </summary>
[CollectionDefinition(SampleBlazorMsBuildTestCollection.Name)]
public sealed class SampleBlazorMsBuildTestCollection : ICollectionFixture<SampleBlazorMsBuildFixture>
{
    /// <summary>Collection name referenced by <c>[Collection(...)]</c> on the test class.</summary>
    public const string Name = "Sample Blazor MSBuildWorkspace tests";
}

/// <summary>
/// Loads <c>tests/samples/EdgeHopExplorer.BlazorServer</c> and runs the REAL production pipeline
/// in-process: the Roslyn extractor (C#/Razor, HTTP + interop passes) and the oxc extractor
/// (JS/inline-script) via their <see cref="IExtractor"/> seam, merged and matched by the host's
/// <see cref="IndexCommand.BuildDesiredGraph"/> in the default <see cref="JsInteropMode.Precise"/>
/// mode. The only thing skipped versus a real <c>edgehop-extract index</c> is the store write.
/// </summary>
public sealed class SampleBlazorMsBuildFixture : IAsyncLifetime
{
    /// <summary>Branch stamped on every extracted row; in-memory only, never written to a store.</summary>
    public const string Branch = "main";

    private ExtractionResult? _graph;
    private Exception? _failure;

    /// <summary>Absolute path of the sample solution file.</summary>
    public string SolutionPath { get; private set; } = string.Empty;

    /// <summary>The merged, interop-augmented desired graph (precise mode).</summary>
    public ExtractionResult Graph =>
        _graph
        ?? throw _failure
        ?? new InvalidOperationException("SampleBlazorMsBuildFixture was not initialized.");

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        FixtureTestSupport.EnsureMsBuildRegistered();
        SolutionPath = FixtureTestSupport.LocateSampleSolution(
            "EdgeHopExplorer.BlazorServer", "EdgeHopExplorer.BlazorServer.sln");
        await FixtureTestSupport.RestoreFixtureSolutionAsync(SolutionPath);
        await LoadAndExtractAsync();
    }

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task LoadAndExtractAsync()
    {
        try
        {
            var solutionDir = Path.GetDirectoryName(Path.GetFullPath(SolutionPath));
            var request = new ExtractionRequest(SolutionPath, Branch, solutionDir);

            await using var roslyn = new RoslynExtractor();
            await using var oxc = new OxcExtractor();
            var outcomes = new List<ExtractionOutcome>
            {
                await roslyn.ExtractAsync(request),
                await oxc.ExtractAsync(request),
            };

            var failures = outcomes.SelectMany(o => o.FailureDiagnostics).ToList();
            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    "Extraction produced Failure diagnostics: " + Environment.NewLine
                    + string.Join(Environment.NewLine, failures));
            }

            _graph = IndexCommand.BuildDesiredGraph(outcomes, Branch, JsInteropMode.Precise);
        }
        catch (Exception ex)
        {
            _failure = ex;
        }
    }
}
