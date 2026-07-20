using System.Security.Cryptography;
using System.Text;

namespace EdgeHop.Core;

/// <summary>
/// SQLite backend settings. <b>No credentials exist for this backend</b> — the
/// never-handle-credentials rule is unchanged for Neo4j; here there is simply nothing to
/// handle: the store is a local file.
/// <para>
/// STORE-PER-SOLUTION (owner-approved, 2026-07-17): the default store path is derived
/// from the repository the caller is working against, so each solution gets its own
/// database file with branches inside it — multi-solution indexing needs no
/// configuration at all. Resolution precedence, mirroring <see cref="BranchResolver"/>:
/// explicit <c>EDGEHOP_SQLITE_PATH</c> &gt; repo of <c>EDGEHOP_REPO</c> &gt; repo of
/// the natural path (solution dir for the indexer, cwd for the CLI) &gt; the shared
/// legacy file. Derived files live under <c>%LOCALAPPDATA%\EdgeHop\stores\</c> named
/// <c>&lt;repo-dir-name&gt;-&lt;8-hex-path-hash&gt;.db</c> — the same naming scheme as the
/// worktree cache, so the same repo always maps to the same store no matter which head
/// (indexer, CLI, MCP server) derives it.
/// </para>
/// </summary>
/// <param name="DatabasePath">Full path of the SQLite database file.</param>
public sealed record SqliteSettings(string DatabasePath)
{
    private const string PathVar = "EDGEHOP_SQLITE_PATH";

    private static string EdgeHopRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EdgeHop");

    /// <summary>Fallback when no explicit path is set and no repository can be found
    /// from the environment or the caller's natural path: one shared store.</summary>
    public static string DefaultDatabasePath => Path.Combine(EdgeHopRoot, "edgehop.db");

    /// <summary>
    /// Reads settings from the environment. Unlike Neo4j this can never fail: an unset
    /// <c>EDGEHOP_SQLITE_PATH</c> selects the store derived from
    /// <paramref name="pathHint"/> (see class remarks), or the shared default when no
    /// repository is found.
    /// </summary>
    /// <param name="pathHint">The caller's natural path — the solution directory for the
    /// indexer, the current directory for the CLI, null for the MCP server (which is
    /// pointed at its repo via <c>EDGEHOP_REPO</c> instead).</param>
    public static SqliteSettings FromEnvironment(string? pathHint = null)
    {
        var explicitPath = Environment.GetEnvironmentVariable(PathVar);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return new SqliteSettings(explicitPath);
        }

        var seed = ResolveStoreSeed(pathHint);
        return new SqliteSettings(seed is null ? DefaultDatabasePath : DerivedStorePath(seed));
    }

    /// <summary>
    /// The directory identifying "which solution": the PRIMARY repo root of
    /// <c>EDGEHOP_REPO</c> when set (falling back to that path itself — the developer
    /// pointed at it explicitly), else the primary repo root found walking up from
    /// <paramref name="pathHint"/>. "Primary" (see
    /// <see cref="GitBranchDetector.TryFindPrimaryRepoRoot"/>) means a linked git
    /// worktree hashes back to its main working tree — every worktree of a repo shares
    /// the repo's ONE store. Null when neither yields a repository — a bare directory
    /// hint deliberately does NOT get its own store, so ad-hoc invocations from random
    /// directories share the stable default instead of scattering single-use store
    /// files.
    /// </summary>
    private static string? ResolveStoreSeed(string? pathHint)
    {
        var repoEnv = Environment.GetEnvironmentVariable(BranchResolver.RepoEnvVar);
        if (!string.IsNullOrWhiteSpace(repoEnv))
        {
            return GitBranchDetector.TryFindPrimaryRepoRoot(repoEnv) ?? Path.GetFullPath(repoEnv);
        }

        return string.IsNullOrWhiteSpace(pathHint)
            ? null
            : GitBranchDetector.TryFindPrimaryRepoRoot(pathHint);
    }

    /// <summary>
    /// <c>%LOCALAPPDATA%\EdgeHop\stores\&lt;repo-dir-name&gt;-&lt;8-hex-hash&gt;.db</c>.
    /// The hash is over the lowercased full path (Windows paths are case-insensitive),
    /// the same scheme as the worktree cache; the readable name prefix is for humans,
    /// the hash is the identity.
    /// </summary>
    private static string DerivedStorePath(string seedDirectory)
    {
        var fullSeed = Path.GetFullPath(seedDirectory);
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(fullSeed.ToLowerInvariant())))[..8]
            .ToLowerInvariant();

        var name = Path.GetFileName(fullSeed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name))
        {
            name = "repo"; // drive roots have no leaf name
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return Path.Combine(EdgeHopRoot, "stores", $"{name}-{hash}.db");
    }
}
