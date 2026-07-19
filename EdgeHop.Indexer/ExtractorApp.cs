using EdgeHop.Core;

namespace EdgeHop.Roslyn;

/// <summary>
/// The indexer's application shell: verb dispatch and argument parsing. Lives behind
/// <c>Program.Main</c> so that no MSBuild/Roslyn type is JIT-resolved before
/// <see cref="MsBuildBootstrap.EnsureRegistered"/> has run (README "MSBuild gotcha").
/// </summary>
/// <remarks>
/// Verbs:
/// <list type="bullet">
/// <item><description><c>index &lt;sln-or-dir&gt; [--branch b] [--dry-run] [--allow-empty]</c> —
/// load → extract → reconcile (upsert + surgical prune of stale rows) under the resolved
/// branch (see <see cref="BranchResolver"/>). The target is a <c>.sln</c> file OR a project
/// DIRECTORY (a non-.NET tree, e.g. a pure JS/TS project — the C# extractor no-ops and the
/// JS/TS extractor graphs the folder). The legacy form <c>edgehop-extract &lt;sln-or-dir&gt;</c>
/// maps to this verb — NOTE the deliberate behavior change from phases 1–3: a full build now
/// also prunes stale rows.</description></item>
/// <item><description><c>prune --branch b --yes</c> — whole-branch delete; without
/// <c>--yes</c> it prints what it would delete and exits 1.</description></item>
/// <item><description><c>branches</c> — distinct branch values with node counts.</description></item>
/// <item><description><c>install-hooks &lt;sln&gt; [--repo p]</c> /
/// <c>uninstall-hooks [--repo p]</c> — write/remove local git hooks
/// (post-commit/-merge/-checkout) that background-re-index the solution, the
/// push-driven-re-indexing replacement for a long-running <c>--watch</c> (see
/// <see cref="GitHookInstaller"/>).</description></item>
/// </list>
/// </remarks>
public static class ExtractorApp
{
    /// <summary>Everything succeeded.</summary>
    public const int ExitSuccess = 0;

    /// <summary>Usage error, missing/invalid solution path, refused operation, or Neo4j
    /// configuration/write failure.</summary>
    public const int ExitUsageOrConfigError = 1;

    /// <summary>
    /// The workspace load produced at least one Failure-level diagnostic. Hard stop per
    /// the README: a graph built from a partially-loaded solution cannot be trusted.
    /// </summary>
    public const int ExitWorkspaceFailure = 2;

    /// <summary>
    /// Dispatches to the requested verb. Callers must have called
    /// <see cref="MsBuildBootstrap.EnsureRegistered"/> before this method is invoked
    /// (Program.Main does; nothing else should call this).
    /// </summary>
    public static Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            PrintUsage(Console.Error);
            return Task.FromResult(ExitUsageOrConfigError);
        }

        return args[0].ToLowerInvariant() switch
        {
            "index" => RunIndexAsync(args[1..]),
            "prune" => RunPruneAsync(args[1..]),
            "branches" => RunBranchesAsync(args[1..]),
            "install-hooks" => RunInstallHooksAsync(args[1..]),
            "uninstall-hooks" => RunUninstallHooksAsync(args[1..]),
            _ => RunIndexAsync(args), // legacy form: <sln> [--dry-run] == index
        };
    }

    private static async Task<int> RunIndexAsync(string[] args)
    {
        string? solutionPath = null;
        string? branch = null;
        var dryRun = false;
        var allowEmpty = false;
        var watch = false;
        var noWorktree = false;
        var debounce = WatchLoop.DefaultDebounce;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                dryRun = true;
            }
            else if (string.Equals(arg, "--allow-empty", StringComparison.OrdinalIgnoreCase))
            {
                allowEmpty = true;
            }
            else if (string.Equals(arg, "--watch", StringComparison.OrdinalIgnoreCase))
            {
                watch = true;
            }
            else if (string.Equals(arg, "--no-worktree", StringComparison.OrdinalIgnoreCase))
            {
                noWorktree = true;
            }
            else if (string.Equals(arg, "--debounce", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out var debounceMs) || debounceMs < 0)
                {
                    Console.Error.WriteLine("--debounce requires a non-negative integer (milliseconds).");
                    return ExitUsageOrConfigError;
                }

                debounce = TimeSpan.FromMilliseconds(debounceMs);
                i++;
            }
            else if (string.Equals(arg, "--branch", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
                {
                    Console.Error.WriteLine("--branch requires a value.");
                    return ExitUsageOrConfigError;
                }

                branch = args[++i];
            }
            else if (arg.StartsWith('-'))
            {
                Console.Error.WriteLine($"Unknown option: {arg}");
                PrintUsage(Console.Error);
                return ExitUsageOrConfigError;
            }
            else if (solutionPath is null)
            {
                solutionPath = arg;
            }
            else
            {
                Console.Error.WriteLine($"Unexpected argument: {arg}");
                PrintUsage(Console.Error);
                return ExitUsageOrConfigError;
            }
        }

        if (solutionPath is null)
        {
            PrintUsage(Console.Error);
            return ExitUsageOrConfigError;
        }

        if (watch && dryRun)
        {
            Console.Error.WriteLine("--watch and --dry-run cannot be combined.");
            return ExitUsageOrConfigError;
        }

        // Worktree routing: an explicit --branch that is NOT the working tree's current
        // branch is indexed from a private worktree so the (possibly dirty) working tree
        // is never disturbed. --no-worktree stamps the branch onto the current tree
        // instead (CI/containers); watch mode always follows the working tree.
        if (branch is not null && !noWorktree && !watch && File.Exists(solutionPath))
        {
            var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath));
            var repoRoot = GitBranchDetector.TryFindRepoRoot(solutionDir);
            var currentBranch = GitBranchDetector.TryDetect(solutionDir);

            if (repoRoot is not null
                && currentBranch is not null
                && !string.Equals(branch, currentBranch, StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"Indexing '{branch}' via a private worktree (working tree is on '{currentBranch}' " +
                    "and is never touched; use --no-worktree to index the current tree under that " +
                    "branch value instead).");
                try
                {
                    var solutionRelative = Path.GetRelativePath(repoRoot, Path.GetFullPath(solutionPath));
                    var worktree = await WorktreeManager.EnsureAsync(
                        repoRoot, branch, solutionRelative, Console.Error.WriteLine).ConfigureAwait(false);
                    // StoreHint = the ORIGINAL solution dir: the worktree's own path is
                    // per-branch and would derive the wrong sqlite store.
                    var worktreeOptions = new IndexOptions(
                        worktree.SolutionPath, branch, dryRun, allowEmpty, StoreHint: solutionDir);
                    return await IndexCommand.RunOnceAsync(worktreeOptions, Console.Out, Console.Error)
                        .ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    Console.Error.WriteLine(ex.Message);
                    return ExitUsageOrConfigError;
                }
            }
        }

        var options = new IndexOptions(solutionPath, branch, dryRun, allowEmpty);
        if (!watch)
        {
            return await IndexCommand.RunOnceAsync(options, Console.Out, Console.Error).ConfigureAwait(false);
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // let the loop unwind and exit 0 cleanly
            cts.Cancel();
        };
        return await WatchLoop.RunAsync(options, debounce, Console.Out, Console.Error, cts.Token)
            .ConfigureAwait(false);
    }

    private static async Task<int> RunInstallHooksAsync(string[] args)
    {
        string? solutionPath = null;
        string? repo = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--repo", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
                {
                    Console.Error.WriteLine("--repo requires a directory path.");
                    return ExitUsageOrConfigError;
                }

                repo = args[++i];
            }
            else if (arg.StartsWith('-'))
            {
                Console.Error.WriteLine($"Unknown option for install-hooks: {arg}");
                return ExitUsageOrConfigError;
            }
            else if (solutionPath is null)
            {
                solutionPath = arg;
            }
            else
            {
                Console.Error.WriteLine($"Unexpected argument: {arg}");
                return ExitUsageOrConfigError;
            }
        }

        if (solutionPath is null)
        {
            Console.Error.WriteLine("install-hooks requires a target: install-hooks <sln-or-dir> [--repo <path>]");
            return ExitUsageOrConfigError;
        }

        var fullTarget = Path.GetFullPath(solutionPath);
        var targetIsDirectory = Directory.Exists(fullTarget);
        if (!targetIsDirectory && !File.Exists(fullTarget))
        {
            Console.Error.WriteLine(
                $"Index target not found (expected a .sln file or a project directory): {solutionPath}");
            return ExitUsageOrConfigError;
        }

        var repoStart = targetIsDirectory ? fullTarget : Path.GetDirectoryName(fullTarget);
        var repoRoot = repo ?? GitBranchDetector.TryFindRepoRoot(repoStart) ?? repoStart;
        if (repoRoot is null)
        {
            Console.Error.WriteLine(
                "Could not determine the git repository for the target; pass --repo <path>.");
            return ExitUsageOrConfigError;
        }

        // The hooks re-index this exact target — a .sln or a directory — with the same 'index' verb.
        return await GitHookInstaller.InstallAsync(
            repoRoot, fullTarget, Console.Out, Console.Error).ConfigureAwait(false);
    }

    private static async Task<int> RunUninstallHooksAsync(string[] args)
    {
        string? repo = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--repo", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
                {
                    Console.Error.WriteLine("--repo requires a directory path.");
                    return ExitUsageOrConfigError;
                }

                repo = args[++i];
            }
            else
            {
                Console.Error.WriteLine($"Unknown option for uninstall-hooks: {arg}");
                return ExitUsageOrConfigError;
            }
        }

        var repoRoot = repo
            ?? GitBranchDetector.TryFindRepoRoot(Environment.CurrentDirectory)
            ?? Environment.CurrentDirectory;

        return await GitHookInstaller.UninstallAsync(repoRoot, Console.Out, Console.Error)
            .ConfigureAwait(false);
    }

    private static async Task<int> RunPruneAsync(string[] args)
    {
        string? branch = null;
        var confirmed = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--yes", StringComparison.OrdinalIgnoreCase))
            {
                confirmed = true;
            }
            else if (string.Equals(arg, "--branch", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
                {
                    Console.Error.WriteLine("--branch requires a value.");
                    return ExitUsageOrConfigError;
                }

                branch = args[++i];
            }
            else
            {
                Console.Error.WriteLine($"Unknown option for prune: {arg}");
                return ExitUsageOrConfigError;
            }
        }

        if (string.IsNullOrWhiteSpace(branch))
        {
            Console.Error.WriteLine("prune requires --branch <name> (and --yes to actually delete).");
            return ExitUsageOrConfigError;
        }

        IGraphStore store;
        try
        {
            store = GraphStoreFactory.FromEnvironment(Environment.CurrentDirectory);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitUsageOrConfigError;
        }

        Console.Error.WriteLine($"Backend: {store.Description}");

        try
        {
            await using (store.ConfigureAwait(false))
            {
                var snapshot = store.Snapshot;
                var nodeCount = (await snapshot.GetNodeIdsAsync(branch).ConfigureAwait(false)).Count;
                var edgeCount = (await snapshot.GetEdgeKeysAsync(branch).ConfigureAwait(false)).Count;

                if (!confirmed)
                {
                    Console.Out.WriteLine(
                        $"Branch '{branch}' holds {nodeCount} nodes and {edgeCount} edges. " +
                        "Re-run with --yes to delete them. Nothing was deleted.");
                    return ExitUsageOrConfigError;
                }

                var deleted = await store.Writer.DeleteBranchAsync(branch).ConfigureAwait(false);
                Console.Out.WriteLine($"Pruned branch '{branch}': deleted {deleted} nodes (and their edges).");
                return ExitSuccess;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Prune failed: {ex.Message}");
            return ExitUsageOrConfigError;
        }
    }

    private static async Task<int> RunBranchesAsync(string[] args)
    {
        if (args.Length > 0)
        {
            Console.Error.WriteLine("branches takes no arguments.");
            return ExitUsageOrConfigError;
        }

        IGraphStore store;
        try
        {
            store = GraphStoreFactory.FromEnvironment(Environment.CurrentDirectory);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitUsageOrConfigError;
        }

        Console.Error.WriteLine($"Backend: {store.Description}");

        try
        {
            await using (store.ConfigureAwait(false))
            {
                var branches = await store.Snapshot.GetBranchesAsync().ConfigureAwait(false);
                if (branches.Count == 0)
                {
                    Console.Out.WriteLine("The graph is empty: no branches.");
                }
                else
                {
                    foreach (var (name, nodes) in branches)
                    {
                        Console.Out.WriteLine($"{name}: {nodes} nodes");
                    }
                }

                return ExitSuccess;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Listing branches failed: {ex.Message}");
            return ExitUsageOrConfigError;
        }
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  edgehop-extract index <sln-or-dir> [--branch <b>] [--dry-run] [--allow-empty]");
        writer.WriteLine("                                       [--watch [--debounce <ms>]] [--no-worktree]");
        writer.WriteLine("  edgehop-extract prune --branch <b> [--yes]");
        writer.WriteLine("  edgehop-extract branches");
        writer.WriteLine("  edgehop-extract install-hooks <sln-or-dir> [--repo <path>]");
        writer.WriteLine("  edgehop-extract uninstall-hooks [--repo <path>]");
        writer.WriteLine("  edgehop-extract <sln-or-dir> [--dry-run]   (legacy form of 'index')");
        writer.WriteLine();
        writer.WriteLine("The target is either a .sln file (C# + any JS/TS via all extractors) or a project");
        writer.WriteLine("DIRECTORY for a non-.NET tree — e.g. a pure JS/TS project with no solution, graphed");
        writer.WriteLine("by the JS/TS extractor while the C# extractor no-ops. Query it exactly as usual:");
        writer.WriteLine("point EDGEHOP_REPO (MCP) or your working dir (edgehop CLI) at that folder.");
        writer.WriteLine();
        writer.WriteLine("index reconciles the branch to the target's current state: everything is");
        writer.WriteLine("upserted and stale nodes/edges are surgically deleted (branch-scoped).");
        writer.WriteLine();
        writer.WriteLine("Branch resolution when --branch is omitted:");
        writer.WriteLine("  EDGEHOP_BRANCH env var, else the current git branch of EDGEHOP_REPO,");
        writer.WriteLine("  else the solution directory's git branch, else 'main'.");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  --dry-run      Extract and print counts + the reconcile plan; never writes.");
        writer.WriteLine("  --allow-empty  Permit an empty extraction to empty the branch (guarded otherwise).");
        writer.WriteLine("  --watch        Keep running: re-index on source changes (.cs/.razor and .js/.ts,");
        writer.WriteLine("                 debounced) and on git branch switches. Ctrl+C stops cleanly.");
        writer.WriteLine("  --debounce     Quiet window in ms before a watch cycle (default 1500).");
        writer.WriteLine("  --no-worktree  Index the current tree under --branch's value instead of routing");
        writer.WriteLine("                 through a private worktree when it differs from the checked-out branch.");
        writer.WriteLine("  --yes          Actually delete (prune); without it, prune only prints counts.");
        writer.WriteLine("  --repo <path>  (install/uninstall-hooks) The git repo to (un)install hooks in;");
        writer.WriteLine("                 defaults to the solution's repo (install) or the cwd's (uninstall).");
        writer.WriteLine();
        writer.WriteLine("install-hooks writes post-commit/post-merge/post-checkout hooks that re-index the");
        writer.WriteLine("solution in the background after commits, merges and branch switches (a managed,");
        writer.WriteLine("marker-delimited block; existing unmanaged hooks are never overwritten). This is the");
        writer.WriteLine("hands-off alternative to leaving 'index --watch' running. uninstall-hooks removes it.");
        writer.WriteLine();
        writer.WriteLine("Environment:");
        writer.WriteLine("  EDGEHOP_BACKEND      'sqlite' (default when unset) or 'neo4j'.");
        writer.WriteLine("  EDGEHOP_SQLITE_PATH  sqlite store file override. Default is PER SOLUTION:");
        writer.WriteLine("                         %LOCALAPPDATA%\\EdgeHop\\stores\\<repo>-<hash>.db derived");
        writer.WriteLine("                         from the repo (EDGEHOP_REPO, else the solution/cwd repo);");
        writer.WriteLine("                         no repo found -> shared edgehop.db. No credentials.");
        writer.WriteLine("  NEO4J_URI, NEO4J_USER, NEO4J_PASSWORD, NEO4J_DATABASE (default: neo4j)");
        writer.WriteLine("                         Required for the neo4j backend (unless --dry-run).");
        writer.WriteLine();
        writer.WriteLine("Exit codes: 0 success, 1 usage/configuration/refused, 2 workspace Failure diagnostics.");
    }
}
