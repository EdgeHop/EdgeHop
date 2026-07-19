namespace EdgeHop.Core;

/// <summary>
/// The single branch-resolution rule shared by the extractor, the CLI, and the MCP
/// server: <b>explicit value &gt; <c>EDGEHOP_BRANCH</c> &gt; detect from
/// <c>EDGEHOP_REPO</c> &gt; detect from the caller's path hint &gt; <c>"main"</c></b>.
/// The fallback preserves the phase 1–3 behavior for non-git directories.
/// </summary>
public static class BranchResolver
{
    /// <summary>Env var pointing at the repository whose current branch queries should
    /// follow (set in <c>.mcp.json</c> for the MCP server).</summary>
    public const string RepoEnvVar = "EDGEHOP_REPO";

    /// <summary>Env var that pins the branch outright, bypassing detection (the
    /// injection seam tests use to point a server at a seeded branch).</summary>
    public const string BranchEnvVar = "EDGEHOP_BRANCH";

    /// <summary>The final fallback when nothing else resolves.</summary>
    public const string Fallback = "main";

    /// <summary>
    /// Resolves the branch per the precedence above. <paramref name="explicitBranch"/>
    /// is a user-supplied value (e.g. <c>--branch</c>); <paramref name="pathHint"/> is
    /// the caller's natural location (solution directory for the extractor, current
    /// directory for the CLI, null for the MCP server which is env-configured only).
    /// </summary>
    public static string Resolve(string? explicitBranch, string? pathHint)
    {
        if (!string.IsNullOrWhiteSpace(explicitBranch))
        {
            return explicitBranch;
        }

        var pinned = Environment.GetEnvironmentVariable(BranchEnvVar);
        if (!string.IsNullOrWhiteSpace(pinned))
        {
            return pinned;
        }

        var repo = Environment.GetEnvironmentVariable(RepoEnvVar);
        if (!string.IsNullOrWhiteSpace(repo)
            && GitBranchDetector.TryDetect(repo) is { } fromRepo)
        {
            return fromRepo;
        }

        if (GitBranchDetector.TryDetect(pathHint) is { } fromHint)
        {
            return fromHint;
        }

        return Fallback;
    }
}
