using EdgeHop.Core;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// Validation-path tests for <see cref="EdgeHopQueryService"/> that need NO live
/// database and no store backend at all: every case below throws (or returns empty)
/// before any query would execute, so a stand-in <see cref="IGraphReader"/> whose members
/// all throw (<see cref="NeverCalledReader"/>) proves the guard fires first. These run
/// everywhere — even where NEO4J_* is not configured — so the shared front-end contract
/// (what the MCP server and the CLI both inherit) stays covered. The live truncation/limit
/// tests that DO reach a reader live beside their store — see
/// <c>EdgeHopQueryServiceTruncationTests</c> in <c>EdgeHop.Neo4j.Tests</c> and
/// <c>SqliteStoreConformanceTests</c>.
/// </summary>
public sealed class EdgeHopQueryServiceValidationTests
{
    [Fact]
    public void Constructor_RequiresAReader()
        => Assert.Throws<ArgumentNullException>(() => new EdgeHopQueryService(null!));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(11)]
    [InlineData(99)]
    public async Task GetCallers_DepthOutsideOneToTen_Throws(int badDepth)
    {
        var service = new EdgeHopQueryService(new NeverCalledReader());

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.GetCallersAsync("main", "Method:x", badDepth));

        // Front ends surface this message verbatim (minus the parameter decoration);
        // pin the sentence so the user-facing text cannot silently degrade.
        Assert.Contains($"depth must be between 1 and 10; got {badDepth}.", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetCallers_EmptySymbolId_Throws(string badId)
    {
        var service = new EdgeHopQueryService(new NeverCalledReader());

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.GetCallersAsync("main", badId));

        Assert.Contains("symbolId", ex.Message);
    }

    [Fact]
    public async Task GetCallers_EmptyBranch_Throws()
    {
        var service = new EdgeHopQueryService(new NeverCalledReader());

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.GetCallersAsync(" ", "Method:x"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FindSymbols_EmptyQuery_ReturnsEmptyWithoutTouchingTheDatabase(string emptyQuery)
    {
        var service = new EdgeHopQueryService(new NeverCalledReader());

        // Would throw NotSupportedException if it reached the (never-called) reader.
        var result = await service.FindSymbolsAsync("main", emptyQuery);

        Assert.Empty(result.Hits);
        Assert.False(result.Truncated);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task FindSymbols_NonPositiveLimit_ReturnsEmptyWithoutTouchingTheDatabase(int badLimit)
    {
        var service = new EdgeHopQueryService(new NeverCalledReader());

        var result = await service.FindSymbolsAsync("main", "anything", limit: badLimit);

        Assert.Empty(result.Hits);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task CleanMessage_StripsTheBclDecorationFromTheServiceOwnExceptions()
    {
        // CleanMessage is the ONE canonical presenter both front ends must use; pin its
        // behavior on the exact exception the service throws for a bad depth.
        var service = new EdgeHopQueryService(new NeverCalledReader());

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.GetCallersAsync("main", "Method:x", 11));

        var cleaned = EdgeHopQueryService.CleanMessage(ex.Message);

        Assert.Equal("depth must be between 1 and 10; got 11.", cleaned);
        Assert.DoesNotContain("(Parameter", cleaned);
        Assert.DoesNotContain("Actual value", cleaned);
    }

    [Fact]
    public void CleanMessage_PassesUndecoratedMessagesThroughUnchanged()
        => Assert.Equal(
            "Neo4j error: connection refused.",
            EdgeHopQueryService.CleanMessage("Neo4j error: connection refused."));

    // ---- get_relationships validation ---------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(11)]
    [InlineData(99)]
    public async Task GetRelationships_DepthOutsideOneToTen_Throws(int badDepth)
    {
        var service = new EdgeHopQueryService(new NeverCalledReader());

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.GetRelationshipsAsync(
                "main", "Method:x", RelationshipDirection.Out, edgeType: EdgeTypes.Calls, depth: badDepth));

        // Same pinned sentence get_callers uses — both share IGraphReader.MaxRelationshipDepth.
        Assert.Contains($"depth must be between 1 and 10; got {badDepth}.", ex.Message);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    public async Task GetRelationships_DepthAboveOneWithoutEdgeType_Throws(int deepDepth)
    {
        var service = new EdgeHopQueryService(new NeverCalledReader());

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.GetRelationshipsAsync(
                "main", "Method:x", RelationshipDirection.Out, edgeType: null, depth: deepDepth));

        // Multi-hop mixed-type traversal is meaningless; the exact sentence is pinned so the
        // user-facing text (surfaced verbatim by both front ends) cannot silently degrade.
        Assert.Contains(
            "depth > 1 requires a single --edge-type; multi-hop mixed-type traversal is not supported.",
            ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetRelationships_DepthAboveOneWithWhitespaceEdgeType_Throws(string blankType)
    {
        // A whitespace edge type is no filter at all: the service normalizes it to null, so
        // depth > 1 still trips the single-edge-type rule rather than being treated as a type.
        var service = new EdgeHopQueryService(new NeverCalledReader());

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.GetRelationshipsAsync(
                "main", "Method:x", RelationshipDirection.Out, edgeType: blankType, depth: 2));

        Assert.Contains(
            "depth > 1 requires a single --edge-type; multi-hop mixed-type traversal is not supported.",
            ex.Message);
    }

    [Fact]
    public async Task GetRelationships_UnknownEdgeType_Throws()
    {
        var service = new EdgeHopQueryService(new NeverCalledReader());

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.GetRelationshipsAsync(
                "main", "Method:x", RelationshipDirection.Out, edgeType: "NOT_A_REAL_EDGE"));

        // Reuses the writer's message shape; the offending token and the valid set are listed.
        Assert.Contains("Unknown edge type 'NOT_A_REAL_EDGE'.", ex.Message);
        Assert.Contains("Valid types:", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetRelationships_EmptySymbolId_Throws(string badId)
    {
        var service = new EdgeHopQueryService(new NeverCalledReader());

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.GetRelationshipsAsync("main", badId, RelationshipDirection.Out));

        Assert.Contains("symbolId", ex.Message);
    }

    [Fact]
    public async Task GetRelationships_EmptyBranch_Throws()
    {
        var service = new EdgeHopQueryService(new NeverCalledReader());

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.GetRelationshipsAsync(" ", "Method:x", RelationshipDirection.Out));
    }

    // ---- get_path validation ------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(16)]
    [InlineData(99)]
    public async Task GetPath_MaxLengthOutsideOneToFifteen_Throws(int badLength)
    {
        var service = new EdgeHopQueryService(new NeverCalledReader());

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.GetPathAsync("main", "Method:a", "Method:b", maxLength: badLength));

        Assert.Contains($"maxLength must be between 1 and 15; got {badLength}.", ex.Message);
    }

    [Fact]
    public async Task GetPath_UnknownEdgeType_Throws()
    {
        var service = new EdgeHopQueryService(new NeverCalledReader());

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.GetPathAsync("main", "Method:a", "Method:b", edgeType: "NOT_A_REAL_EDGE"));

        Assert.Contains("Unknown edge type 'NOT_A_REAL_EDGE'.", ex.Message);
        Assert.Contains("Valid types:", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetPath_EmptyFromId_Throws(string badId)
    {
        var service = new EdgeHopQueryService(new NeverCalledReader());

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.GetPathAsync("main", badId, "Method:b"));

        Assert.Contains("fromId", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetPath_EmptyToId_Throws(string badId)
    {
        var service = new EdgeHopQueryService(new NeverCalledReader());

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.GetPathAsync("main", "Method:a", badId));

        Assert.Contains("toId", ex.Message);
    }

    [Fact]
    public async Task GetPath_EmptyBranch_Throws()
    {
        var service = new EdgeHopQueryService(new NeverCalledReader());

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.GetPathAsync(" ", "Method:a", "Method:b"));
    }

    // ---- graph_stats validation ---------------------------------------------------------

    [Fact]
    public async Task GetStats_EmptyBranch_Throws()
    {
        var service = new EdgeHopQueryService(new NeverCalledReader());

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.GetStatsAsync(" "));
    }

    [Theory]
    [InlineData(0, 1)]     // below the floor clamps up to 1
    [InlineData(-5, 1)]    // negatives clamp up to 1
    [InlineData(999, 50)]  // above IGraphReader.MaxTopN clamps down to 50
    public async Task GetStats_OutOfRangeTopN_ClampsSilentlyRatherThanThrowing(int requestedTopN, int expectedTopN)
    {
        // topN is CLAMPED into [1, MaxTopN], never rejected (unlike depth/maxLength). A
        // recording stub reader lets us prove — with no store at all — both that no
        // validation exception escapes and that the exact clamped value reaches the reader.
        var reader = new TopNRecordingReader();
        var service = new EdgeHopQueryService(reader);

        var stats = await service.GetStatsAsync("main", requestedTopN);

        Assert.Equal("main", stats.Branch);
        Assert.Equal(expectedTopN, reader.LastTopN);
    }

    /// <summary>
    /// Minimal in-memory <see cref="IGraphReader"/> for the clamp test: it touches no
    /// database, records the <c>topN</c> the service passes after clamping, and returns an
    /// empty <see cref="GraphStatsResult"/>. Only <see cref="GetStatsAsync"/> is exercised;
    /// the other members are never reached by these tests.
    /// </summary>
    private sealed class TopNRecordingReader : IGraphReader
    {
        public int LastTopN { get; private set; } = -1;

        public Task<GraphStatsResult> GetStatsAsync(string branch, int topN, CancellationToken ct = default)
        {
            LastTopN = topN;
            return Task.FromResult(new GraphStatsResult(branch, 0, 0, [], [], []));
        }

        public Task<IReadOnlyList<SymbolHit>> FindSymbolsAsync(
            string branch, string query, string? kind = null, int limit = 25, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SymbolHit>> GetCallersAsync(
            string branch, string symbolId, int depth = 1, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<RelationshipHit>> GetRelationshipsAsync(
            string branch, string symbolId, RelationshipDirection direction, string? edgeType,
            int depth, int limit, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<PathResult> GetPathAsync(
            string branch, string fromId, string toId, string? edgeType, int maxLength,
            CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// In-memory <see cref="IGraphReader"/> whose every member throws
    /// <see cref="NotSupportedException"/>. It stands in wherever a validation test must
    /// prove the guard fires BEFORE any reader call: if a guard ever regressed and let the
    /// call through, the reader would throw <see cref="NotSupportedException"/> instead of
    /// the expected <see cref="ArgumentException"/>/<see cref="ArgumentOutOfRangeException"/>
    /// and the test would fail loudly. No database — these tests run everywhere.
    /// </summary>
    private sealed class NeverCalledReader : IGraphReader
    {
        public Task<IReadOnlyList<SymbolHit>> FindSymbolsAsync(
            string branch, string query, string? kind = null, int limit = 25, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SymbolHit>> GetCallersAsync(
            string branch, string symbolId, int depth = 1, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<RelationshipHit>> GetRelationshipsAsync(
            string branch, string symbolId, RelationshipDirection direction, string? edgeType,
            int depth, int limit, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<PathResult> GetPathAsync(
            string branch, string fromId, string toId, string? edgeType, int maxLength,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<GraphStatsResult> GetStatsAsync(string branch, int topN, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
