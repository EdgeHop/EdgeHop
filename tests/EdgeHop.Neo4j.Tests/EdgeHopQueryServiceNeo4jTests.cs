using EdgeHop.Core;
using Neo4j.Driver;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// Class fixture for <see cref="EdgeHopQueryServiceTruncationTests"/>: seeds a
/// throwaway GUID branch with 26 method nodes sharing the searchable name stem
/// "BulkThing" plus one "LonelyThing", so truncation boundaries around the default
/// limit of 25 are exactly observable — plus <see cref="ManyCount"/> "ManyThing"
/// methods (more than <see cref="Neo4jGraphReader.MaxLimit"/>) so the
/// <see cref="EdgeHopQueryService.MaxRequestLimit"/> clamp boundary at 99 is
/// exactly observable too. Deletes ONLY that branch afterwards; nothing
/// here can touch branch 'main'. No-ops when Neo4j is not configured (all tests are
/// <see cref="Neo4jFactAttribute"/> and get skipped).
/// </summary>
public sealed class QueryServiceGraphFixture : IAsyncLifetime
{
    public const int BulkCount = 26;

    /// <summary>More matches than <see cref="Neo4jGraphReader.MaxLimit"/> (100), so a
    /// clamped search really is cut off and the 99-hit cap is observable.</summary>
    public const int ManyCount = Neo4jGraphReader.MaxLimit + 10;

    private IDriver? _driver;

    public string Branch { get; } = $"test-svc-{Guid.NewGuid():N}";

    public string Database { get; private set; } = "neo4j";

    public IDriver Driver => _driver ?? throw new InvalidOperationException(
        "Neo4j is not configured — [Neo4jFact] tests should have been skipped.");

    public EdgeHopQueryService Service => new(new Neo4jGraphReader(Driver, Database));

    public async Task InitializeAsync()
    {
        if (!Neo4jSettings.IsConfigured)
        {
            return;
        }

        var settings = Neo4jSettings.FromEnvironment();
        Database = settings.Database;
        _driver = GraphDatabase.Driver(settings.Uri, AuthTokens.Basic(settings.User, settings.Password));

        await Neo4jSchema.ApplyAsync(_driver, Database);

        var nodes = Enumerable.Range(0, BulkCount)
            .Select(i => new NodeRow(
                Branch,
                $"Method:void Svc.BulkThing{i:D2}()",
                $"void BulkThing{i:D2}()",
                SymbolKinds.Method,
                "Svc.cs",
                "SvcAssembly",
                IsAbstract: false))
            .Append(new NodeRow(
                Branch,
                "Method:void Svc.LonelyThing()",
                "void LonelyThing()",
                SymbolKinds.Method,
                "Svc.cs",
                "SvcAssembly",
                IsAbstract: false))
            .Concat(Enumerable.Range(0, ManyCount)
                .Select(i => new NodeRow(
                    Branch,
                    $"Method:void Svc.ManyThing{i:D3}()",
                    $"void ManyThing{i:D3}()",
                    SymbolKinds.Method,
                    "Svc.cs",
                    "SvcAssembly",
                    IsAbstract: false)))
            .ToArray();

        await new Neo4jGraphWriter(_driver, Database).UpsertNodesAsync(nodes);
    }

    public async Task DisposeAsync()
    {
        if (_driver is null)
        {
            return;
        }

        try
        {
            // Targeted cleanup: ONLY this run's unique GUID branch. Parameterized;
            // can never be 'main'.
            var session = _driver.AsyncSession(o => o.WithDatabase(Database));
            try
            {
                await session.ExecuteWriteAsync(async tx =>
                {
                    var cursor = await tx.RunAsync(
                        "MATCH (s:Symbol {branch: $branch}) DETACH DELETE s",
                        new Dictionary<string, object> { ["branch"] = Branch });
                    return await cursor.ConsumeAsync();
                });
            }
            finally
            {
                await session.CloseAsync();
            }
        }
        finally
        {
            await _driver.DisposeAsync();
        }
    }
}

/// <summary>
/// Live-database tests for the truncation/limit behavior that
/// <see cref="EdgeHopQueryService"/> owns on behalf of BOTH front ends. The flag must
/// mean "really cut off" — never "happened to land on the limit" — and the clamp must
/// keep the probe exact. 26 "BulkThing" methods around the default limit of 25 make
/// every boundary observable.
/// </summary>
public sealed class EdgeHopQueryServiceTruncationTests : IClassFixture<QueryServiceGraphFixture>
{
    private readonly QueryServiceGraphFixture _fx;

    public EdgeHopQueryServiceTruncationTests(QueryServiceGraphFixture fx) => _fx = fx;

    [Neo4jFact]
    public async Task DefaultLimit_WithOneMatchBeyondIt_TruncatesAndSetsTheFlag()
    {
        var result = await _fx.Service.FindSymbolsAsync(_fx.Branch, "bulkthing");

        Assert.Equal(EdgeHopQueryService.DefaultLimit, result.Hits.Count);
        Assert.True(result.Truncated);
    }

    [Neo4jFact]
    public async Task LimitEqualToMatchCount_ReturnsAllAndReportsNotTruncated()
    {
        // Exactly-limit matches with none beyond: the flag must be FALSE. An
        // implementation that reports "hit the limit" instead of "was cut off"
        // fails here.
        var result = await _fx.Service.FindSymbolsAsync(
            _fx.Branch, "bulkthing", limit: QueryServiceGraphFixture.BulkCount);

        Assert.Equal(QueryServiceGraphFixture.BulkCount, result.Hits.Count);
        Assert.False(result.Truncated);
    }

    [Neo4jFact]
    public async Task SmallLimit_TruncatesAtThatLimit()
    {
        var result = await _fx.Service.FindSymbolsAsync(_fx.Branch, "bulkthing", limit: 5);

        Assert.Equal(5, result.Hits.Count);
        Assert.True(result.Truncated);
    }

    [Neo4jFact]
    public async Task OversizedLimit_IsClampedButStillReturnsEverything()
    {
        // 200 exceeds MaxRequestLimit (99); with only 26 matches the clamp must be
        // invisible in the results and the flag stays false.
        var result = await _fx.Service.FindSymbolsAsync(_fx.Branch, "bulkthing", limit: 200);

        Assert.Equal(QueryServiceGraphFixture.BulkCount, result.Hits.Count);
        Assert.False(result.Truncated);
    }

    [Neo4jFact]
    public async Task OversizedLimit_WithMoreMatchesThanTheCap_CapsAtExactly99AndDetectsTruncation()
    {
        // 110 ManyThing matches exceed the reader's hard cap (100). The service must
        // clamp the request to MaxRequestLimit (99) so its limit+1 probe (100) stays
        // within the reader's cap and Truncated stays EXACT: 99 hits, truncated=true.
        // The off-by-one this pins: if MaxRequestLimit regressed to Neo4jGraphReader.
        // MaxLimit (100), the 101-row probe would be silently capped to 100 rows and
        // this returns 100 hits with truncated=FALSE despite 10 more matches existing.
        var result = await _fx.Service.FindSymbolsAsync(_fx.Branch, "manything", limit: 200);

        Assert.Equal(EdgeHopQueryService.MaxRequestLimit, result.Hits.Count);
        Assert.True(result.Truncated);
    }

    [Neo4jFact]
    public async Task SingleMatch_IsNotTruncated_AndCaseInsensitive()
    {
        var result = await _fx.Service.FindSymbolsAsync(_fx.Branch, "LONELYthing");

        var hit = Assert.Single(result.Hits);
        Assert.Equal("Method:void Svc.LonelyThing()", hit.Id);
        Assert.False(result.Truncated);
    }

    [Neo4jFact]
    public async Task KindFilter_PassesThroughToTheReader()
    {
        var methods = await _fx.Service.FindSymbolsAsync(
            _fx.Branch, "bulkthing", kind: SymbolKinds.Method, limit: 99);
        var types = await _fx.Service.FindSymbolsAsync(
            _fx.Branch, "bulkthing", kind: SymbolKinds.NamedType, limit: 99);

        Assert.Equal(QueryServiceGraphFixture.BulkCount, methods.Hits.Count);
        Assert.Empty(types.Hits);
    }

    [Neo4jFact]
    public async Task Hits_AreOrderedByName()
    {
        var result = await _fx.Service.FindSymbolsAsync(_fx.Branch, "bulkthing", limit: 99);

        var names = result.Hits.Select(h => h.Name).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal).ToList(), names);
    }
}
