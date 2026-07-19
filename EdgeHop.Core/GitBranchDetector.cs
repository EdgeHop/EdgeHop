namespace EdgeHop.Core;

/// <summary>
/// Resolves the currently checked-out git branch by reading repository files directly —
/// no <c>git.exe</c> dependency, cheap enough to call per query. Handles both the
/// ordinary layout (<c>.git</c> directory) and the worktree/submodule layout
/// (<c>.git</c> FILE containing <c>gitdir: &lt;path&gt;</c>, whose target holds the
/// per-worktree <c>HEAD</c>).
/// </summary>
public static class GitBranchDetector
{
    private const string GitDirPrefix = "gitdir:";

    /// <summary>
    /// The current branch of the repository at or above <paramref name="startPath"/>:
    /// <list type="bullet">
    /// <item><description><c>ref: refs/heads/&lt;name&gt;</c> → <c>&lt;name&gt;</c>
    /// verbatim (only the <c>refs/heads/</c> prefix is stripped, so
    /// <c>feature/foo</c> survives intact),</description></item>
    /// <item><description>detached HEAD (a 40/64-char hex SHA) → the first 12 characters
    /// of the SHA — a defined, deterministic branch partition value,</description></item>
    /// <item><description>no repository found walking up, or unreadable/unrecognized
    /// HEAD → null.</description></item>
    /// </list>
    /// </summary>
    public static string? TryDetect(string? startPath)
    {
        var gitDir = TryFindGitDir(startPath);
        return gitDir is null ? null : ReadHead(Path.Combine(gitDir, "HEAD"));
    }

    /// <summary>
    /// The working-tree root (the directory containing the <c>.git</c> entry — dir or
    /// file) for the repository at or above <paramref name="startPath"/>; null when no
    /// repository is found. This is the path <c>git -C</c> and worktree operations want.
    /// </summary>
    public static string? TryFindRepoRoot(string? startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath))
        {
            return null;
        }

        string full;
        try
        {
            full = Path.GetFullPath(startPath);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }

        for (var dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
             dir is not null;
             dir = Path.GetDirectoryName(dir))
        {
            var gitPath = Path.Combine(dir, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return dir;
            }
        }

        return null;
    }

    /// <summary>
    /// The PRIMARY working-tree root for the repository at or above
    /// <paramref name="startPath"/>. For an ordinary checkout this equals
    /// <see cref="TryFindRepoRoot"/>. For a LINKED WORKTREE — a <c>.git</c> FILE whose
    /// gitdir carries a <c>commondir</c> pointer — it resolves back to the main working
    /// tree's root, so per-repo derivations (the sqlite store-per-solution path) land on
    /// ONE store no matter which worktree of the repo produced the path. A submodule's
    /// <c>.git</c>-file gitdir has no <c>commondir</c> and keeps its own root on
    /// purpose: a submodule IS a distinct repository and deserves a distinct store.
    /// Null when no repository is found; any unreadable/unrecognized plumbing falls
    /// back to the found root rather than failing.
    /// </summary>
    public static string? TryFindPrimaryRepoRoot(string? startPath)
    {
        var root = TryFindRepoRoot(startPath);
        if (root is null)
        {
            return null;
        }

        var gitPath = Path.Combine(root, ".git");
        if (Directory.Exists(gitPath))
        {
            return root; // ordinary layout — this root is the primary one
        }

        var gitDir = File.Exists(gitPath) ? ResolveGitFile(gitPath, root) : null;
        if (gitDir is null)
        {
            return root;
        }

        try
        {
            // Worktree gitdirs (<main>/.git/worktrees/<name>) carry a 'commondir' file
            // pointing (usually "../..") at the shared .git directory; its parent is the
            // main working tree. Submodule gitdirs have no commondir — keep their root.
            var commonDirFile = Path.Combine(gitDir, "commondir");
            if (!File.Exists(commonDirFile))
            {
                return root;
            }

            var common = (File.ReadLines(commonDirFile).FirstOrDefault() ?? "").Trim();
            if (common.Length == 0)
            {
                return root;
            }

            var commonGitDir = Path.TrimEndingDirectorySeparator(Path.IsPathRooted(common)
                ? Path.GetFullPath(common)
                : Path.GetFullPath(Path.Combine(gitDir, common)));
            return Path.GetDirectoryName(commonGitDir) ?? root;
        }
        catch (IOException)
        {
            return root;
        }
        catch (UnauthorizedAccessException)
        {
            return root;
        }
    }

    /// <summary>
    /// The git directory (the one containing this checkout's <c>HEAD</c>) for the
    /// repository at or above <paramref name="startPath"/>, resolving the worktree
    /// <c>.git</c>-file indirection; null when no repository is found. The watch loop
    /// uses this to observe <c>HEAD</c> for branch switches.
    /// </summary>
    public static string? TryFindGitDir(string? startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath))
        {
            return null;
        }

        string full;
        try
        {
            full = Path.GetFullPath(startPath);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }

        for (var dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
             dir is not null;
             dir = Path.GetDirectoryName(dir))
        {
            var gitPath = Path.Combine(dir, ".git");
            if (Directory.Exists(gitPath))
            {
                return gitPath;
            }

            if (File.Exists(gitPath) && ResolveGitFile(gitPath, dir) is { } resolved)
            {
                return resolved;
            }
        }

        return null;
    }

    /// <summary>
    /// A <c>.git</c> FILE (worktree/submodule layout) contains a single
    /// <c>gitdir: &lt;path&gt;</c> line; relative paths resolve against the directory
    /// containing the file. The pointed-at directory holds this worktree's own HEAD.
    /// </summary>
    private static string? ResolveGitFile(string gitFilePath, string containingDir)
    {
        string firstLine;
        try
        {
            firstLine = File.ReadLines(gitFilePath).FirstOrDefault() ?? "";
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (!firstLine.StartsWith(GitDirPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var target = firstLine[GitDirPrefix.Length..].Trim();
        if (target.Length == 0)
        {
            return null;
        }

        return Path.IsPathRooted(target)
            ? target
            : Path.GetFullPath(Path.Combine(containingDir, target));
    }

    private static string? ReadHead(string headPath)
    {
        string head;
        try
        {
            if (!File.Exists(headPath))
            {
                return null;
            }

            head = (File.ReadLines(headPath).FirstOrDefault() ?? "").Trim();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        const string refPrefix = "ref: refs/heads/";
        if (head.StartsWith(refPrefix, StringComparison.Ordinal))
        {
            var name = head[refPrefix.Length..];
            return name.Length > 0 ? name : null;
        }

        if (head.Length is 40 or 64 && head.All(Uri.IsHexDigit))
        {
            return head[..12]; // detached HEAD → short-SHA branch value.
        }

        return null;
    }
}
