using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// Variant of <see cref="McpServerFixture"/> that exercises the DETECTION rung of the
/// branch-resolution chain instead of the pin: <c>EDGEHOP_BRANCH</c> is explicitly
/// removed and <c>EDGEHOP_REPO</c> points at a synthetic repo directory whose
/// <c>.git/HEAD</c> names the fixture's GUID test branch. The server must read the repo's
/// HEAD per call to find the seeded data.
/// </summary>
public sealed class McpRepoDetectFixture : McpServerFixture
{
    private string? _repoDir;

    protected override void ConfigureBranchEnvironment(ProcessStartInfo psi)
    {
        _repoDir = Path.Combine(Path.GetTempPath(), $"edgehop-mcp-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_repoDir, ".git"));
        File.WriteAllText(
            Path.Combine(_repoDir, ".git", "HEAD"),
            $"ref: refs/heads/{TestBranch}\n");

        psi.Environment.Remove("EDGEHOP_BRANCH");
        psi.Environment["EDGEHOP_REPO"] = _repoDir;
    }

    protected override Task DisposeExtraAsync()
    {
        if (_repoDir is not null)
        {
            try
            {
                Directory.Delete(_repoDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a throwaway temp dir.
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Proves the <c>EDGEHOP_REPO</c> → <c>.git/HEAD</c> detection path through the REAL
/// server process: the seeded GUID branch is discoverable only by reading the synthetic
/// repo's HEAD, so a hit on the seeded rows is proof the whole chain works end-to-end.
/// Skipped automatically when NEO4J_* environment variables are not set.
/// </summary>
public sealed class McpBranchDetectionTests : IClassFixture<McpRepoDetectFixture>
{
    private readonly McpRepoDetectFixture _fx;

    public McpBranchDetectionTests(McpRepoDetectFixture fx) => _fx = fx;

    [Neo4jFact]
    public async Task Server_detects_the_branch_from_the_repos_head_file()
    {
        var response = await _fx.CallToolAsync(
            "find_symbol", new Dictionary<string, object?> { ["query"] = "PartialThing" });

        Assert.False(response.TryGetProperty("error", out _));
        var result = response.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var isError)
            && isError.ValueKind == JsonValueKind.True);

        var payload = result.TryGetProperty("structuredContent", out var structured)
                      && structured.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? structured
            : JsonDocument.Parse(
                result.GetProperty("content").EnumerateArray()
                    .First(i => i.GetProperty("type").GetString() == "text")
                    .GetProperty("text").GetString()!).RootElement;

        // The PartialThing rows exist ONLY under the GUID branch named by the synthetic
        // repo's HEAD — three hits require the full detect chain to have run.
        Assert.Equal(3, payload.GetProperty("hits").GetArrayLength());
    }
}
