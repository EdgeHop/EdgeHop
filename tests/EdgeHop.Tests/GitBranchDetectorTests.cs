using EdgeHop.Core;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// Phase 5 checkpoint — branch detection over synthetic <c>.git</c> layouts written to a
/// throwaway temp directory. No <c>git.exe</c>, no real repository, no database.
/// </summary>
public sealed class GitBranchDetectorTests : IDisposable
{
    private readonly string _root;

    public GitBranchDetectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"edgehop-gitdetect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a throwaway temp dir.
        }
    }

    private string MakeRepo(string name, string headContent)
    {
        var repo = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), headContent);
        return repo;
    }

    [Fact]
    public void Ordinary_head_ref_resolves_to_the_branch_name()
    {
        var repo = MakeRepo("plain", "ref: refs/heads/master\n");
        Assert.Equal("master", GitBranchDetector.TryDetect(repo));
    }

    [Fact]
    public void Branch_names_containing_slashes_survive_intact()
    {
        var repo = MakeRepo("slashes", "ref: refs/heads/feature/foo\n");
        Assert.Equal("feature/foo", GitBranchDetector.TryDetect(repo));
    }

    [Fact]
    public void Detached_head_resolves_to_a_twelve_char_sha_prefix()
    {
        var sha = "0123456789abcdef0123456789abcdef01234567";
        var repo = MakeRepo("detached", sha + "\n");
        Assert.Equal("0123456789ab", GitBranchDetector.TryDetect(repo));
    }

    [Fact]
    public void Detection_walks_up_from_a_nested_directory()
    {
        var repo = MakeRepo("nested", "ref: refs/heads/UI\n");
        var deep = Path.Combine(repo, "src", "Web", "Components");
        Directory.CreateDirectory(deep);

        Assert.Equal("UI", GitBranchDetector.TryDetect(deep));
    }

    [Fact]
    public void Worktree_git_file_with_relative_gitdir_resolves_the_per_worktree_head()
    {
        // Main repo holds the worktree gitdir; the worktree has a .git FILE pointing at it.
        var main = MakeRepo("wt-main", "ref: refs/heads/master\n");
        var worktreeGitDir = Path.Combine(main, ".git", "worktrees", "wt");
        Directory.CreateDirectory(worktreeGitDir);
        File.WriteAllText(Path.Combine(worktreeGitDir, "HEAD"), "ref: refs/heads/Simplified\n");

        var worktree = Path.Combine(_root, "wt-checkout");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(
            Path.Combine(worktree, ".git"),
            $"gitdir: ../wt-main/.git/worktrees/wt\n");

        Assert.Equal("Simplified", GitBranchDetector.TryDetect(worktree));
    }

    [Fact]
    public void Worktree_git_file_with_absolute_gitdir_resolves_the_per_worktree_head()
    {
        var main = MakeRepo("wt-abs-main", "ref: refs/heads/master\n");
        var worktreeGitDir = Path.Combine(main, ".git", "worktrees", "wt");
        Directory.CreateDirectory(worktreeGitDir);
        File.WriteAllText(Path.Combine(worktreeGitDir, "HEAD"), "ref: refs/heads/UI\n");

        var worktree = Path.Combine(_root, "wt-abs-checkout");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {worktreeGitDir}\n");

        Assert.Equal("UI", GitBranchDetector.TryDetect(worktree));
    }

    [Fact]
    public void No_repository_or_unrecognized_head_returns_null()
    {
        var plainDir = Path.Combine(_root, "no-repo", "sub");
        Directory.CreateDirectory(plainDir);
        Assert.Null(GitBranchDetector.TryDetect(plainDir));

        var garbage = MakeRepo("garbage", "not a head at all\n");
        Assert.Null(GitBranchDetector.TryDetect(garbage));

        Assert.Null(GitBranchDetector.TryDetect(null));
        Assert.Null(GitBranchDetector.TryDetect("   "));
    }
}
