using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace EdgeHop.Roslyn;

/// <summary>
/// The result of opening a solution with <see cref="WorkspaceLoader.OpenSolutionAsync"/>.
/// </summary>
/// <param name="Solution">The loaded solution snapshot.</param>
/// <param name="FailureDiagnostics">
/// Every <see cref="WorkspaceDiagnosticKind.Failure"/>-level <c>WorkspaceFailed</c>
/// diagnostic raised during the load. Any entry here means the graph would be silently
/// incomplete — the README treats this as a hard stop for phases 1–3.
/// </param>
/// <param name="WarningDiagnostics">
/// Every <see cref="WorkspaceDiagnosticKind.Warning"/>-level diagnostic raised during the
/// load. Logged for the developer but not fatal.
/// </param>
public sealed record WorkspaceLoadResult(
    Solution Solution,
    IReadOnlyList<string> FailureDiagnostics,
    IReadOnlyList<string> WarningDiagnostics);

/// <summary>
/// Opens a solution via <see cref="MSBuildWorkspace"/>, capturing every
/// <c>WorkspaceFailed</c> diagnostic — MSBuild load failures are the #1 source of a
/// silently-incomplete graph (README).
/// </summary>
/// <remarks>
/// This class references <c>MSBuildWorkspace</c>, so it must never be JIT-resolved before
/// <see cref="MsBuildBootstrap.EnsureRegistered"/> has run. This loader deliberately does
/// NOT register the locator itself: registration is the very first statement of
/// <c>Program.Main</c>, which never references this class directly (see the README
/// "MSBuild gotcha" and <see cref="MsBuildBootstrap"/>).
/// </remarks>
public static class WorkspaceLoader
{
    /// <summary>
    /// Opens <paramref name="solutionPath"/> and returns the solution together with all
    /// Failure- and Warning-level workspace diagnostics, split by kind.
    /// </summary>
    /// <param name="solutionPath">Path to the <c>.sln</c> file.</param>
    /// <param name="log">
    /// Optional sink invoked once per <c>WorkspaceFailed</c> diagnostic as it arrives
    /// (formatted <c>"[Kind] message"</c>), so failures are visible even mid-load.
    /// </param>
    /// <remarks>
    /// <para>
    /// The <c>WorkspaceFailed</c> subscription is attached <b>before</b>
    /// <see cref="MSBuildWorkspace.OpenSolutionAsync(string, System.IProgress{ProjectLoadProgress}, System.Threading.CancellationToken)"/>
    /// is awaited, so no diagnostic raised during the load can be missed.
    /// </para>
    /// <para>
    /// The workspace is intentionally not disposed here: the returned
    /// <see cref="Solution"/> snapshot still produces compilations lazily through the
    /// workspace's services and build hosts. The extractor is a run-to-exit console tool,
    /// so the workspace simply lives for the remainder of the process.
    /// </para>
    /// </remarks>
    public static async Task<WorkspaceLoadResult> OpenSolutionAsync(
        string solutionPath,
        Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionPath);

        var failures = new List<string>();
        var warnings = new List<string>();
        var gate = new object();

        // NuGetAudit=false: package-vulnerability audit belongs to the target solution's
        // own build/CI, not to this indexing-time design-time build. With audit on, a
        // dependency CVE (e.g. a stale package on an older branch) surfaces as Failure
        // diagnostics and hard-stops indexing of code that loads perfectly fine.
        var workspace = MSBuildWorkspace.Create(new Dictionary<string, string>
        {
            ["NuGetAudit"] = "false",
        });

        // Subscribe BEFORE OpenSolutionAsync so every diagnostic is captured. The event
        // can fire on arbitrary threads during the load, hence the lock around the lists.
        workspace.WorkspaceFailed += (_, e) =>
        {
            var line = $"[{e.Diagnostic.Kind}] {e.Diagnostic.Message}";
            lock (gate)
            {
                if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                {
                    failures.Add(line);
                }
                else
                {
                    warnings.Add(line);
                }
            }

            log?.Invoke(line);
        };

        var solution = await workspace.OpenSolutionAsync(solutionPath).ConfigureAwait(false);

        lock (gate)
        {
            return new WorkspaceLoadResult(solution, failures.ToArray(), warnings.ToArray());
        }
    }
}
