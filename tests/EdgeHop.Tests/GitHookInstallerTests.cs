using System.Diagnostics;
using EdgeHop.Roslyn;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// Handoff 4 checkpoint — <see cref="GitHookInstaller"/> against a scratch repository
/// built with <c>git init</c> in a temp directory (mirrors
/// <see cref="WorktreeManagerTests"/>; <see cref="GitFactAttribute"/> skips when git is
/// absent). Proves: all three hooks are written with a managed, marker-delimited block;
/// re-install is idempotent; an existing UNMANAGED hook is never overwritten (and the
/// refusal writes nothing at all); and uninstall removes only the managed block,
/// preserving surrounding user content and deleting a hook only when nothing but the
/// shebang remains.
/// </summary>
public sealed class GitHookInstallerTests : IAsyncLifetime
{
    private const string SolutionFileName = "Scratch.sln";

    private string _repo = string.Empty;
    private string _sln = string.Empty;

    public async Task InitializeAsync()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"edgehop-hooks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repo);
        _sln = Path.Combine(_repo, SolutionFileName);
        File.WriteAllText(_sln, "Microsoft Visual Studio Solution File, Format Version 12.00\n");
        await GitAsync("init", "--initial-branch=main");
    }

    public Task DisposeAsync()
    {
        TryDeleteDirectory(_repo);
        return Task.CompletedTask;
    }

    private string HooksDir => Path.Combine(_repo, ".git", "hooks");

    [GitFact]
    public async Task Install_writes_all_three_hooks_with_a_managed_block()
    {
        var exit = await GitHookInstaller.InstallAsync(_repo, _sln, TextWriter.Null, TextWriter.Null);
        Assert.Equal(ExtractorApp.ExitSuccess, exit);

        foreach (var name in GitHookInstaller.HookNames)
        {
            var path = Path.Combine(HooksDir, name);
            Assert.True(File.Exists(path), $"expected hook '{path}'");
            var content = File.ReadAllText(path);

            Assert.StartsWith("#!/bin/sh", content, StringComparison.Ordinal);
            Assert.Contains("edgehop hooks (managed block", content, StringComparison.Ordinal);
            // Detached, output-suppressed invocation of THIS exe's index verb on the sln.
            Assert.Contains("index", content, StringComparison.Ordinal);
            Assert.Contains(SolutionFileName, content, StringComparison.Ordinal);
            Assert.Contains(">/dev/null 2>&1 &", content, StringComparison.Ordinal);
        }

        // post-checkout must guard on $3 = 1 (branch checkout, not a file checkout);
        // the other two hooks fire unconditionally.
        var postCheckout = File.ReadAllText(Path.Combine(HooksDir, "post-checkout"));
        Assert.Contains("\"$3\" = \"1\"", postCheckout, StringComparison.Ordinal);
        Assert.DoesNotContain("\"$3\"", File.ReadAllText(Path.Combine(HooksDir, "post-commit")),
            StringComparison.Ordinal);
    }

    [GitFact]
    public async Task Reinstall_is_idempotent_and_does_not_duplicate_the_block()
    {
        await GitHookInstaller.InstallAsync(_repo, _sln, TextWriter.Null, TextWriter.Null);
        await GitHookInstaller.InstallAsync(_repo, _sln, TextWriter.Null, TextWriter.Null);

        var content = File.ReadAllText(Path.Combine(HooksDir, "post-commit"));
        var occurrences = content.Split("managed block").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [GitFact]
    public async Task Install_refuses_to_touch_an_unmanaged_hook_and_writes_nothing()
    {
        // A pre-existing hook we did not author must be left exactly as-is.
        var postCommit = Path.Combine(HooksDir, "post-commit");
        const string userScript = "#!/bin/sh\necho \"my own hook\"\n";
        Directory.CreateDirectory(HooksDir);
        File.WriteAllText(postCommit, userScript);

        var error = new StringWriter();
        var exit = await GitHookInstaller.InstallAsync(_repo, _sln, TextWriter.Null, error);

        Assert.Equal(ExtractorApp.ExitUsageOrConfigError, exit);
        Assert.Equal(userScript, File.ReadAllText(postCommit)); // untouched
        Assert.Contains("does not manage", error.ToString(), StringComparison.Ordinal);

        // Refuse-before-write: the other hooks must NOT have been created.
        Assert.False(File.Exists(Path.Combine(HooksDir, "post-merge")));
        Assert.False(File.Exists(Path.Combine(HooksDir, "post-checkout")));
    }

    [GitFact]
    public async Task Uninstall_removes_the_managed_block_and_preserves_user_content()
    {
        await GitHookInstaller.InstallAsync(_repo, _sln, TextWriter.Null, TextWriter.Null);

        // Simulate a hook that mixes user content with our managed block: insert a user
        // line right after the shebang, keeping the managed block intact.
        var postCommit = Path.Combine(HooksDir, "post-commit");
        var content = File.ReadAllText(postCommit);
        content = content.Replace("#!/bin/sh\n", "#!/bin/sh\necho \"user line\"\n", StringComparison.Ordinal);
        File.WriteAllText(postCommit, content);

        var exit = await GitHookInstaller.UninstallAsync(_repo, TextWriter.Null, TextWriter.Null);
        Assert.Equal(ExtractorApp.ExitSuccess, exit);

        // Mixed hook: file kept, managed block gone, the user line survives.
        Assert.True(File.Exists(postCommit));
        var after = File.ReadAllText(postCommit);
        Assert.DoesNotContain("managed block", after, StringComparison.Ordinal);
        Assert.Contains("user line", after, StringComparison.Ordinal);

        // Pure-managed hooks (only shebang + block) are removed entirely.
        Assert.False(File.Exists(Path.Combine(HooksDir, "post-merge")));
        Assert.False(File.Exists(Path.Combine(HooksDir, "post-checkout")));
    }

    [GitFact]
    public async Task Uninstall_leaves_unmanaged_hooks_alone()
    {
        var postCommit = Path.Combine(HooksDir, "post-commit");
        const string userScript = "#!/bin/sh\necho \"my own hook\"\n";
        Directory.CreateDirectory(HooksDir);
        File.WriteAllText(postCommit, userScript);

        var exit = await GitHookInstaller.UninstallAsync(_repo, TextWriter.Null, TextWriter.Null);

        Assert.Equal(ExtractorApp.ExitSuccess, exit);
        Assert.Equal(userScript, File.ReadAllText(postCommit));
    }

    // ---------------------------------------------------------------------- helpers --

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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
