using System.Diagnostics;
using EdgeHop.Core;

namespace EdgeHop.Roslyn;

/// <summary>
/// The Roslyn C#/Razor source extractor behind the <see cref="IExtractor"/> seam: loads (or,
/// in watch mode, refreshes) the solution via <see cref="WorkspaceSession"/> and walks it with
/// <see cref="SymbolGraphExtractor"/>. Stateful across watch cycles — the kept session enables
/// the in-memory refresh — so the indexer host holds one instance for the duration of a watch.
/// </summary>
/// <remarks>
/// <b>MSBuild JIT-ordering rule (README "MSBuild gotcha").</b> This method registers the MSBuild
/// locator (<see cref="MsBuildBootstrap.EnsureRegistered"/>) as its FIRST statement, before any
/// <c>MSBuildWorkspace</c> type is resolved. Its own body references no <c>MSBuildWorkspace</c>
/// type (only <see cref="WorkspaceSession"/> / <see cref="SymbolGraphExtractor"/>, whose
/// signatures use <c>Solution</c>, not <c>MSBuildWorkspace</c>); <see cref="WorkspaceLoader"/> —
/// the only type touching <c>MSBuildWorkspace</c> — JIT-compiles when the session calls it,
/// which is after registration. The host never touches MSBuild at all.
/// </remarks>
public sealed class RoslynExtractor : IExtractor
{
    private readonly WorkspaceSession _session = new();

    public string Name => "roslyn";

    public async Task<ExtractionOutcome> ExtractAsync(
        ExtractionRequest request, Action<string>? log = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // No solution file → a bare-directory (non-.NET) index target: the C# extractor has
        // nothing to load, so it contributes an empty result. Returning BEFORE
        // MsBuildBootstrap.EnsureRegistered means a pure JS/TS index never even loads MSBuild.
        if (request.SolutionPath is null)
        {
            log?.Invoke("roslyn: no solution file for this target; skipping C#/Razor extraction.");
            return new ExtractionOutcome(
                new ExtractionResult([], []),
                FailureDiagnostics: [],
                WarningDiagnostics: [],
                LoadDescription: "roslyn (no solution)",
                LoadMs: 0,
                ExtractMs: 0);
        }

        // FIRST — before WorkspaceLoader (and thus MSBuildWorkspace) can JIT-resolve.
        MsBuildBootstrap.EnsureRegistered();

        var stopwatch = Stopwatch.StartNew();
        var load = await _session
            .LoadOrRefreshAsync(request.SolutionPath, request.ChangedPaths, log)
            .ConfigureAwait(false);
        var loadMs = stopwatch.ElapsedMilliseconds;
        var loadKind = _session.LastCycleDescription;

        if (load.FailureDiagnostics.Count > 0)
        {
            // Surface the failure to the host, which hard-stops uniformly (exit code 2). No
            // extraction runs against a partially-loaded solution.
            return new ExtractionOutcome(
                new ExtractionResult([], []),
                load.FailureDiagnostics,
                load.WarningDiagnostics,
                loadKind,
                loadMs,
                ExtractMs: 0);
        }

        stopwatch.Restart();
        var result = await SymbolGraphExtractor
            .ExtractAsync(load.Solution, request.Branch, log)
            .ConfigureAwait(false);
        var extractMs = stopwatch.ElapsedMilliseconds;

        return new ExtractionOutcome(
            result, [], load.WarningDiagnostics, loadKind, loadMs, extractMs);
    }

    /// <summary>Nothing to release: the session holds only a <c>Solution</c> snapshot, whose
    /// originating workspace lives for the process (the one-shot tool never disposed it either).</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
