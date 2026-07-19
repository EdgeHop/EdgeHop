using System.Runtime.CompilerServices;
using EdgeHop.Core;
using EdgeHop.Oxc;
using EdgeHop.Roslyn;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// xUnit collection definition for tests over the JsFixture solution — the regression anchor
/// for cross-tier C#→JS <c>JS_CALLS</c> interop. One shared <see cref="JsMsBuildFixture"/> so
/// the (slow) MSBuild load + oxc run happen once per run. No database.
/// </summary>
[CollectionDefinition(JsMsBuildTestCollection.Name)]
public sealed class JsMsBuildTestCollection : ICollectionFixture<JsMsBuildFixture>
{
    /// <summary>Collection name referenced by <c>[Collection(...)]</c> on test classes.</summary>
    public const string Name = "Js MSBuildWorkspace tests";
}

/// <summary>
/// Shared fixture for <c>fixtures\JsFixture</c>: runs the REAL production pipeline in-process —
/// the Roslyn extractor (C# interop call sites) and the oxc extractor (JS exports) via their
/// <see cref="IExtractor"/> seam, then the host's <see cref="IndexCommand.BuildDesiredGraph"/>
/// (merge + precise JS_CALLS derivation). The only thing skipped versus a real
/// <c>edgehop-extract index</c> is the store write. The vendored <c>edgehop-oxc.exe</c> is
/// deployed next to the test assembly by the EdgeHop.Oxc project reference.
/// </summary>
public sealed class JsMsBuildFixture : IAsyncLifetime
{
    /// <summary>Branch stamped on every extracted row; in-memory only, never written.</summary>
    public const string Branch = "main";

    private ExtractionResult? _graph;
    private Exception? _failure;

    /// <summary>Absolute path of the JsFixture solution file.</summary>
    public string SolutionPath { get; private set; } = string.Empty;

    /// <summary>The merged, JS_CALLS-augmented desired graph (precise mode).</summary>
    public ExtractionResult Graph =>
        _graph
        ?? throw _failure
        ?? new InvalidOperationException("JsMsBuildFixture was not initialized.");

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        FixtureTestSupport.EnsureMsBuildRegistered();
        SolutionPath = FixtureTestSupport.LocateFixtureSolution("JsFixture", "JsFixture.sln");
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

            // The exact host path, precise mode (env-independent for a deterministic anchor).
            _graph = IndexCommand.BuildDesiredGraph(outcomes, Branch, JsInteropMode.Precise);
        }
        catch (Exception ex)
        {
            _failure = ex;
        }
    }
}
