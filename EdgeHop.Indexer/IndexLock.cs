using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace EdgeHop.Roslyn;

/// <summary>
/// A cross-process, per-store advisory lock around the read-plan-write reconcile, so two
/// index runs against the SAME store (rapid git-hook firings — a commit and a checkout
/// during a rebase — or a manual <c>index</c> racing a hook) serialize instead of
/// interleaving their delete traffic. Reconcile is single-writer-per-branch by design;
/// this keeps that true across processes.
/// </summary>
/// <remarks>
/// Implemented as an exclusive OS file lock (a <see cref="FileStream"/> opened with
/// <see cref="FileShare.None"/>), NOT a named <see cref="Mutex"/>: a mutex is
/// thread-affine and would break across the <c>await</c>s in the reconcile, and a named
/// semaphore is not crash-safe. An OS file lock is neither — the kernel releases it when
/// the holding process exits, so a crash never strands the lock, and the handle is not
/// tied to the acquiring thread. The <c>.lock</c> file itself is never deleted (deletion
/// would race); a lingering file carries no lock and is simply reopened.
/// </remarks>
public sealed class IndexLock : IAsyncDisposable
{
    private readonly FileStream? _stream;

    private IndexLock(FileStream? stream, bool acquired)
    {
        _stream = stream;
        Acquired = acquired;
    }

    /// <summary>True when the exclusive lock is held; false when acquisition timed out
    /// (the caller should skip the write and keep the last good graph).</summary>
    public bool Acquired { get; }

    /// <summary>
    /// Acquires the per-store lock, retrying until it is free or <paramref name="timeout"/>
    /// elapses. Returns a held lock (<see cref="Acquired"/> true) or, on timeout, a
    /// no-op (<see cref="Acquired"/> false) — never throws for contention.
    /// </summary>
    /// <param name="storeKey">A value that is stable per target store (the store's
    /// <c>Description</c>); hashed into the lock file name so different stores never
    /// contend.</param>
    /// <param name="timeout">How long to wait for a competing run to finish.</param>
    /// <param name="log">Optional sink; a single line is written once if the caller has
    /// to wait, so a blocked hook run is not silent.</param>
    /// <param name="cancellationToken">Cancels the wait (watch-mode Ctrl+C).</param>
    public static async Task<IndexLock> AcquireAsync(
        string storeKey,
        TimeSpan timeout,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeKey);

        var path = LockPathFor(storeKey);
        var stopwatch = Stopwatch.StartNew();
        var announcedWait = false;

        while (true)
        {
            try
            {
                // OpenOrCreate + FileShare.None: the first opener holds the lock; others
                // get a sharing violation until it closes. DeleteOnClose is deliberately
                // NOT used — it would let a second waiter recreate the file underneath us.
                var stream = new FileStream(
                    path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new IndexLock(stream, acquired: true);
            }
            catch (IOException)
            {
                if (stopwatch.Elapsed >= timeout)
                {
                    return new IndexLock(null, acquired: false);
                }

                if (!announcedWait)
                {
                    announcedWait = true;
                    log?.Invoke("Waiting for another index run on this store to finish…");
                }

                try
                {
                    await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return new IndexLock(null, acquired: false);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // The lock directory or file is not writable: proceed unlocked rather
                // than block indexing entirely (best-effort serialization).
                log?.Invoke($"Index lock unavailable at '{path}'; proceeding without it.");
                return new IndexLock(null, acquired: true);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string LockPathFor(string storeKey)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(storeKey)))[..16].ToLowerInvariant();
        var dir = Path.Combine(Path.GetTempPath(), "edgehop-locks");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"idx-{hash}.lock");
    }
}
