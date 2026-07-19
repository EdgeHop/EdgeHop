using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace EdgeHop.Roslyn;

/// <summary>
/// Keeps the loaded <see cref="Solution"/> alive across watch cycles so a batch of
/// modified files refreshes in memory (<see cref="Solution.WithDocumentText"/> /
/// <see cref="Solution.WithAdditionalDocumentText"/>) instead of re-running the full
/// <c>MSBuildWorkspace.OpenSolutionAsync</c> — the dominant cost of a warm cycle on a
/// real solution (README: ~½–2 min full-solution load vs seconds of extract).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>
/// <b>Refresh applies ONLY when a non-null <c>changedPaths</c> is given AND every path maps
/// to a document the solution already knows and still exists on disk.</b> A null
/// <c>changedPaths</c> (one-shot run, initial watch cycle, branch switch, watcher overflow)
/// or anything else — created/deleted/renamed files, an unreadable file — falls back to a
/// full reload. A <c>.razor</c> change refreshes the matching AdditionalDocument, which
/// invalidates the Razor source generator so the next <c>GetCompilationAsync</c> regenerates
/// the component code (pinned by <c>WorkspaceSessionTests</c>).
/// </description></item>
/// <item><description>
/// <b>Extraction stays whole-solution either way.</b> The refresh replaces the LOAD, not
/// the extract: the reconciler's whole-solution diff remains correct by construction
/// (per-document surgical deletes were rejected — README "reconcile-by-diff").
/// </description></item>
/// <item><description>
/// Holds only the <see cref="Solution"/> snapshot — solutions keep their originating
/// workspace's services alive, so no <c>MSBuildWorkspace</c> type is referenced here and
/// the <see cref="MsBuildBootstrap"/> JIT-ordering rule cannot be violated by this class.
/// A full reload simply drops the old snapshot (same never-dispose lifecycle the
/// one-shot tool always had, but one live workspace instead of one per cycle).
/// </description></item>
/// </list>
/// </remarks>
public sealed class WorkspaceSession
{
    private Solution? _solution;

    /// <summary>How the last <see cref="LoadOrRefreshAsync"/> obtained its solution:
    /// <c>"load"</c> or <c>"refresh (N doc(s))"</c>. Feeds the cycle timing line.</summary>
    public string LastCycleDescription { get; private set; } = "load";

    /// <summary>
    /// Returns the solution for this cycle: an in-memory refresh of the kept snapshot when
    /// <paramref name="changedPaths"/> is a non-empty set of known, still-existing documents,
    /// else a fresh <see cref="WorkspaceLoader"/> load. Refresh returns no diagnostics — the
    /// snapshot's original load already passed the caller's Failure gate, and no MSBuild
    /// evaluation happens on this path.
    /// </summary>
    /// <param name="solutionPath">The <c>.sln</c> to (re)load on the full-load path.</param>
    /// <param name="changedPaths">The full paths a watch batch touched, or <c>null</c> to force
    /// a full reload (one-shot runs pass null).</param>
    /// <param name="log">Optional progress sink.</param>
    public async Task<WorkspaceLoadResult> LoadOrRefreshAsync(
        string solutionPath,
        IReadOnlyList<string>? changedPaths,
        Action<string>? log = null)
    {
        if (_solution is not null
            && changedPaths is { Count: > 0 }
            && TryRefresh(_solution, changedPaths) is var (refreshed, docCount)
            && refreshed is not null)
        {
            _solution = refreshed;
            LastCycleDescription = $"refresh ({docCount} doc(s))";
            log?.Invoke($"Workspace: in-memory refresh of {docCount} document(s).");
            return new WorkspaceLoadResult(refreshed, [], []);
        }

        var load = await WorkspaceLoader.OpenSolutionAsync(solutionPath, log).ConfigureAwait(false);
        // Keep the snapshot only when the load passed the Failure gate the caller
        // enforces — a partially-loaded solution must not become the refresh baseline.
        _solution = load.FailureDiagnostics.Count == 0 ? load.Solution : null;
        LastCycleDescription = "load";
        return load;
    }

    /// <summary>
    /// Applies every changed path as an in-memory text update, or returns a null solution
    /// when any path cannot be applied that way (unknown to the solution, deleted,
    /// unreadable) — the caller then falls back to a full reload.
    /// </summary>
    private static (Solution?, int) TryRefresh(Solution solution, IReadOnlyList<string> paths)
    {
        var docCount = 0;
        foreach (var path in paths)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
            {
                return (null, 0);
            }

            if (!File.Exists(fullPath))
            {
                return (null, 0); // deleted or renamed away: membership changed.
            }

            var documentIds = solution.GetDocumentIdsWithFilePath(fullPath);
            if (documentIds.IsDefaultOrEmpty)
            {
                return (null, 0); // new file or non-document (e.g. the HEAD marker).
            }

            SourceText text;
            try
            {
                using var stream = File.OpenRead(fullPath);
                text = SourceText.From(stream);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return (null, 0); // locked mid-save or unreadable: let the reload retry.
            }

            foreach (var id in documentIds)
            {
                if (solution.GetDocument(id) is not null)
                {
                    solution = solution.WithDocumentText(id, text);
                }
                else if (solution.GetAdditionalDocument(id) is not null)
                {
                    // .razor files live here; this invalidates the Razor generator.
                    solution = solution.WithAdditionalDocumentText(id, text);
                }
                else if (solution.GetAnalyzerConfigDocument(id) is not null)
                {
                    solution = solution.WithAnalyzerConfigDocumentText(id, text);
                }
                else
                {
                    return (null, 0); // unknown document kind: be conservative.
                }
            }

            docCount++;
        }

        return docCount > 0 ? (solution, docCount) : (null, 0);
    }
}
