using EdgeHop.Core;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// Phase 5 checkpoint — the branch-resolution precedence chain:
/// explicit &gt; EDGEHOP_BRANCH &gt; detect(EDGEHOP_REPO) &gt; detect(pathHint) &gt; "main".
/// Env vars are set and restored around each assertion. The named collection serializes
/// this class against every other in-process reader/mutator of the EDGEHOP_* process
/// environment (see <see cref="SqliteSettingsDerivationTests"/>) — without it the
/// classes race each other under xUnit's parallel class execution.
/// </summary>
[Collection("edgehop-env-vars")]
public sealed class BranchResolverTests : IDisposable
{
    private readonly string _root;

    public BranchResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"edgehop-resolver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BranchResolver.BranchEnvVar, null);
        Environment.SetEnvironmentVariable(BranchResolver.RepoEnvVar, null);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a throwaway temp dir.
        }
    }

    private string MakeRepo(string name, string branch)
    {
        var repo = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), $"ref: refs/heads/{branch}\n");
        return repo;
    }

    [Fact]
    public void Explicit_branch_beats_everything()
    {
        Environment.SetEnvironmentVariable(BranchResolver.BranchEnvVar, "from-env");
        Environment.SetEnvironmentVariable(BranchResolver.RepoEnvVar, MakeRepo("r1", "from-repo"));

        Assert.Equal("explicit", BranchResolver.Resolve("explicit", MakeRepo("r2", "from-hint")));
    }

    [Fact]
    public void Branch_env_var_beats_detection()
    {
        Environment.SetEnvironmentVariable(BranchResolver.BranchEnvVar, "pinned");
        Environment.SetEnvironmentVariable(BranchResolver.RepoEnvVar, MakeRepo("r3", "from-repo"));

        Assert.Equal("pinned", BranchResolver.Resolve(null, MakeRepo("r4", "from-hint")));
    }

    [Fact]
    public void Repo_env_var_beats_the_path_hint()
    {
        Environment.SetEnvironmentVariable(BranchResolver.BranchEnvVar, null);
        Environment.SetEnvironmentVariable(BranchResolver.RepoEnvVar, MakeRepo("r5", "from-repo"));

        Assert.Equal("from-repo", BranchResolver.Resolve(null, MakeRepo("r6", "from-hint")));
    }

    [Fact]
    public void Path_hint_is_used_when_no_env_is_set()
    {
        Environment.SetEnvironmentVariable(BranchResolver.BranchEnvVar, null);
        Environment.SetEnvironmentVariable(BranchResolver.RepoEnvVar, null);

        Assert.Equal("from-hint", BranchResolver.Resolve(null, MakeRepo("r7", "from-hint")));
    }

    [Fact]
    public void Everything_unset_falls_back_to_main()
    {
        Environment.SetEnvironmentVariable(BranchResolver.BranchEnvVar, null);
        Environment.SetEnvironmentVariable(BranchResolver.RepoEnvVar, null);

        var nonRepo = Path.Combine(_root, "not-a-repo");
        Directory.CreateDirectory(nonRepo);

        Assert.Equal("main", BranchResolver.Resolve(null, nonRepo));
        Assert.Equal("main", BranchResolver.Resolve(null, null));
        Assert.Equal("main", BranchResolver.Resolve("  ", null));
    }

    [Fact]
    public void Unresolvable_repo_env_var_falls_through_to_the_hint()
    {
        Environment.SetEnvironmentVariable(BranchResolver.BranchEnvVar, null);
        Environment.SetEnvironmentVariable(
            BranchResolver.RepoEnvVar, Path.Combine(_root, "does-not-exist"));

        Assert.Equal("from-hint", BranchResolver.Resolve(null, MakeRepo("r8", "from-hint")));
    }
}
