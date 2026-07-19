using System.Diagnostics;
using System.Text;

namespace EdgeHop.Roslyn;

/// <summary>
/// Installs/removes the local git hooks behind <c>edgehop-extract install-hooks</c> —
/// the push-driven-re-indexing replacement: post-commit, post-merge and post-checkout
/// re-index the solution in a fire-and-forget background process, so the graph follows
/// commits, pulls and branch switches without a long-running <c>--watch</c> session.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>
/// Hooks are sh scripts (git for Windows runs them under its own sh), written with
/// forward-slashed absolute paths and a detached (<c>&amp;</c>) invocation of THIS
/// executable's <c>index</c> verb, so git returns immediately. post-checkout only fires
/// on branch checkouts (<c>$3 = 1</c>), not file checkouts.
/// </description></item>
/// <item><description>
/// Everything EdgeHop writes sits inside a marker-delimited managed block. An existing
/// hook that lacks the markers is NEVER touched — install refuses with instructions
/// instead of clobbering or appending to a script it does not own. Re-install replaces
/// only the managed block; <c>uninstall-hooks</c> removes only the managed block and
/// deletes the file only when nothing but the shebang remains.
/// </description></item>
/// <item><description>
/// The hooks directory comes from <c>git rev-parse --git-path hooks</c>, which respects
/// worktrees (hooks live in the primary gitdir) and a configured <c>core.hooksPath</c>.
/// </description></item>
/// <item><description>
/// Concurrent hook firings (e.g. a checkout during a rebase) are serialized by the
/// per-store index lock inside <see cref="IndexCommand"/>, keeping reconcile
/// single-writer-per-branch.
/// </description></item>
/// </list>
/// </remarks>
public static class GitHookInstaller
{
    private const string BeginMarker = "# >>> edgehop hooks (managed block - do not edit) >>>";
    private const string EndMarker = "# <<< edgehop hooks <<<";
    private const string Shebang = "#!/bin/sh";

    /// <summary>The hook points that re-index: commits, merges/pulls, branch switches.</summary>
    public static readonly IReadOnlyList<string> HookNames =
        ["post-commit", "post-merge", "post-checkout"];

    /// <summary>
    /// Installs (or refreshes) the managed block in each hook. Returns an
    /// <see cref="ExtractorApp"/> exit code; refuses the WHOLE install when any existing
    /// hook lacks the managed markers.
    /// </summary>
    public static async Task<int> InstallAsync(
        string repoRoot,
        string solutionPath,
        TextWriter output,
        TextWriter error)
    {
        var extractorPath = Environment.ProcessPath;
        if (extractorPath is null)
        {
            error.WriteLine("Cannot determine the edgehop-extract executable path.");
            return ExtractorApp.ExitUsageOrConfigError;
        }

        var hooksDir = await ResolveHooksDirAsync(repoRoot, error).ConfigureAwait(false);
        if (hooksDir is null)
        {
            return ExtractorApp.ExitUsageOrConfigError;
        }

        // Refuse-before-write: no hook is modified unless every hook is writable by us.
        var unmanaged = HookNames
            .Select(name => Path.Combine(hooksDir, name))
            .Where(path => File.Exists(path) && !File.ReadAllText(path).Contains(BeginMarker, StringComparison.Ordinal))
            .ToList();
        if (unmanaged.Count > 0)
        {
            error.WriteLine(
                "install-hooks refuses to modify existing hook script(s) it does not manage:");
            foreach (var path in unmanaged)
            {
                error.WriteLine($"  {path}");
            }

            error.WriteLine(
                "Add the managed block manually (see README 'Git-hook re-indexing') or move the "
                + "existing hooks aside and re-run.");
            return ExtractorApp.ExitUsageOrConfigError;
        }

        Directory.CreateDirectory(hooksDir);
        foreach (var name in HookNames)
        {
            var path = Path.Combine(hooksDir, name);
            var block = BuildManagedBlock(name, extractorPath, solutionPath);
            var content = File.Exists(path)
                ? ReplaceManagedBlock(File.ReadAllText(path), block)
                : Shebang + "\n" + block;
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            output.WriteLine($"Installed: {path}");
        }

        output.WriteLine(
            $"Hooks re-index '{solutionPath}' in the background after commits, merges and "
            + "branch checkouts. Remove them with 'edgehop-extract uninstall-hooks'.");
        return ExtractorApp.ExitSuccess;
    }

    /// <summary>
    /// Removes the managed block from every hook (deleting a hook file only when nothing
    /// but the shebang remains). Unmanaged hooks are reported and left untouched.
    /// </summary>
    public static async Task<int> UninstallAsync(string repoRoot, TextWriter output, TextWriter error)
    {
        var hooksDir = await ResolveHooksDirAsync(repoRoot, error).ConfigureAwait(false);
        if (hooksDir is null)
        {
            return ExtractorApp.ExitUsageOrConfigError;
        }

        foreach (var name in HookNames)
        {
            var path = Path.Combine(hooksDir, name);
            if (!File.Exists(path))
            {
                continue;
            }

            var content = File.ReadAllText(path);
            if (!content.Contains(BeginMarker, StringComparison.Ordinal))
            {
                output.WriteLine($"Skipped (not managed by edgehop): {path}");
                continue;
            }

            var remaining = ReplaceManagedBlock(content, string.Empty);
            if (remaining.Trim() is "" or Shebang)
            {
                File.Delete(path);
                output.WriteLine($"Removed: {path}");
            }
            else
            {
                File.WriteAllText(path, remaining, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                output.WriteLine($"Removed managed block from: {path}");
            }
        }

        return ExtractorApp.ExitSuccess;
    }

    /// <summary>The managed block for one hook: marker, detached index invocation
    /// (branch resolution is the ordinary git-based one at fire time), end marker.
    /// post-checkout additionally guards on <c>$3 = 1</c> (branch, not file, checkout).</summary>
    internal static string BuildManagedBlock(string hookName, string extractorPath, string solutionPath)
    {
        var exe = ToShPath(extractorPath);
        var sln = ToShPath(solutionPath);
        var command = $"\"{exe}\" index \"{sln}\" >/dev/null 2>&1 &";
        var body = hookName == "post-checkout"
            ? $"if [ \"$3\" = \"1\" ]; then\n  {command}\nfi"
            : command;
        return $"{BeginMarker}\n{body}\n{EndMarker}\n";
    }

    /// <summary>Replaces the marker-delimited block (inclusive) with
    /// <paramref name="replacement"/>; appends the block when no markers exist.</summary>
    internal static string ReplaceManagedBlock(string content, string replacement)
    {
        var begin = content.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (begin < 0)
        {
            var separator = content.EndsWith('\n') ? string.Empty : "\n";
            return content + separator + replacement;
        }

        var end = content.IndexOf(EndMarker, begin, StringComparison.Ordinal);
        var afterEnd = end < 0
            ? content.Length
            : Math.Min(content.Length, end + EndMarker.Length + 1); // +1 swallows the newline
        return content[..begin] + replacement + content[afterEnd..];
    }

    /// <summary>git-for-Windows sh handles <c>C:/forward/slashed</c> paths; backslashes
    /// invite escaping surprises inside double quotes.</summary>
    private static string ToShPath(string path) =>
        Path.GetFullPath(path).Replace('\\', '/');

    /// <summary>
    /// The absolute hooks directory via <c>git rev-parse --git-path hooks</c> — correct
    /// for worktrees and <c>core.hooksPath</c> alike. Null (with a logged reason) when
    /// git is unavailable or the directory is not a repository.
    /// </summary>
    private static async Task<string?> ResolveHooksDirAsync(string repoRoot, TextWriter error)
    {
        if (!Directory.Exists(repoRoot))
        {
            error.WriteLine($"Repository directory not found: {repoRoot}");
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("rev-parse");
        startInfo.ArgumentList.Add("--git-path");
        startInfo.ArgumentList.Add("hooks");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                error.WriteLine("Failed to start git.");
                return null;
            }

            var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                error.WriteLine($"'git rev-parse --git-path hooks' failed in '{repoRoot}': {stderr.Trim()}");
                return null;
            }

            var hooks = stdout.Trim();
            return Path.IsPathRooted(hooks) ? hooks : Path.GetFullPath(Path.Combine(repoRoot, hooks));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            error.WriteLine("git was not found on PATH; install-hooks requires git.");
            return null;
        }
    }
}
