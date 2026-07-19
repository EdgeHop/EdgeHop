using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace EdgeHop.Roslyn;

/// <summary>A ready-to-index private worktree for one branch.</summary>
/// <param name="Path">The worktree's root directory.</param>
/// <param name="Branch">The branch it has checked out.</param>
/// <param name="SolutionPath">The solution file inside the worktree.</param>
public sealed record WorktreeInfo(string Path, string Branch, string SolutionPath);

/// <summary>
/// Creates and refreshes the private, disposable worktrees used to index a branch OTHER
/// than the one checked out in the developer's working tree — which is never touched
/// (in-place checkouts never happen anywhere in EdgeHop; a dirty main tree is the
/// normal case this exists for). Worktrees live outside the repository under
/// <c>%LOCALAPPDATA%\EdgeHop\worktrees\&lt;repo&gt;-&lt;hash&gt;\&lt;branch&gt;</c> and are
/// kept between runs (restore + MSBuild caches make reuse much faster); they are a
/// cache, never user-edited, so refresh is a hard reset. Shells out to <c>git.exe</c> —
/// indexing-time only, never on the query path.
/// </summary>
public static class WorktreeManager
{
    /// <summary>
    /// Ensures a worktree for <paramref name="branch"/> exists and matches the branch
    /// ref, then restores its solution. Requires the branch to exist locally (no
    /// auto-create — explicit is safer) and not be checked out in another worktree.
    /// </summary>
    /// <exception cref="InvalidOperationException">git/dotnet failures, missing local
    /// branch, or branch checked out elsewhere — all with actionable messages.</exception>
    public static async Task<WorktreeInfo> EnsureAsync(
        string repoRoot, string branch, string solutionRelativePath, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionRelativePath);

        // 1. The branch must exist as a local ref.
        var verify = await RunGitAsync(repoRoot, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}")
            .ConfigureAwait(false);
        if (verify.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Branch '{branch}' has no local ref in '{repoRoot}'. Create it first, e.g.: " +
                $"git branch {branch} origin/{branch}");
        }

        var worktreePath = GetWorktreePath(repoRoot, branch);

        if (!Directory.Exists(worktreePath))
        {
            // Remove any stale registration for a path that no longer exists on disk.
            await RunGitAsync(repoRoot, "worktree", "prune").ConfigureAwait(false);

            log?.Invoke($"Creating worktree for '{branch}' at '{worktreePath}'…");
            Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);
            var add = await RunGitAsync(repoRoot, "worktree", "add", worktreePath, branch)
                .ConfigureAwait(false);
            if (add.ExitCode != 0)
            {
                // Phrasing varies across git versions: "already checked out at …" vs
                // "already used by worktree at …".
                var alreadyInUse =
                    add.Stderr.Contains("already checked out", StringComparison.OrdinalIgnoreCase)
                    || add.Stderr.Contains("already used by worktree", StringComparison.OrdinalIgnoreCase);
                var hint = alreadyInUse
                    ? $" Branch '{branch}' is already checked out in another worktree — if it is your " +
                      "working tree's current branch, index it directly (no --branch needed)."
                    : "";
                throw new InvalidOperationException(
                    $"git worktree add failed (exit {add.ExitCode}): {add.Stderr.Trim()}{hint}");
            }
        }
        else
        {
            // Reuse: the worktree is a private cache — hard-refresh it to the current ref.
            log?.Invoke($"Refreshing worktree for '{branch}' at '{worktreePath}'…");
            var reset = await RunGitAsync(worktreePath, "reset", "--hard", branch).ConfigureAwait(false);
            if (reset.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git reset --hard failed in worktree '{worktreePath}' (exit {reset.ExitCode}): " +
                    $"{reset.Stderr.Trim()}. Delete the directory and re-run to recreate it.");
            }

            var clean = await RunGitAsync(worktreePath, "clean", "-fd").ConfigureAwait(false);
            if (clean.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git clean -fd failed in worktree '{worktreePath}' (exit {clean.ExitCode}): {clean.Stderr.Trim()}");
            }
        }

        var solutionPath = Path.Combine(worktreePath, solutionRelativePath);
        if (!File.Exists(solutionPath))
        {
            throw new InvalidOperationException(
                $"Solution '{solutionRelativePath}' does not exist on branch '{branch}' " +
                $"(looked at '{solutionPath}').");
        }

        // 2. Restore so the design-time build has assets (exactly like the test fixtures).
        // NuGetAudit=false: audit warnings are RECORDED into project.assets.json and
        // replayed by every subsequent build — including the indexer's design-time build,
        // where a dependency CVE on an older branch would masquerade as a workspace
        // Failure and hard-stop indexing. Auditing that branch's packages is its own
        // build/CI's job, not the indexer's.
        log?.Invoke($"Restoring '{solutionPath}'…");
        var restore = await RunProcessAsync(
            "dotnet", worktreePath, "restore", solutionPath, "-p:NuGetAudit=false")
            .ConfigureAwait(false);
        if (restore.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet restore failed for '{solutionPath}' (exit {restore.ExitCode}):" +
                Environment.NewLine + restore.Stderr.Trim());
        }

        return new WorktreeInfo(worktreePath, branch, solutionPath);
    }

    /// <summary>
    /// <c>%LOCALAPPDATA%\EdgeHop\worktrees\&lt;repo-dir-name&gt;-&lt;8-hex-path-hash&gt;\&lt;branch-sanitized&gt;</c>.
    /// Outside the repo (never seen by its tooling or the watch filter); the hash keeps
    /// same-named repos apart; sanitization keeps <c>feature/x</c> a single directory.
    /// </summary>
    public static string GetWorktreePath(string repoRoot, string branch)
    {
        var fullRoot = Path.GetFullPath(repoRoot);
        var hashInput = fullRoot.ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput)))[..8]
            .ToLowerInvariant();

        var repoName = Path.GetFileName(fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var sanitizedBranch = SanitizeBranch(branch);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EdgeHop", "worktrees", $"{repoName}-{hash}", sanitizedBranch);
    }

    private static string SanitizeBranch(string branch)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(branch.Length);
        foreach (var ch in branch)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return builder.ToString();
    }

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(
        string workingDirectory, params string[] args)
        => RunProcessAsync("git", workingDirectory, args);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName, string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

        // Drain both streams before waiting so neither pipe can fill and deadlock.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }
}
