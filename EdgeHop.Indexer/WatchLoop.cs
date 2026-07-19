using EdgeHop.Core;

namespace EdgeHop.Roslyn;

/// <summary>One debounced batch of file-change activity.</summary>
/// <param name="Paths">The distinct relevant paths seen. Fed to
/// <see cref="WorkspaceSession"/>, which refreshes them in memory when every path is a
/// known, still-existing document — and falls back to a full load otherwise. Extraction
/// and reconcile stay whole-solution either way.</param>
/// <param name="Overflow">True when the watcher reported a buffer overflow ("everything
/// may have changed"); <see cref="Paths"/> may then be incomplete, so the next cycle
/// always full-loads.</param>
public sealed record ChangeBatch(IReadOnlyList<string> Paths, bool Overflow);

/// <summary>
/// Debounced change coalescing, decoupled from <see cref="FileSystemWatcher"/> so it is
/// unit-testable: <see cref="Post"/> events from any thread; <see cref="WaitForBatchAsync"/>
/// completes once at least one relevant event has arrived AND the debounce window has
/// been quiet, returning the coalesced distinct batch.
/// </summary>
public sealed class ChangeBatcher
{
    private readonly TimeSpan _debounce;
    private readonly Func<string, bool> _isRelevant;
    private readonly object _gate = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);

    private bool _overflow;
    private DateTime _lastEventUtc;
    private TaskCompletionSource _signal = NewSignal();

    public ChangeBatcher(TimeSpan debounce, Func<string, bool> isRelevant)
    {
        ArgumentNullException.ThrowIfNull(isRelevant);
        _debounce = debounce;
        _isRelevant = isRelevant;
    }

    /// <summary>Records one path event; irrelevant paths are ignored.</summary>
    public void Post(string fullPath)
    {
        if (!_isRelevant(fullPath))
        {
            return;
        }

        PostAlways(fullPath);
    }

    /// <summary>Records an event that must fire a batch regardless of the relevance
    /// filter (e.g. a git <c>HEAD</c> change marker).</summary>
    public void PostAlways(string marker)
    {
        lock (_gate)
        {
            _pending.Add(marker);
            _lastEventUtc = DateTime.UtcNow;
            _signal.TrySetResult();
        }
    }

    /// <summary>Records a watcher buffer overflow: the next batch is marked
    /// <see cref="ChangeBatch.Overflow"/> and fires even with no named paths.</summary>
    public void PostOverflow()
    {
        lock (_gate)
        {
            _overflow = true;
            _lastEventUtc = DateTime.UtcNow;
            _signal.TrySetResult();
        }
    }

    /// <summary>
    /// Waits for the next quiet-debounced batch. Events arriving while a previous batch
    /// is being processed simply accumulate for the next call — batches never overlap by
    /// construction of the caller's single loop.
    /// </summary>
    public async Task<ChangeBatch> WaitForBatchAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task waitTask;
            lock (_gate)
            {
                if (_pending.Count > 0 || _overflow)
                {
                    var quietFor = DateTime.UtcNow - _lastEventUtc;
                    if (quietFor >= _debounce)
                    {
                        var batch = new ChangeBatch(_pending.ToList(), _overflow);
                        _pending.Clear();
                        _overflow = false;
                        _signal = NewSignal();
                        return batch;
                    }
                }

                waitTask = _signal.Task;
            }

            // Wait for either new activity or the remainder of the quiet window.
            var delay = Task.Delay(_debounce, cancellationToken);
            await Task.WhenAny(waitTask, delay).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// <c>index --watch</c>: an initial full cycle, then a <see cref="FileSystemWatcher"/> on
/// the solution's repository tree (plus a dedicated watcher on the gitdir's <c>HEAD</c>,
/// which the main watcher's <c>.git</c> exclusion would otherwise hide) feeding a
/// debounced loop where every batch runs an extract→reconcile cycle over a solution
/// obtained from the shared <see cref="WorkspaceSession"/>: an in-memory refresh when
/// the batch touched only known documents, a full load otherwise (created/deleted/
/// renamed files, branch switches, overflow). Extraction and reconcile stay
/// whole-solution either way, so correctness never depends on the batch contents; a
/// cycle failure keeps the last good graph and the loop alive.
/// </summary>
public static class WatchLoop
{
    /// <summary>Default quiet window between the last file event and a cycle.</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(1500);

    /// <summary>Authored-source extensions a change should trigger a re-index for: the C#/Razor
    /// set the Roslyn extractor consumes plus the JS/TS + markup set the oxc extractor discovers
    /// (so a JS/TS project — or a Blazor project's collocated scripts — re-indexes on edit).</summary>
    private static readonly HashSet<string> WatchedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".razor", ".cshtml", ".html", ".htm", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx",
    };

    /// <summary>Path segments whose subtrees never carry authored source worth re-indexing —
    /// build outputs, the git dir and third-party trees. Mirrors the oxc discovery skip list so a
    /// churn under <c>node_modules</c>/<c>dist</c> cannot spin the watch loop.</summary>
    private static readonly HashSet<string> IgnoredSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "obj", "bin", ".git", "node_modules", "dist", "out", "coverage", ".vs", ".idea", "_framework",
    };

    /// <summary>True for authored source files the watcher should react to: a
    /// <see cref="WatchedExtensions"/> file outside any <see cref="IgnoredSegments"/> subtree.</summary>
    public static bool IsRelevantSourceFile(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        if (!WatchedExtensions.Contains(Path.GetExtension(path)))
        {
            return false;
        }

        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var segment in segments)
        {
            if (IgnoredSegments.Contains(segment))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Runs the watch loop until <paramref name="cancellationToken"/> fires (Ctrl+C).
    /// Returns <see cref="ExtractorApp.ExitSuccess"/> on clean shutdown; a fatal startup
    /// problem (bad solution path, missing Neo4j configuration on the FIRST cycle)
    /// returns that cycle's exit code instead of looping.
    /// </summary>
    public static async Task<int> RunAsync(
        IndexOptions options,
        TimeSpan debounce,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The watch tree is the target directory itself (directory mode) or the .sln's directory.
        var fullTarget = Path.GetFullPath(options.SolutionPath);
        var watchRoot = Directory.Exists(fullTarget) ? fullTarget : Path.GetDirectoryName(fullTarget);
        if (watchRoot is null || !Directory.Exists(watchRoot))
        {
            error.WriteLine($"Cannot watch: target directory not found for '{options.SolutionPath}'.");
            return ExtractorApp.ExitUsageOrConfigError;
        }

        // One extractor set for the whole watch: kept alive so each extractor's in-memory
        // state (the Roslyn workspace) powers the refresh — a batch with no membership change
        // refreshes instead of reloading (see WorkspaceSession). Disposed on shutdown.
        IReadOnlyList<IExtractor> extractors;
        try
        {
            extractors = ExtractorFactory.LoadAll(error.WriteLine);
        }
        catch (InvalidOperationException ex)
        {
            error.WriteLine(ex.Message);
            return ExtractorApp.ExitUsageOrConfigError;
        }

        try
        {
            // Initial cycle: a startup failure here is fatal (nothing sensible to watch for).
            output.WriteLine($"Watch: initial index of '{options.SolutionPath}'…");
            var firstExit = await IndexCommand
                .RunOnceAsync(options, output, error, extractors, changedPaths: null)
                .ConfigureAwait(false);
            if (firstExit == ExtractorApp.ExitUsageOrConfigError)
            {
                return firstExit;
            }

            var lastBranch = BranchResolver.Resolve(options.ExplicitBranch, watchRoot);
            var batcher = new ChangeBatcher(debounce, IsRelevantSourceFile);

            using var sourceWatcher = new FileSystemWatcher(watchRoot)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            };
            sourceWatcher.Changed += (_, e) => batcher.Post(e.FullPath);
            sourceWatcher.Created += (_, e) => batcher.Post(e.FullPath);
            sourceWatcher.Deleted += (_, e) => batcher.Post(e.FullPath);
            sourceWatcher.Renamed += (_, e) =>
            {
                batcher.Post(e.OldFullPath);
                batcher.Post(e.FullPath);
            };
            sourceWatcher.Error += (_, _) => batcher.PostOverflow();

            // The main watcher excludes .git, so branch switches need their own HEAD watcher
            // (the gitdir may also live outside the tree entirely for worktrees).
            using var headWatcher = TryCreateHeadWatcher(watchRoot, batcher, error);

            sourceWatcher.EnableRaisingEvents = true;
            output.WriteLine(
                $"Watching '{watchRoot}' for source changes (debounce {debounce.TotalMilliseconds:F0} ms). Ctrl+C to stop.");

            try
            {
                while (true)
                {
                    var batch = await batcher.WaitForBatchAsync(cancellationToken).ConfigureAwait(false);
                    output.WriteLine(batch.Overflow
                        ? "Watch: change buffer overflowed — running a full cycle."
                        : $"Watch: {batch.Paths.Count} changed file(s) — running a cycle.");

                    var branch = BranchResolver.Resolve(options.ExplicitBranch, watchRoot);
                    if (!string.Equals(branch, lastBranch, StringComparison.Ordinal))
                    {
                        output.WriteLine(
                            $"Watch: branch changed '{lastBranch}' → '{branch}'. " +
                            $"Data indexed under '{lastBranch}' is retained (prune it explicitly if unwanted).");
                        lastBranch = branch;
                    }

                    // Overflow means "everything may have changed": force a full reload by
                    // passing null. Otherwise the batch's paths drive refresh-vs-reload inside
                    // the extractor; the HEAD marker a branch switch posts is not a document, so
                    // it forces a full reload of the switched working tree by construction.
                    var changedPaths = batch.Overflow ? null : batch.Paths;
                    var exit = await IndexCommand
                        .RunOnceAsync(options, output, error, extractors, changedPaths)
                        .ConfigureAwait(false);
                    if (exit != ExtractorApp.ExitSuccess)
                    {
                        error.WriteLine(
                            $"Watch: cycle failed (exit {exit}); the graph keeps its last good state. Still watching.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                output.WriteLine("Watch: stopped.");
                return ExtractorApp.ExitSuccess;
            }
        }
        finally
        {
            foreach (var extractor in extractors)
            {
                await extractor.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static FileSystemWatcher? TryCreateHeadWatcher(
        string watchRoot, ChangeBatcher batcher, TextWriter error)
    {
        var gitDir = GitBranchDetector.TryFindGitDir(watchRoot);
        if (gitDir is null || !Directory.Exists(gitDir))
        {
            error.WriteLine("Watch: no git repository found — branch switches will not be observed.");
            return null;
        }

        var headWatcher = new FileSystemWatcher(gitDir)
        {
            Filter = "HEAD",
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
        };
        // HEAD changes must fire a cycle even though the path fails the source filter.
        FileSystemEventHandler onHead = (_, e) => batcher.PostAlways(e.FullPath);
        headWatcher.Changed += onHead;
        headWatcher.Created += onHead;
        headWatcher.Renamed += (_, e) => batcher.PostAlways(e.FullPath);
        headWatcher.EnableRaisingEvents = true;
        return headWatcher;
    }
}
