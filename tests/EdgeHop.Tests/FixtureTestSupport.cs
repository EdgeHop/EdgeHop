using System.Diagnostics;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// Shared plumbing for MSBuildWorkspace-based fixture classes: the process-wide MSBuild
/// locator guard, fixture-solution location, and <c>dotnet restore</c>.
/// </summary>
internal static class FixtureTestSupport
{
    private static int s_bootstrapped;

    /// <summary>
    /// Registers the MSBuild locator exactly once per process, before anything that
    /// touches workspace types runs. <c>MsBuildBootstrap</c> itself guards via
    /// <c>MSBuildLocator.IsRegistered</c>; the interlocked flag additionally guards
    /// repeat fixture construction in-process.
    /// </summary>
    public static void EnsureMsBuildRegistered()
    {
        if (Interlocked.Exchange(ref s_bootstrapped, 1) == 0)
        {
            EdgeHop.Roslyn.MsBuildBootstrap.EnsureRegistered();
        }
    }

    /// <summary>
    /// Locates <c>fixtures\{fixtureDirectory}\{solutionFileName}</c> by walking up from
    /// <see cref="AppContext.BaseDirectory"/>. The fixtures live under the
    /// <c>EdgeHop.Tests</c> project; this resolves them whether the running test assembly
    /// is <c>EdgeHop.Tests</c> itself (the <c>EdgeHop.Tests.csproj</c> directory is on
    /// the walk-up path) or a sibling test project such as <c>EdgeHop.Neo4j.Tests</c> (in
    /// which case the repo root is found via <c>EdgeHop.sln</c> and the fixtures are read
    /// from the adjacent <c>EdgeHop.Tests\fixtures</c> directory).
    /// </summary>
    public static string LocateFixtureSolution(string fixtureDirectory, string solutionFileName)
    {
        DirectoryInfo? repoRoot = null;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            // Fast path: running from the EdgeHop.Tests output — fixtures sit beside its csproj.
            if (File.Exists(Path.Combine(dir.FullName, "EdgeHop.Tests.csproj")))
            {
                return RequireFixture(dir.FullName, fixtureDirectory, solutionFileName);
            }

            // Remember the repo root (the directory holding the solution) for the fallback below.
            if (repoRoot is null && File.Exists(Path.Combine(dir.FullName, "EdgeHop.sln")))
            {
                repoRoot = dir;
            }
        }

        // Fallback: a sibling test project (e.g. EdgeHop.Neo4j.Tests) whose output does not
        // sit under EdgeHop.Tests — resolve the fixtures from the repo-root EdgeHop.Tests dir.
        if (repoRoot is not null)
        {
            return RequireFixture(
                Path.Combine(repoRoot.FullName, "tests", "EdgeHop.Tests"), fixtureDirectory, solutionFileName);
        }

        throw new InvalidOperationException(
            "Could not locate EdgeHop.Tests.csproj or EdgeHop.sln walking up from "
            + $"'{AppContext.BaseDirectory}'. The fixture tests must run from a test "
            + "project's build output within the repository.");
    }

    /// <summary>
    /// Locates the fixture DIRECTORY <c>fixtures\{fixtureDirectory}</c> (no solution file) —
    /// used by the bare-directory index anchor (a pure JS/TS tree). Same walk-up resolution as
    /// <see cref="LocateFixtureSolution"/>.
    /// </summary>
    public static string LocateFixtureDirectory(string fixtureDirectory)
    {
        DirectoryInfo? repoRoot = null;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EdgeHop.Tests.csproj")))
            {
                return RequireFixtureDirectory(dir.FullName, fixtureDirectory);
            }

            if (repoRoot is null && File.Exists(Path.Combine(dir.FullName, "EdgeHop.sln")))
            {
                repoRoot = dir;
            }
        }

        if (repoRoot is not null)
        {
            return RequireFixtureDirectory(Path.Combine(repoRoot.FullName, "tests", "EdgeHop.Tests"), fixtureDirectory);
        }

        throw new InvalidOperationException(
            "Could not locate EdgeHop.Tests.csproj or EdgeHop.sln walking up from "
            + $"'{AppContext.BaseDirectory}'.");
    }

    private static string RequireFixtureDirectory(string testProjectDir, string fixtureDirectory)
    {
        var dir = Path.Combine(testProjectDir, "fixtures", fixtureDirectory);
        if (!Directory.Exists(dir))
        {
            throw new DirectoryNotFoundException(
                $"Resolved the EdgeHop.Tests project directory at '{testProjectDir}' but the "
                + $"fixture directory is missing at '{dir}'.");
        }

        return dir;
    }

    private static string RequireFixture(string testProjectDir, string fixtureDirectory, string solutionFileName)
    {
        var sln = Path.Combine(testProjectDir, "fixtures", fixtureDirectory, solutionFileName);
        if (!File.Exists(sln))
        {
            throw new FileNotFoundException(
                $"Resolved the EdgeHop.Tests project directory at '{testProjectDir}' but the "
                + $"fixture solution is missing at '{sln}'.", sln);
        }

        return sln;
    }

    // ------------------------------------------------------------------ samples --------

    /// <summary>
    /// Locates a runnable sample SOLUTION under <c>tests/samples/{sampleName}/{solutionFileName}</c>
    /// (the self-documenting EdgeHop Explorer projects). Resolved from the repo root — the
    /// directory holding <c>EdgeHop.sln</c> — so it works from any test project's output.
    /// </summary>
    public static string LocateSampleSolution(string sampleName, string solutionFileName)
    {
        var sln = Path.Combine(LocateRepoRoot(), "tests", "samples", sampleName, solutionFileName);
        if (!File.Exists(sln))
        {
            throw new FileNotFoundException($"Sample solution not found at '{sln}'.", sln);
        }

        return sln;
    }

    /// <summary>
    /// Locates a sample DIRECTORY under <c>tests/samples/{sampleName}</c> (no solution file) — used
    /// by the pure JS/HTML sample, indexed as a bare directory exactly like
    /// <c>edgehop-extract index &lt;dir&gt;</c>.
    /// </summary>
    public static string LocateSampleDirectory(string sampleName)
    {
        var dir = Path.Combine(LocateRepoRoot(), "tests", "samples", sampleName);
        if (!Directory.Exists(dir))
        {
            throw new DirectoryNotFoundException($"Sample directory not found at '{dir}'.");
        }

        return dir;
    }

    private static string LocateRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EdgeHop.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Could not locate EdgeHop.sln walking up from '{AppContext.BaseDirectory}'.");
    }

    /// <summary>
    /// Runs <c>dotnet restore</c> on a fixture solution (asserting exit code 0) so
    /// MSBuildWorkspace can design-time-build it.
    /// </summary>
    public static async Task RestoreFixtureSolutionAsync(string solutionPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(solutionPath)!,
        };
        startInfo.ArgumentList.Add("restore");
        startInfo.ArgumentList.Add(solutionPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start 'dotnet restore' for the fixture solution.");

        // Drain both streams before waiting so neither pipe can fill and deadlock.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        Assert.True(
            process.ExitCode == 0,
            $"'dotnet restore \"{solutionPath}\"' exited with code {process.ExitCode}."
            + Environment.NewLine + "STDOUT:" + Environment.NewLine + stdout
            + Environment.NewLine + "STDERR:" + Environment.NewLine + stderr);
    }
}
