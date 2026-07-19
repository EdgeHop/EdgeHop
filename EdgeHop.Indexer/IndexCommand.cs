using System.Diagnostics;
using EdgeHop.Core;

namespace EdgeHop.Roslyn;

/// <summary>Options for one <c>index</c> run (parsed by <see cref="ExtractorApp"/>).</summary>
/// <param name="SolutionPath">The index target: a <c>.sln</c> file, or a project DIRECTORY for
/// a non-.NET (e.g. pure JS/TS) tree that has no solution. <see cref="IndexCommand.RunOnceAsync"/>
/// resolves which it is at run time (directory → no C# extraction, root is the directory itself).</param>
/// <param name="ExplicitBranch">A user-supplied <c>--branch</c>, or null to resolve via
/// <see cref="BranchResolver"/> (env vars, then git detection from the solution directory,
/// then <c>"main"</c>).</param>
/// <param name="DryRun">Extract and plan only; the store is never written.</param>
/// <param name="AllowEmpty">Permit reconciling an empty desired set against a non-empty
/// branch (the deliberate way to empty a branch from an emptied solution).</param>
/// <param name="StoreHint">Path hint for the sqlite store-per-solution derivation when
/// it must differ from <paramref name="SolutionPath"/>'s directory — set by the
/// worktree route to the ORIGINAL solution directory, so a branch indexed from a
/// private worktree still lands in the main repo's store (a worktree's own path is
/// per-branch and would derive the wrong store). Null means "use the solution
/// directory".</param>
public sealed record IndexOptions(
    string SolutionPath,
    string? ExplicitBranch = null,
    bool DryRun = false,
    bool AllowEmpty = false,
    string? StoreHint = null);

/// <summary>
/// The load → extract → reconcile pipeline behind the <c>index</c> verb (and each watch
/// cycle). One call = one complete, self-consistent indexing of the solution's current
/// on-disk state into the resolved branch: every loaded extractor (Roslyn, oxc, …) is run,
/// their rows are merged into one desired set, everything desired is upserted and everything
/// stale is surgically deleted (see <see cref="GraphReconciler"/>). Extractors and the store
/// are both obtained by reflection (<see cref="ExtractorFactory"/> / <see cref="GraphStoreFactory"/>),
/// so this host references neither MSBuild nor any store driver.
/// </summary>
public static class IndexCommand
{
    /// <summary>
    /// Runs one indexing cycle.
    /// </summary>
    /// <param name="options">The parsed index options.</param>
    /// <param name="output">Stdout sink.</param>
    /// <param name="error">Stderr sink.</param>
    /// <param name="extractors">Extractors kept alive across watch cycles: when non-null the
    /// caller owns their lifetime (and their in-memory state powers the refresh); when null a
    /// one-shot set is loaded via <see cref="ExtractorFactory"/> and disposed before return.</param>
    /// <param name="changedPaths">Watch hint forwarded to every extractor (null = full extraction).</param>
    public static async Task<int> RunOnceAsync(
        IndexOptions options,
        TextWriter output,
        TextWriter error,
        IReadOnlyList<IExtractor>? extractors = null,
        IReadOnlyList<string>? changedPaths = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The index target is either a .sln FILE (C# + JS via all extractors) or a bare
        // DIRECTORY (a non-.NET tree — e.g. a pure JS/TS project — extracted by whichever
        // extractors work off the repo root; the Roslyn C# extractor no-ops with no solution).
        var fullTarget = Path.GetFullPath(options.SolutionPath);
        var isDirectory = Directory.Exists(fullTarget);
        if (!isDirectory && !File.Exists(fullTarget))
        {
            error.WriteLine(
                $"Index target not found (expected a .sln file or a project directory): {options.SolutionPath}");
            return ExtractorApp.ExitUsageOrConfigError;
        }

        // Directory mode has no solution file and the target itself is the root; solution mode
        // keeps the .sln and derives the root from its containing directory.
        var solutionFile = isDirectory ? null : fullTarget;
        var solutionDir = isDirectory ? fullTarget : Path.GetDirectoryName(fullTarget);
        var storeHint = options.StoreHint ?? solutionDir;
        var branch = BranchResolver.Resolve(options.ExplicitBranch, solutionDir);
        error.WriteLine($"Branch: '{branch}'"
            + (options.ExplicitBranch is null ? " (resolved)" : " (explicit)"));

        // Obtain the extractor set (reflection-loaded when the caller did not supply one).
        var ownExtractors = extractors is null;
        IReadOnlyList<IExtractor> exs;
        try
        {
            exs = extractors ?? ExtractorFactory.LoadAll(error.WriteLine);
        }
        catch (InvalidOperationException ex)
        {
            error.WriteLine(ex.Message);
            return ExtractorApp.ExitUsageOrConfigError;
        }

        try
        {
            // ---- Extract with every loaded extractor; each owns its own source loading. ----
            var request = new ExtractionRequest(solutionFile, branch, solutionDir, changedPaths);
            var outcomes = new List<ExtractionOutcome>(exs.Count);
            try
            {
                foreach (var extractor in exs)
                {
                    outcomes.Add(await extractor.ExtractAsync(request, error.WriteLine).ConfigureAwait(false));
                }
            }
            catch (Exception ex)
            {
                error.WriteLine($"Failed to extract from '{options.SolutionPath}': {ex.Message}");
                return ExtractorApp.ExitUsageOrConfigError;
            }

            var loadMs = outcomes.Sum(o => o.LoadMs);
            var extractMs = outcomes.Sum(o => o.ExtractMs);
            var loadKind = outcomes.Count == 1 ? outcomes[0].LoadDescription : "load";

            var failures = outcomes.Sum(o => o.FailureDiagnostics.Count);
            if (failures > 0)
            {
                error.WriteLine(
                    $"{failures} workspace Failure diagnostic(s) (listed above). " +
                    "A graph built from a partially-loaded solution cannot be trusted — aborting " +
                    "(README phase 2 hard stop). Fix the load and re-run.");
                return ExtractorApp.ExitWorkspaceFailure;
            }

            var warnings = outcomes.Sum(o => o.WarningDiagnostics.Count);
            if (warnings > 0)
            {
                error.WriteLine(
                    $"{warnings} workspace Warning diagnostic(s) (listed above); continuing.");
            }

            var extraction = BuildDesiredGraph(outcomes, branch, JsInteropMatcher.ResolveMode(), error.WriteLine);
            PrintSummary(extraction, output);

            if (options.DryRun)
            {
                await PrintDryRunPlanAsync(branch, storeHint, extraction, output, error).ConfigureAwait(false);
                output.WriteLine($"Timing: {loadKind} {loadMs} ms, extract {extractMs} ms.");
                output.WriteLine("Dry run: the graph store was not touched.");
                return ExtractorApp.ExitSuccess;
            }

            // ---- Reconcile into the graph store. Connection info comes ONLY from env vars. ----
            IGraphStore store;
            try
            {
                // The solution directory (or the worktree route's original-solution
                // StoreHint) is the indexer's natural path hint, so the sqlite
                // store-per-solution derivation lands on the same store every head
                // resolves for this repo.
                store = GraphStoreFactory.FromEnvironment(storeHint);
            }
            catch (InvalidOperationException ex)
            {
                error.WriteLine(ex.Message);
                return ExtractorApp.ExitUsageOrConfigError;
            }

            error.WriteLine($"Backend: {store.Description}");

            // Serialize the read-plan-write reconcile against any other index run on this
            // same store (rapid git-hook firings, a manual index racing a hook). A watch
            // loop's own cycles are already sequential, so this only ever contends across
            // processes. Timeout generously — a full-solution index can take minutes.
            await using var indexLock = await IndexLock
                .AcquireAsync(store.Description, TimeSpan.FromMinutes(10), error.WriteLine)
                .ConfigureAwait(false);
            if (!indexLock.Acquired)
            {
                await store.DisposeAsync().ConfigureAwait(false);
                error.WriteLine(
                    "Another index run on this store did not finish in time; skipping this cycle " +
                    "(the graph keeps its last good state).");
                return ExtractorApp.ExitUsageOrConfigError;
            }

            try
            {
                await using (store.ConfigureAwait(false))
                {
                    await store.EnsureSchemaAsync().ConfigureAwait(false);

                    var reconciler = new GraphReconciler(store.Writer, store.Snapshot);

                    var stopwatch = Stopwatch.StartNew();
                    ReconcileReport report;
                    try
                    {
                        report = await reconciler.ReconcileAsync(
                            branch, extraction.Nodes, extraction.Edges, options.AllowEmpty)
                            .ConfigureAwait(false);
                    }
                    catch (InvalidOperationException ex)
                    {
                        // The empty-desired guard: refusal, not failure of the environment.
                        error.WriteLine(ex.Message);
                        return ExtractorApp.ExitUsageOrConfigError;
                    }

                    var reconcileMs = stopwatch.ElapsedMilliseconds;

                    output.WriteLine(
                        $"Reconciled branch '{branch}' at {store.Description}: " +
                        $"upserted {report.NodesUpserted} nodes / {report.EdgesUpserted} edges, " +
                        $"pruned {report.NodesDeleted} stale nodes / {report.EdgesDeleted} stale edges.");
                    output.WriteLine(
                        $"Timing: {loadKind} {loadMs} ms, extract {extractMs} ms, reconcile {reconcileMs} ms.");
                }

                return ExtractorApp.ExitSuccess;
            }
            catch (Exception ex)
            {
                error.WriteLine($"Graph write failed: {ex.Message}");
                return ExtractorApp.ExitUsageOrConfigError;
            }
        }
        finally
        {
            if (ownExtractors)
            {
                foreach (var extractor in exs)
                {
                    await extractor.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// The desired graph for one cycle: every extractor's rows merged
    /// (<see cref="MergeResults"/>), then the cross-tier interop edges derived from the merged
    /// interop surface (<see cref="JsInteropMatcher"/>) appended — <c>JS_CALLS</c> (C#→JS) and
    /// <c>JS_INVOKES</c> (JS→C#). The matching runs only once the whole graph exists, so both
    /// endpoints of each edge — contributed by different extractors — are present as nodes. When
    /// it derives nothing (a single-tier solution, or <see cref="JsInteropMode.Off"/>), the
    /// merged result is returned unchanged, keeping the single-tier fixtures byte-for-byte
    /// identical.
    /// </summary>
    internal static ExtractionResult BuildDesiredGraph(
        IReadOnlyList<ExtractionOutcome> outcomes,
        string branch,
        JsInteropMode mode,
        Action<string>? log = null)
    {
        var merged = MergeResults(outcomes);
        if (mode == JsInteropMode.Off || merged.Interop is not { } surface)
        {
            return merged;
        }

        var nodeIds = merged.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var interopEdges = JsInteropMatcher.Match(surface, nodeIds, branch, mode, log)
            .Concat(JsInteropMatcher.MatchDotNetInvokes(surface, nodeIds, branch, mode, log))
            .ToList();
        if (interopEdges.Count == 0)
        {
            return merged;
        }

        var edges = new List<EdgeRow>(merged.Edges);
        var edgeKeys = new HashSet<string>(
            edges.Select(e => $"{e.Type}\n{e.FromId}\n{e.ToId}"), StringComparer.Ordinal);
        foreach (var edge in interopEdges)
        {
            if (edgeKeys.Add($"{edge.Type}\n{edge.FromId}\n{edge.ToId}"))
            {
                edges.Add(edge);
            }
        }

        return merged with { Edges = edges };
    }

    /// <summary>
    /// Merges every extractor's rows into one desired set: nodes deduped by <c>Id</c>
    /// (first writer wins — cross-language ids never collide, see the <c>js|</c> tier tag),
    /// edges deduped by <c>(Type, FromId, ToId)</c>, and the interop surfaces concatenated (the
    /// Roslyn C# call sites with the oxc JS exports). A single extractor's rows are returned
    /// as-is (identical order, so a pinned single-tier fixture is byte-for-byte unchanged); its
    /// own interop surface rides along for the JS_CALLS pass.
    /// </summary>
    internal static ExtractionResult MergeResults(IReadOnlyList<ExtractionOutcome> outcomes)
    {
        if (outcomes.Count == 1)
        {
            return outcomes[0].Result;
        }

        var nodes = new Dictionary<string, NodeRow>(StringComparer.Ordinal);
        var edges = new List<EdgeRow>();
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
        var csSites = new List<CsJsInteropSite>();
        var jsExports = new List<JsInteropExport>();
        var jsDotNetCalls = new List<JsDotNetCall>();
        var csInvokableTargets = new List<CsInvokableTarget>();
        foreach (var outcome in outcomes)
        {
            foreach (var node in outcome.Result.Nodes)
            {
                nodes.TryAdd(node.Id, node);
            }

            foreach (var edge in outcome.Result.Edges)
            {
                if (edgeKeys.Add($"{edge.Type}\n{edge.FromId}\n{edge.ToId}"))
                {
                    edges.Add(edge);
                }
            }

            if (outcome.Result.Interop is { } surface)
            {
                csSites.AddRange(surface.CsCallSites);
                jsExports.AddRange(surface.JsExports);
                jsDotNetCalls.AddRange(surface.JsDotNetCalls);
                csInvokableTargets.AddRange(surface.CsInvokableTargets);
            }
        }

        var interop = csSites.Count > 0 || jsExports.Count > 0
                      || jsDotNetCalls.Count > 0 || csInvokableTargets.Count > 0
            ? new InteropSurface(csSites, jsExports, jsDotNetCalls, csInvokableTargets)
            : null;
        return new ExtractionResult(nodes.Values.ToList(), edges, interop);
    }

    /// <summary>
    /// On <c>--dry-run</c> with the backend configured, also show what a real run WOULD
    /// delete (extraction + snapshot + pure plan; zero writes). Without configuration the
    /// extraction summary alone is printed.
    /// </summary>
    private static async Task PrintDryRunPlanAsync(
        string branch, string? storeHint, ExtractionResult extraction, TextWriter output, TextWriter error)
    {
        if (!GraphStoreFactory.IsConfigured)
        {
            output.WriteLine("Plan: NEO4J_* not configured — extraction summary only.");
            return;
        }

        try
        {
            var store = GraphStoreFactory.FromEnvironment(storeHint);
            error.WriteLine($"Backend: {store.Description}");
            await using (store.ConfigureAwait(false))
            {
                var snapshot = store.Snapshot;
                var existingIds = await snapshot.GetNodeIdsAsync(branch).ConfigureAwait(false);
                var existingKeys = await snapshot.GetEdgeKeysAsync(
                    branch, t => error.WriteLine($"Warning: unknown edge type '{t}' in store; ignored."))
                    .ConfigureAwait(false);

                var plan = GraphReconciler.ComputePlan(
                    extraction.Nodes, extraction.Edges, existingIds, existingKeys);
                output.WriteLine(
                    $"Plan for branch '{branch}': upsert {plan.NodesToUpsert.Count} nodes / " +
                    $"{plan.EdgesToUpsert.Count} edges, delete {plan.NodeIdsToDelete.Count} stale " +
                    $"nodes / {plan.EdgeKeysToDelete.Count} stale edges.");
            }
        }
        catch (Exception ex)
        {
            error.WriteLine($"Plan unavailable (graph read failed): {ex.Message}");
        }
    }

    /// <summary>Node counts by kind and edge counts by type, plus totals.</summary>
    internal static void PrintSummary(ExtractionResult extraction, TextWriter writer)
    {
        writer.WriteLine($"Nodes: {extraction.Nodes.Count}");
        foreach (var group in extraction.Nodes
                     .GroupBy(n => n.Kind, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            writer.WriteLine($"  {group.Key}: {group.Count()}");
        }

        writer.WriteLine($"Edges: {extraction.Edges.Count}");
        foreach (var group in extraction.Edges
                     .GroupBy(e => e.Type, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            writer.WriteLine($"  {group.Key}: {group.Count()}");
        }
    }
}
