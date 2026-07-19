using System.Diagnostics;
using EdgeHop.Roslyn;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips when <c>git.exe</c> is not available on PATH
/// (mirrors the skip-not-fail style of the Neo4j-gated facts). Worktree operations
/// shell out to git; everything else in EdgeHop reads repository files directly.
/// </summary>
public sealed class GitFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> s_gitAvailable = new(() =>
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                ArgumentList = { "--version" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            process?.WaitForExit(10_000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    });

    public GitFactAttribute()
    {
        if (!s_gitAvailable.Value)
        {
            Skip = "git.exe is not available on PATH; worktree tests skipped.";
        }
    }
}

/// <summary>
/// Phase 8 checkpoint — <see cref="WorktreeManager"/> against a scratch repository built
/// with <c>git init</c> in a temp directory. The scratch repo contains a trivial
/// solution-marker file (a real MSBuild solution is not needed: EnsureAsync only checks
/// existence and runs <c>dotnet restore</c>, which succeeds on an empty solution file).
/// </summary>
public sealed class WorktreeManagerTests : IAsyncLifetime
{
    private const string SolutionFileName = "Scratch.sln";

    private string _repo = string.Empty;
    private readonly List<string> _worktreesToRemove = [];

    public async Task InitializeAsync()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"edgehop-worktree-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repo);

        await GitAsync("init", "--initial-branch=main");
        File.WriteAllText(Path.Combine(_repo, SolutionFileName), MinimalSolutionText());
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "one");
        await GitAsync("add", ".");
        await GitAsync("-c", "user.email=test@example.com", "-c", "user.name=test",
            "commit", "-m", "initial");
        await GitAsync("branch", "other");
        await GitAsync("branch", "feature/x");
    }

    public async Task DisposeAsync()
    {
        foreach (var worktree in _worktreesToRemove)
        {
            try
            {
                await GitAsync("worktree", "remove", "--force", worktree);
            }
            catch
            {
                // Repo dir may already be gone; directory cleanup below still runs.
            }

            TryDeleteDirectory(worktree);
        }

        TryDeleteDirectory(_repo);
    }

    [GitFact]
    public async Task Ensure_creates_a_worktree_for_a_local_branch_outside_the_repo()
    {
        var info = await EnsureAsync("other");

        Assert.True(File.Exists(info.SolutionPath));
        Assert.Equal("other", EdgeHop.Core.GitBranchDetector.TryDetect(info.Path));
        Assert.False(info.Path.StartsWith(_repo, StringComparison.OrdinalIgnoreCase),
            $"Worktree '{info.Path}' must live outside the repository '{_repo}'.");
        Assert.Equal(WorktreeManager.GetWorktreePath(_repo, "other"), info.Path);
    }

    [GitFact]
    public async Task Ensure_reuse_hard_refreshes_the_private_cache()
    {
        var info = await EnsureAsync("other");

        // Mutate the worktree: modify a tracked file and add an untracked one. The
        // worktree is a private cache — reuse must reset both.
        File.WriteAllText(Path.Combine(info.Path, "a.txt"), "MUTATED");
        File.WriteAllText(Path.Combine(info.Path, "untracked.txt"), "junk");

        var again = await EnsureAsync("other");

        Assert.Equal(info.Path, again.Path);
        Assert.Equal("one", File.ReadAllText(Path.Combine(again.Path, "a.txt")));
        Assert.False(File.Exists(Path.Combine(again.Path, "untracked.txt")));
    }

    [GitFact]
    public async Task Missing_local_branch_yields_an_actionable_error()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => WorktreeManager.EnsureAsync(_repo, "does-not-exist", SolutionFileName));

        Assert.Contains("no local ref", ex.Message);
        Assert.Contains("git branch does-not-exist", ex.Message);
    }

    [GitFact]
    public async Task Branch_checked_out_in_the_main_tree_yields_an_actionable_error()
    {
        // 'main' is checked out in the scratch repo's main working tree.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => EnsureRawAsync("main"));

        Assert.Contains("already checked out", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("index it directly", ex.Message);
    }

    [GitFact]
    public async Task Branch_names_with_slashes_are_sanitized_into_one_directory()
    {
        var info = await EnsureAsync("feature/x");

        Assert.Equal("feature/x", EdgeHop.Core.GitBranchDetector.TryDetect(info.Path));
        Assert.Equal("feature_x", Path.GetFileName(info.Path));
    }

    // ---------------------------------------------------------------------- helpers --

    private async Task<WorktreeInfo> EnsureAsync(string branch)
    {
        var info = await EnsureRawAsync(branch);
        _worktreesToRemove.Add(info.Path);
        return info;
    }

    private async Task<WorktreeInfo> EnsureRawAsync(string branch)
    {
        // Track the computed path even on failure so a partially-created worktree
        // never outlives the test.
        var expectedPath = WorktreeManager.GetWorktreePath(_repo, branch);
        if (!_worktreesToRemove.Contains(expectedPath))
        {
            _worktreesToRemove.Add(expectedPath);
        }

        return await WorktreeManager.EnsureAsync(_repo, branch, SolutionFileName);
    }

    /// <summary>An empty-but-valid solution file: dotnet restore succeeds with nothing to do.</summary>
    private static string MinimalSolutionText() => """
        Microsoft Visual Studio Solution File, Format Version 12.00
        # Visual Studio Version 17
        Global
        EndGlobal
        """;

    private async Task GitAsync(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _repo,
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
            ?? throw new InvalidOperationException("Failed to start git.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0,
            $"git {string.Join(' ', args)} exited {process.ExitCode}:"
            + Environment.NewLine + await stdoutTask + Environment.NewLine + await stderrTask);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of throwaway temp dirs.
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only git object files can resist deletion; leave for OS temp cleanup.
        }
    }
}
