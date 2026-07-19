using EdgeHop.Core;
using Neo4j.Driver;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// Class fixture for <see cref="Neo4jGraphReaderTests"/>: seeds a throwaway, GUID-unique
/// branch with a mini-graph mirroring the TinyFixture CALLS topology (see
/// <c>fixtures/TinyFixture/EXPECTED-GRAPH.md</c>), then deletes ONLY that branch when the
/// class finishes. Nothing here can ever touch branch 'main': every write and the final
/// targeted <c>DETACH DELETE</c> are constrained to the unique test branch.
/// When Neo4j is not configured the fixture no-ops entirely (all tests in the class are
/// <see cref="Neo4jFactAttribute"/> and get skipped).
/// <para>
/// BRANCH-ISOLATION LENS (deliberate id design): branch 'main' holds the REAL TinyFixture
/// graph, whose <c>Greeter.Greet</c> node has the exact same stable id as
/// <see cref="GreeterGreetId"/> — that collision is intentional. If any branch predicate
/// were dropped from the <see cref="Neo4jGraphReader.GetCallersAsync"/> Cypher, main's
/// node would match the same target id and main's callers would bleed into the result.
/// To make that bleed OBSERVABLE (and not collapsed by <c>DISTINCT</c> into id-identical
/// expected rows), every seeded CALLER id carries this run's GUID branch suffix
/// (<see cref="IdSuffix"/>), while main's caller ids (the <c>Main*</c> constants) are
/// asserted absent. CALLS edges are intra-branch by writer construction, so the seeded
/// topology stays self-contained.
/// </para>
/// <para>
/// Query-surface extension: the seed also carries a small component graph and a
/// type hierarchy exercising every non-CALLS edge type — a routable component
/// (<see cref="HomeId"/>) that <c>RENDERS</c> a child, an <c>ISalutation</c>/<c>Salutation</c>/
/// <c>LoudSalutation</c> triad wired with <c>IMPLEMENTS</c>/<c>INHERITS</c>/<c>OVERRIDES</c>/
/// <c>REFERENCES</c>/<c>HTTP_CALLS</c>, and a <see cref="NamespaceId"/> that <c>CONTAINS</c>
/// the types. These back the <c>get_relationships</c>/<c>get_path</c>/<c>graph_stats</c>
/// tests and the <see cref="SymbolHit.IsComponent"/>/<see cref="SymbolHit.Routes"/>
/// round-trip. They too are GUID-suffixed (main has none of them), keeping the branch
/// self-contained; the relationship/path branch-isolation probes stay anchored on the
/// id-colliding <see cref="GreeterGreetId"/> so a dropped branch predicate is still
/// observable. NOTE: nothing seeded here contains the substring "greet" in its name (it
/// would inflate the pinned <c>find_symbol "greet"</c> counts) and no <c>CALLS</c>/
/// <c>HTTP_CALLS</c>/<c>CONTAINS</c> edge points into <see cref="GreeterGreetId"/> (it would
/// perturb the pinned get_callers / relationship counts).
/// </para>
/// </summary>
public sealed class ReaderGraphFixture : IAsyncLifetime
{
    // Ids shared with branch 'main' ON PURPOSE (SymbolIdFormat shape:
    // "{Kind}:{qualified signature}") — the get_callers target must collide with main's
    // real node so a missing branch predicate has cross-branch rows to leak.
    public const string GreeterTypeId = "NamedType:TinyFixture.Greeter";
    public const string GreeterGreetId = "Method:string TinyFixture.Greeter.Greet(string)";

    // The REAL ids of main's callers of Greeter.Greet (EXPECTED-GRAPH.md rows #10, #11,
    // #19, #13). These must NEVER appear in a test-branch query result: seeing one means
    // the reader leaked rows across branches.
    public const string MainLoudGreeterGreetId = "Method:string TinyFixture.LoudGreeter.Greet(string)";
    public const string MainCallerCallGreetId = "Method:string TinyFixture.Caller.CallGreet()";
    public const string MainDecoratorDecorateId = "Method:string TinyFixture.Decorator.Decorate()";
    public const string MainAppRunId = "Method:string TinyFixture.App.Run()";

    private const string Assembly = "TinyFixture";

    /// <summary>Suffix baked into every seeded CALLER id so test-branch rows can never be
    /// id-identical to main's (DISTINCT cannot mask a cross-branch leak).</summary>
    public string IdSuffix => $"|{Branch}";

    // Test-branch caller ids: main's id + the GUID branch suffix.
    public string LoudGreeterGreetId => MainLoudGreeterGreetId + IdSuffix;
    public string CallerCallGreetId => MainCallerCallGreetId + IdSuffix;
    public string DecoratorDecorateId => MainDecoratorDecorateId + IdSuffix;
    public string AppRunId => MainAppRunId + IdSuffix;

    // Handoff-5 query-surface extension ids (component graph + type hierarchy). All carry
    // the GUID branch suffix so the branch stays self-contained (main has none of these).
    public string NamespaceId => $"Namespace:TinyFixture{IdSuffix}";
    public string HomeId => $"NamedType:TinyFixture.Home{IdSuffix}";
    public string ChildId => $"NamedType:TinyFixture.Child{IdSuffix}";
    public string SalutationInterfaceId => $"NamedType:TinyFixture.ISalutation{IdSuffix}";
    public string SalutationId => $"NamedType:TinyFixture.Salutation{IdSuffix}";
    public string LoudSalutationId => $"NamedType:TinyFixture.LoudSalutation{IdSuffix}";
    public string SalutationSayId => $"Method:void TinyFixture.Salutation.Say(){IdSuffix}";
    public string LoudSalutationSayId => $"Method:void TinyFixture.LoudSalutation.Say(){IdSuffix}";

    private IDriver? _driver;

    /// <summary>Unique throwaway branch for this class run — never 'main'.</summary>
    public string Branch { get; } = $"test-mcp-{Guid.NewGuid():N}";

    public string Database { get; private set; } = "neo4j";

    public IDriver Driver => _driver ?? throw new InvalidOperationException(
        "Neo4j is not configured — [Neo4jFact] tests should have been skipped.");

    public Neo4jGraphReader Reader => new(Driver, Database);

    public async Task InitializeAsync()
    {
        if (!Neo4jSettings.IsConfigured)
        {
            // All tests in the class are [Neo4jFact] and will be skipped; the fixture
            // must not fail construction just because no database is reachable.
            return;
        }

        var settings = Neo4jSettings.FromEnvironment();
        Database = settings.Database;
        _driver = GraphDatabase.Driver(settings.Uri, AuthTokens.Basic(settings.User, settings.Password));

        await Neo4jSchema.ApplyAsync(_driver, Database);

        // Seed via the (already-proven) writer: the TinyFixture call topology.
        // Names use the fixture's MinimallyQualifiedFormat rendering so that a
        // case-insensitive "greet" matches the three Greet methods AND the Greeter type
        // (4 hits), while Decorate/Run do not match.
        // The TARGET (Greeter.Greet) keeps main's exact id; the four CALLERS get the
        // GUID-suffixed ids (see class doc: this is what makes a missing branch
        // predicate in GetCallersAsync observable instead of DISTINCT-collapsed).
        var writer = new Neo4jGraphWriter(_driver, Database);
        await writer.UpsertNodesAsync(new[]
        {
            new NodeRow(Branch, GreeterTypeId, "Greeter", SymbolKinds.NamedType,
                "Greeter.cs", Assembly, IsAbstract: false),
            new NodeRow(Branch, GreeterGreetId, "string Greeter.Greet(string name)", SymbolKinds.Method,
                "Greeter.cs", Assembly, IsAbstract: false),
            new NodeRow(Branch, LoudGreeterGreetId, "string LoudGreeter.Greet(string name)", SymbolKinds.Method,
                "LoudGreeter.cs", Assembly, IsAbstract: false),
            new NodeRow(Branch, CallerCallGreetId, "string Caller.CallGreet()", SymbolKinds.Method,
                "Caller.cs", Assembly, IsAbstract: false),
            new NodeRow(Branch, DecoratorDecorateId, "string Decorator.Decorate()", SymbolKinds.Method,
                "Sub/Decorator.cs", Assembly, IsAbstract: false),
            new NodeRow(Branch, AppRunId, "string App.Run()", SymbolKinds.Method,
                "App.cs", Assembly, IsAbstract: false),

            // Handoff-5 query-surface extension: component graph + type hierarchy. Names
            // deliberately avoid the substring "greet" so the pinned find_symbol counts hold.
            new NodeRow(Branch, NamespaceId, "TinyFixture", SymbolKinds.Namespace,
                null, Assembly, IsAbstract: false),
            new NodeRow(Branch, HomeId, "Home", SymbolKinds.NamedType,
                "Home.razor", Assembly, IsAbstract: false, IsComponent: true, Routes: ["/", "/home"]),
            new NodeRow(Branch, ChildId, "Child", SymbolKinds.NamedType,
                "Child.razor", Assembly, IsAbstract: false, IsComponent: true, Routes: null),
            new NodeRow(Branch, SalutationInterfaceId, "ISalutation", SymbolKinds.NamedType,
                "ISalutation.cs", Assembly, IsAbstract: true),
            new NodeRow(Branch, SalutationId, "Salutation", SymbolKinds.NamedType,
                "Salutation.cs", Assembly, IsAbstract: false),
            new NodeRow(Branch, LoudSalutationId, "LoudSalutation", SymbolKinds.NamedType,
                "LoudSalutation.cs", Assembly, IsAbstract: false),
            new NodeRow(Branch, SalutationSayId, "void Salutation.Say()", SymbolKinds.Method,
                "Salutation.cs", Assembly, IsAbstract: false),
            new NodeRow(Branch, LoudSalutationSayId, "void LoudSalutation.Say()", SymbolKinds.Method,
                "LoudSalutation.cs", Assembly, IsAbstract: false),
        });
        await writer.UpsertEdgesAsync(new[]
        {
            new EdgeRow(Branch, LoudGreeterGreetId, GreeterGreetId, EdgeTypes.Calls, "LoudGreeter.cs"),
            new EdgeRow(Branch, CallerCallGreetId, GreeterGreetId, EdgeTypes.Calls, "Caller.cs"),
            new EdgeRow(Branch, DecoratorDecorateId, GreeterGreetId, EdgeTypes.Calls, "Sub/Decorator.cs"),
            new EdgeRow(Branch, AppRunId, CallerCallGreetId, EdgeTypes.Calls, "App.cs"),

            // Handoff-5 extension edges — one of every non-CALLS edge type. None point INTO
            // GreeterGreetId, so the get_callers / relationship counts on it stay pinned.
            new EdgeRow(Branch, HomeId, ChildId, EdgeTypes.Renders, "Home.razor"),
            new EdgeRow(Branch, SalutationId, SalutationInterfaceId, EdgeTypes.Implements, "Salutation.cs"),
            new EdgeRow(Branch, LoudSalutationId, SalutationId, EdgeTypes.Inherits, "LoudSalutation.cs"),
            new EdgeRow(Branch, LoudSalutationSayId, SalutationSayId, EdgeTypes.Overrides, "LoudSalutation.cs"),
            new EdgeRow(Branch, SalutationId, SalutationSayId, EdgeTypes.Contains, "Salutation.cs"),
            new EdgeRow(Branch, LoudSalutationId, LoudSalutationSayId, EdgeTypes.Contains, "LoudSalutation.cs"),
            new EdgeRow(Branch, SalutationSayId, SalutationInterfaceId, EdgeTypes.References, "Salutation.cs"),
            new EdgeRow(Branch, SalutationSayId, LoudSalutationSayId, EdgeTypes.HttpCalls, "Salutation.cs"),
            new EdgeRow(Branch, NamespaceId, GreeterTypeId, EdgeTypes.Contains, "Greeter.cs"),
            new EdgeRow(Branch, NamespaceId, HomeId, EdgeTypes.Contains, "Home.razor"),
            new EdgeRow(Branch, NamespaceId, ChildId, EdgeTypes.Contains, "Child.razor"),
            new EdgeRow(Branch, NamespaceId, SalutationInterfaceId, EdgeTypes.Contains, "ISalutation.cs"),
            new EdgeRow(Branch, NamespaceId, SalutationId, EdgeTypes.Contains, "Salutation.cs"),
            new EdgeRow(Branch, NamespaceId, LoudSalutationId, EdgeTypes.Contains, "LoudSalutation.cs"),
        });
    }

    public async Task DisposeAsync()
    {
        if (_driver is null)
        {
            return;
        }

        try
        {
            // Targeted cleanup: ONLY this run's unique GUID branch (a handful of nodes).
            // The branch value is a parameter and can never be 'main'.
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
/// Phase 3 checkpoint: live-database tests for <see cref="Neo4jGraphReader"/> — the query
/// layer behind the <c>find_symbol</c> / <c>get_callers</c> MCP tools. Expectations mirror
/// <c>fixtures/TinyFixture/EXPECTED-GRAPH.md</c>: depth 1 → exactly 3 direct callers of
/// <c>Greeter.Greet</c>; depth 2 → exactly 4 (adds <c>App.Run</c> transitively).
/// Skipped automatically when NEO4J_* environment variables are not set.
/// </summary>
public sealed class Neo4jGraphReaderTests : IClassFixture<ReaderGraphFixture>
{
    private readonly ReaderGraphFixture _fx;

    public Neo4jGraphReaderTests(ReaderGraphFixture fx) => _fx = fx;

    // ---------------------------------------------------------------- find_symbol --

    [Neo4jFact]
    public async Task FindSymbols_CaseInsensitiveSubstring_FindsGreetSymbols_OrderedByName()
    {
        // Lower-case query against Pascal-case names: proves CONTAINS is case-insensitive.
        var hits = await _fx.Reader.FindSymbolsAsync(_fx.Branch, "greet");

        var ids = hits.Select(h => h.Id).ToList();
        Assert.Equal(4, hits.Count);
        Assert.Contains(ReaderGraphFixture.GreeterTypeId, ids);
        Assert.Contains(ReaderGraphFixture.GreeterGreetId, ids);
        Assert.Contains(_fx.LoudGreeterGreetId, ids);
        Assert.Contains(_fx.CallerCallGreetId, ids);

        // Ordered by name (ordinal, matching Neo4j's string ordering for these ASCII names).
        Assert.Equal(
            hits.OrderBy(h => h.Name, StringComparer.Ordinal).Select(h => h.Id).ToList(),
            ids);

        // All four projected properties round-trip through the reader.
        var greet = Assert.Single(hits, h => h.Id == ReaderGraphFixture.GreeterGreetId);
        Assert.Equal("string Greeter.Greet(string name)", greet.Name);
        Assert.Equal(SymbolKinds.Method, greet.Kind);
        Assert.Equal("Greeter.cs", greet.SourceDoc);
    }

    [Neo4jFact]
    public async Task FindSymbols_KindFilter_ExcludesOtherKinds()
    {
        var hits = await _fx.Reader.FindSymbolsAsync(_fx.Branch, "greet", kind: SymbolKinds.NamedType);

        var hit = Assert.Single(hits);
        Assert.Equal(ReaderGraphFixture.GreeterTypeId, hit.Id);
        Assert.Equal(SymbolKinds.NamedType, hit.Kind);
    }

    [Neo4jFact]
    public async Task FindSymbols_LimitIsRespected_AndOversizedLimitIsSafe()
    {
        var limited = await _fx.Reader.FindSymbolsAsync(_fx.Branch, "greet", limit: 2);
        Assert.Equal(2, limited.Count);

        // Above the 100 cap: must not throw, and returns everything available (4 here).
        var capped = await _fx.Reader.FindSymbolsAsync(_fx.Branch, "greet", limit: 1000);
        Assert.Equal(4, capped.Count);
    }

    [Neo4jFact]
    public async Task FindSymbols_EmptyOrWhitespaceQuery_ReturnsEmpty()
    {
        Assert.Empty(await _fx.Reader.FindSymbolsAsync(_fx.Branch, string.Empty));
        Assert.Empty(await _fx.Reader.FindSymbolsAsync(_fx.Branch, "   "));
    }

    [Neo4jFact]
    public async Task FindSymbols_DoesNotLeakAcrossBranches()
    {
        // Branch 'main' holds the real TinyFixture graph, which includes PartialThing
        // symbols; this test branch seeds none. Any hit here would be cross-branch leakage.
        Assert.Empty(await _fx.Reader.FindSymbolsAsync(_fx.Branch, "PartialThing"));

        // 'main' also holds Greet symbols with the same names (and, for Greeter/its Greet
        // method, the same ids). An exact count of 4 distinct ids proves no rows bled in
        // from other branches, and no main-only (unsuffixed-caller) id may appear.
        var hits = await _fx.Reader.FindSymbolsAsync(_fx.Branch, "greet");
        Assert.Equal(4, hits.Count);
        Assert.Equal(4, hits.Select(h => h.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(ReaderGraphFixture.MainLoudGreeterGreetId, hits.Select(h => h.Id));
        Assert.DoesNotContain(ReaderGraphFixture.MainCallerCallGreetId, hits.Select(h => h.Id));
    }

    // ---------------------------------------------------------------- get_callers --

    [Neo4jFact]
    public async Task GetCallers_Depth1_ReturnsExactlyTheThreeDirectCallers()
    {
        // No explicit depth: also pins the default depth of 1.
        var hits = await _fx.Reader.GetCallersAsync(_fx.Branch, ReaderGraphFixture.GreeterGreetId);

        var ids = hits.Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(3, hits.Count);
        Assert.Contains(_fx.LoudGreeterGreetId, ids);
        Assert.Contains(_fx.CallerCallGreetId, ids);
        Assert.Contains(_fx.DecoratorDecorateId, ids);

        // The transitive caller must NOT appear at depth 1, and the target is never
        // included in its own callers.
        Assert.DoesNotContain(_fx.AppRunId, ids);
        Assert.DoesNotContain(ReaderGraphFixture.GreeterGreetId, ids);
    }

    [Neo4jFact]
    public async Task GetCallers_Depth2_AddsExactlyTheTransitiveCaller()
    {
        var hits = await _fx.Reader.GetCallersAsync(
            _fx.Branch, ReaderGraphFixture.GreeterGreetId, depth: 2);

        var ids = hits.Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(4, hits.Count);
        Assert.Contains(_fx.LoudGreeterGreetId, ids);
        Assert.Contains(_fx.CallerCallGreetId, ids);
        Assert.Contains(_fx.DecoratorDecorateId, ids);
        Assert.Contains(_fx.AppRunId, ids);
        Assert.DoesNotContain(ReaderGraphFixture.GreeterGreetId, ids);

        // Caller.CallGreet reaches Greeter.Greet by BOTH a 1-hop path and no other; App.Run
        // reaches it once via CallGreet — DISTINCT must keep each caller single. Count == 4
        // (asserted above) already proves de-duplication; ids being a set double-checks it.
        Assert.Equal(4, ids.Count);
    }

    [Neo4jFact]
    public async Task GetCallers_DoesNotLeakAcrossBranches()
    {
        // The target id is deliberately IDENTICAL to the real Greeter.Greet node on branch
        // 'main' (which has the same CALLS topology with UNsuffixed caller ids), while
        // every caller seeded on this test branch carries the run's GUID id-suffix. If a
        // branch predicate were dropped from the GetCallersAsync Cypher, main's node would
        // match $symbolId too and main's callers — id-distinct from the seeded ones, so
        // DISTINCT cannot collapse them — would bleed in: the exact counts would break AND
        // the Main* ids below would appear. This is the observable cross-branch probe that
        // find_symbol already has via FindSymbols_DoesNotLeakAcrossBranches.
        var depth1 = await _fx.Reader.GetCallersAsync(_fx.Branch, ReaderGraphFixture.GreeterGreetId);
        var depth2 = await _fx.Reader.GetCallersAsync(_fx.Branch, ReaderGraphFixture.GreeterGreetId, depth: 2);

        Assert.Equal(3, depth1.Count);
        Assert.Equal(4, depth2.Count);

        foreach (var hits in new[] { depth1, depth2 })
        {
            var ids = hits.Select(h => h.Id).ToHashSet(StringComparer.Ordinal);

            // Main's real caller ids must be absent — their presence is a branch leak.
            Assert.DoesNotContain(ReaderGraphFixture.MainLoudGreeterGreetId, ids);
            Assert.DoesNotContain(ReaderGraphFixture.MainCallerCallGreetId, ids);
            Assert.DoesNotContain(ReaderGraphFixture.MainDecoratorDecorateId, ids);
            Assert.DoesNotContain(ReaderGraphFixture.MainAppRunId, ids);

            // And every returned row must be one of this branch's suffixed seeds.
            Assert.All(ids, id => Assert.EndsWith(_fx.IdSuffix, id, StringComparison.Ordinal));
        }
    }

    [Neo4jFact]
    public async Task GetCallers_DepthOutOfRange_Throws()
    {
        var reader = _fx.Reader;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => reader.GetCallersAsync(_fx.Branch, ReaderGraphFixture.GreeterGreetId, depth: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => reader.GetCallersAsync(_fx.Branch, ReaderGraphFixture.GreeterGreetId, depth: -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => reader.GetCallersAsync(_fx.Branch, ReaderGraphFixture.GreeterGreetId, depth: 11));
    }

    [Neo4jFact]
    public async Task GetCallers_UnknownSymbolId_ReturnsEmpty()
    {
        var hits = await _fx.Reader.GetCallersAsync(
            _fx.Branch, "Method:TinyFixture.DoesNotExist.Nope()", depth: 2);

        Assert.Empty(hits);
    }

    // ---------------------------------------------------- SymbolHit component facets --

    [Neo4jFact]
    public async Task FindSymbols_ComponentFacets_RoundTripIsComponentAndRoutes()
    {
        var reader = _fx.Reader;

        // A routable component: isComponent true and its route templates in order.
        var home = Assert.Single(await reader.FindSymbolsAsync(_fx.Branch, "home"));
        Assert.Equal(_fx.HomeId, home.Id);
        Assert.True(home.IsComponent);
        Assert.Equal(new[] { "/", "/home" }, home.Routes);

        // A routeless component: isComponent true, routes null (property absent → null).
        var child = Assert.Single(await reader.FindSymbolsAsync(_fx.Branch, "child"));
        Assert.Equal(_fx.ChildId, child.Id);
        Assert.True(child.IsComponent);
        Assert.Null(child.Routes);

        // A plain (non-component) type: isComponent false, routes null round-trip.
        var greeterType = Assert.Single(
            await reader.FindSymbolsAsync(_fx.Branch, "greeter", kind: SymbolKinds.NamedType));
        Assert.Equal(ReaderGraphFixture.GreeterTypeId, greeterType.Id);
        Assert.False(greeterType.IsComponent);
        Assert.Null(greeterType.Routes);
    }

    // ------------------------------------------------------------ get_relationships --

    /// <summary>The fan-out cap the reader applies as a bare <c>LIMIT</c>; comfortably
    /// larger than any seeded neighbor set so nothing is truncated in these tests.</summary>
    private const int RelLimit = 50;

    [Neo4jFact]
    public async Task GetRelationships_Depth1_OutInBoth_AndEdgeTypeFilter()
    {
        var reader = _fx.Reader;

        // OUT: Salutation → ISalutation (IMPLEMENTS) and → Salutation.Say (CONTAINS). Each
        // hit carries the edge type that reached it and the direction traversed.
        var outHits = await reader.GetRelationshipsAsync(
            _fx.Branch, _fx.SalutationId, RelationshipDirection.Out, null, 1, RelLimit);
        var outById = outHits.ToDictionary(h => h.Symbol.Id);
        Assert.Equal(2, outHits.Count);
        Assert.Equal(EdgeTypes.Implements, outById[_fx.SalutationInterfaceId].EdgeType);
        Assert.Equal("out", outById[_fx.SalutationInterfaceId].Direction);
        Assert.Equal(EdgeTypes.Contains, outById[_fx.SalutationSayId].EdgeType);
        Assert.Equal("out", outById[_fx.SalutationSayId].Direction);
        Assert.DoesNotContain(_fx.SalutationId, outById.Keys); // self excluded

        // IN: the namespace CONTAINS it and LoudSalutation INHERITS from it.
        var inHits = await reader.GetRelationshipsAsync(
            _fx.Branch, _fx.SalutationId, RelationshipDirection.In, null, 1, RelLimit);
        Assert.Equal(
            new HashSet<string> { _fx.NamespaceId, _fx.LoudSalutationId },
            inHits.Select(h => h.Symbol.Id).ToHashSet(StringComparer.Ordinal));
        Assert.All(inHits, h => Assert.Equal("in", h.Direction));

        // BOTH: the union of the two, each direction tagged.
        var bothHits = await reader.GetRelationshipsAsync(
            _fx.Branch, _fx.SalutationId, RelationshipDirection.Both, null, 1, RelLimit);
        Assert.Equal(
            new HashSet<string>
            {
                _fx.SalutationInterfaceId, _fx.SalutationSayId, _fx.NamespaceId, _fx.LoudSalutationId,
            },
            bothHits.Select(h => h.Symbol.Id).ToHashSet(StringComparer.Ordinal));

        // Edge-type filter narrows OUT to just the IMPLEMENTS neighbor.
        var impl = await reader.GetRelationshipsAsync(
            _fx.Branch, _fx.SalutationId, RelationshipDirection.Out, EdgeTypes.Implements, 1, RelLimit);
        var implHit = Assert.Single(impl);
        Assert.Equal(_fx.SalutationInterfaceId, implHit.Symbol.Id);
        Assert.Equal(EdgeTypes.Implements, implHit.EdgeType);

        // RENDERS is a first-class relationship: Home renders Child.
        var renders = await reader.GetRelationshipsAsync(
            _fx.Branch, _fx.HomeId, RelationshipDirection.Out, EdgeTypes.Renders, 1, RelLimit);
        var rendersHit = Assert.Single(renders);
        Assert.Equal(_fx.ChildId, rendersHit.Symbol.Id);
        Assert.Equal(EdgeTypes.Renders, rendersHit.EdgeType);
    }

    [Neo4jFact]
    public async Task GetRelationships_Depth2_SingleEdgeType_FollowsTheChain()
    {
        // App.Run -CALLS-> Caller.CallGreet -CALLS-> Greeter.Greet: a 2-hop single-type walk
        // returns both reachable nodes, tagged with the (validated, interpolated) edge type.
        var chain = await _fx.Reader.GetRelationshipsAsync(
            _fx.Branch, _fx.AppRunId, RelationshipDirection.Out, EdgeTypes.Calls, 2, RelLimit);

        Assert.Equal(
            new HashSet<string> { _fx.CallerCallGreetId, ReaderGraphFixture.GreeterGreetId },
            chain.Select(h => h.Symbol.Id).ToHashSet(StringComparer.Ordinal));
        Assert.All(chain, h =>
        {
            Assert.Equal(EdgeTypes.Calls, h.EdgeType);
            Assert.Equal("out", h.Direction);
        });
    }

    [Neo4jFact]
    public async Task GetRelationships_DoesNotLeakAcrossBranches()
    {
        // Anchored on the id-colliding Greeter.Greet: its IN neighbors on this branch are
        // exactly the three GUID-suffixed CALLS callers. If a branch predicate were dropped
        // main's identical-id node would add its UNsuffixed callers — the count would break
        // and a Main* id would surface. (No CONTAINS/other edge points into Greeter.Greet.)
        var inHits = await _fx.Reader.GetRelationshipsAsync(
            _fx.Branch, ReaderGraphFixture.GreeterGreetId, RelationshipDirection.In, null, 1, RelLimit);

        var ids = inHits.Select(h => h.Symbol.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(3, inHits.Count);
        Assert.Contains(_fx.LoudGreeterGreetId, ids);
        Assert.Contains(_fx.CallerCallGreetId, ids);
        Assert.Contains(_fx.DecoratorDecorateId, ids);

        Assert.DoesNotContain(ReaderGraphFixture.MainLoudGreeterGreetId, ids);
        Assert.DoesNotContain(ReaderGraphFixture.MainCallerCallGreetId, ids);
        Assert.DoesNotContain(ReaderGraphFixture.MainDecoratorDecorateId, ids);
        Assert.All(ids, id => Assert.EndsWith(_fx.IdSuffix, id, StringComparison.Ordinal));
    }

    [Neo4jFact]
    public async Task GetRelationships_ValidationAndUnknownId()
    {
        var reader = _fx.Reader;

        // depth > 1 requires a single edge type (multi-hop mixed-type is unsupported).
        await Assert.ThrowsAsync<ArgumentException>(() => reader.GetRelationshipsAsync(
            _fx.Branch, _fx.SalutationId, RelationshipDirection.Out, null, 2, RelLimit));

        // depth > 1 with an unknown edge type is rejected before interpolation (defense in depth).
        await Assert.ThrowsAsync<ArgumentException>(() => reader.GetRelationshipsAsync(
            _fx.Branch, _fx.SalutationId, RelationshipDirection.Out, "NOT_A_TYPE", 2, RelLimit));

        // depth outside [1, 10].
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.GetRelationshipsAsync(
            _fx.Branch, _fx.SalutationId, RelationshipDirection.Out, EdgeTypes.Calls, 0, RelLimit));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.GetRelationshipsAsync(
            _fx.Branch, _fx.SalutationId, RelationshipDirection.Out, EdgeTypes.Calls, 11, RelLimit));

        // Blank branch/id guards.
        await Assert.ThrowsAsync<ArgumentException>(() => reader.GetRelationshipsAsync(
            "   ", _fx.SalutationId, RelationshipDirection.Out, null, 1, RelLimit));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.GetRelationshipsAsync(
            _fx.Branch, "   ", RelationshipDirection.Out, null, 1, RelLimit));

        // An id that exists nowhere in the branch → empty.
        Assert.Empty(await reader.GetRelationshipsAsync(
            _fx.Branch, "NamedType:TinyFixture.DoesNotExist", RelationshipDirection.Out, null, 1, RelLimit));
    }

    [Neo4jFact]
    public async Task GetRelationships_InjectionAttempt_IsTreatedAsLiteral()
    {
        var reader = _fx.Reader;

        // An evil symbolId is a parameter → matches nothing, no error.
        var evilId = "NamedType:evil'\"}) DETACH DELETE (s) RETURN s //\\";
        Assert.Empty(await reader.GetRelationshipsAsync(
            _fx.Branch, evilId, RelationshipDirection.Out, null, 1, RelLimit));

        // At depth 1 the edge type is a parameter (type(r) = $edgeType): an injection string
        // is just a type name nothing has → empty, no error.
        Assert.Empty(await reader.GetRelationshipsAsync(
            _fx.Branch, _fx.SalutationId, RelationshipDirection.Out, "'; DETACH DELETE n //", 1, RelLimit));

        // And the seeded graph still answers correctly afterwards.
        Assert.Equal(2, (await reader.GetRelationshipsAsync(
            _fx.Branch, _fx.SalutationId, RelationshipDirection.Out, null, 1, RelLimit)).Count);
    }

    // --------------------------------------------------------------------- get_path --

    [Neo4jFact]
    public async Task GetPath_ShortestDirectedPath_WithTypesAndBounds()
    {
        var reader = _fx.Reader;

        // App.Run -CALLS-> Caller.CallGreet -CALLS-> Greeter.Greet: one shortest 2-hop path,
        // nodes in order, each non-first node tagged with the edge linking it to its predecessor.
        var path = await reader.GetPathAsync(_fx.Branch, _fx.AppRunId, ReaderGraphFixture.GreeterGreetId, null, 10);
        Assert.True(path.Found);
        Assert.Equal(
            new[] { _fx.AppRunId, _fx.CallerCallGreetId, ReaderGraphFixture.GreeterGreetId },
            path.Nodes.Select(n => n.Symbol.Id).ToArray());
        Assert.Null(path.Nodes[0].EdgeTypeFromPrev);
        Assert.Equal(EdgeTypes.Calls, path.Nodes[1].EdgeTypeFromPrev);
        Assert.Equal(EdgeTypes.Calls, path.Nodes[2].EdgeTypeFromPrev);

        // maxLength shorter than the path → not found.
        var tooShort = await reader.GetPathAsync(_fx.Branch, _fx.AppRunId, ReaderGraphFixture.GreeterGreetId, null, 1);
        Assert.False(tooShort.Found);
        Assert.Empty(tooShort.Nodes);

        // Edge-type filter: the all-CALLS path survives a CALLS restriction, dies under RENDERS.
        Assert.True((await reader.GetPathAsync(
            _fx.Branch, _fx.AppRunId, ReaderGraphFixture.GreeterGreetId, EdgeTypes.Calls, 10)).Found);
        Assert.False((await reader.GetPathAsync(
            _fx.Branch, _fx.AppRunId, ReaderGraphFixture.GreeterGreetId, EdgeTypes.Renders, 10)).Found);
    }

    [Neo4jFact]
    public async Task GetPath_SameEndpoint_IsZeroLengthFoundPath()
    {
        var reader = _fx.Reader;

        // fromId == toId for an existing symbol → a found, single-node, zero-length path.
        var self = await reader.GetPathAsync(
            _fx.Branch, ReaderGraphFixture.GreeterGreetId, ReaderGraphFixture.GreeterGreetId, null, 10);
        Assert.True(self.Found);
        var only = Assert.Single(self.Nodes);
        Assert.Equal(ReaderGraphFixture.GreeterGreetId, only.Symbol.Id);
        Assert.Null(only.EdgeTypeFromPrev);

        // fromId == toId for a NON-existent symbol → not found (nothing to stand on).
        const string ghost = "Method:TinyFixture.Ghost.Nope()";
        var missing = await reader.GetPathAsync(_fx.Branch, ghost, ghost, null, 10);
        Assert.False(missing.Found);
        Assert.Empty(missing.Nodes);
    }

    [Neo4jFact]
    public async Task GetPath_Unreachable_ReturnsNotFound()
    {
        // Greeter.Greet has no outgoing edges, so App.Run is not reachable FROM it
        // (the reverse direction is a path, but get_path follows out-edges only).
        var none = await _fx.Reader.GetPathAsync(
            _fx.Branch, ReaderGraphFixture.GreeterGreetId, _fx.AppRunId, null, 10);
        Assert.False(none.Found);
        Assert.Empty(none.Nodes);
    }

    [Neo4jFact]
    public async Task GetPath_ValidationAndInjection()
    {
        var reader = _fx.Reader;

        // maxLength outside [1, 15].
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.GetPathAsync(
            _fx.Branch, _fx.AppRunId, ReaderGraphFixture.GreeterGreetId, null, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.GetPathAsync(
            _fx.Branch, _fx.AppRunId, ReaderGraphFixture.GreeterGreetId, null, 16));

        // Blank branch/id guards.
        await Assert.ThrowsAsync<ArgumentException>(() => reader.GetPathAsync(
            "   ", _fx.AppRunId, ReaderGraphFixture.GreeterGreetId, null, 10));

        // Injection in either endpoint is a literal id → not found, no error.
        var evil = "Method:evil'\"}) DETACH DELETE (a) RETURN a //\\";
        Assert.False((await reader.GetPathAsync(_fx.Branch, evil, ReaderGraphFixture.GreeterGreetId, null, 10)).Found);
        Assert.False((await reader.GetPathAsync(_fx.Branch, _fx.AppRunId, evil, null, 10)).Found);

        // And the seeded path still resolves afterwards.
        Assert.True((await reader.GetPathAsync(
            _fx.Branch, _fx.AppRunId, ReaderGraphFixture.GreeterGreetId, null, 10)).Found);
    }

    // ------------------------------------------------------------------ graph_stats --

    [Neo4jFact]
    public async Task GraphStats_Totals_Kinds_Types_AndGodNodesExcludeContains()
    {
        var stats = await _fx.Reader.GetStatsAsync(_fx.Branch, topN: 20);

        Assert.Equal(_fx.Branch, stats.Branch);
        Assert.Equal(14, stats.TotalNodes);
        Assert.Equal(18, stats.TotalEdges);

        long KindCount(string kind) => stats.NodesByKind.Single(k => k.Kind == kind).Count;
        Assert.Equal(7, KindCount(SymbolKinds.Method));
        Assert.Equal(6, KindCount(SymbolKinds.NamedType));
        Assert.Equal(1, KindCount(SymbolKinds.Namespace));

        long TypeCount(string type) => stats.EdgesByType.Single(t => t.Type == type).Count;
        Assert.Equal(4, TypeCount(EdgeTypes.Calls));
        Assert.Equal(8, TypeCount(EdgeTypes.Contains));
        Assert.Equal(1, TypeCount(EdgeTypes.Renders));
        Assert.Equal(1, TypeCount(EdgeTypes.Implements));
        Assert.Equal(1, TypeCount(EdgeTypes.Inherits));
        Assert.Equal(1, TypeCount(EdgeTypes.Overrides));
        Assert.Equal(1, TypeCount(EdgeTypes.References));
        Assert.Equal(1, TypeCount(EdgeTypes.HttpCalls));

        // God nodes rank by degree EXCLUDING CONTAINS. The namespace has the highest RAW
        // degree (6 CONTAINS edges) yet its non-CONTAINS degree is 0, so it must NOT top the
        // list — and, being a zero-degree (non-hub) node, must be excluded entirely, exactly
        // as the SQLite backend excludes it (cross-backend parity — the deg CTE there is
        // built only from non-CONTAINS edge endpoints).
        Assert.Equal(3, stats.GodNodes[0].Degree);
        Assert.NotEqual(_fx.NamespaceId, stats.GodNodes[0].Symbol.Id);
        Assert.DoesNotContain(stats.GodNodes, g => g.Symbol.Id == _fx.NamespaceId);
        Assert.All(stats.GodNodes, g => Assert.True(g.Degree > 0));

        // Blank branch guard.
        await Assert.ThrowsAsync<ArgumentException>(() => _fx.Reader.GetStatsAsync("   ", topN: 5));

        // branch is a parameter: an injection-shaped branch matches nothing (no leak, no error).
        var evil = await _fx.Reader.GetStatsAsync("x\" DETACH DELETE n RETURN n //", topN: 5);
        Assert.Equal(0, evil.TotalNodes);
        Assert.Equal(0, evil.TotalEdges);
        Assert.Empty(evil.GodNodes);
    }

    // ----------------------------------------------------------- injection safety --

    [Neo4jFact]
    public async Task FindSymbols_InjectionAttemptInQuery_IsTreatedAsLiteral()
    {
        // If the query were spliced into Cypher text this would break out of the string;
        // as a parameter it is a literal substring no name contains → empty, no error.
        var hits = await _fx.Reader.FindSymbolsAsync(_fx.Branch, "') RETURN s //");
        Assert.Empty(hits);

        // And the graph is untouched afterwards.
        Assert.Equal(4, (await _fx.Reader.FindSymbolsAsync(_fx.Branch, "greet")).Count);
    }

    [Neo4jFact]
    public async Task GetCallers_InjectionAttemptInSymbolId_IsTreatedAsLiteral()
    {
        // Quotes and backslashes that would terminate a spliced string literal; as a
        // parameter this is just an id that matches nothing → empty, no error.
        var evil = "Method:evil'\"}) DETACH DELETE (s) RETURN s //\\";
        var hits = await _fx.Reader.GetCallersAsync(_fx.Branch, evil, depth: 2);
        Assert.Empty(hits);

        // And the seeded call graph still answers correctly afterwards.
        var callers = await _fx.Reader.GetCallersAsync(
            _fx.Branch, ReaderGraphFixture.GreeterGreetId, depth: 2);
        Assert.Equal(4, callers.Count);
    }

    [Neo4jFact]
    public async Task GetCallers_FollowsJsCallsAcrossTheCsToJsBoundary()
    {
        // C#: Page.Load() -CALLS-> Widget.Refresh() -JS_CALLS-> js|…widget.js#getWidget.
        // get_callers on the JS function surfaces the direct C# caller (depth 1) and, via the
        // CALLS+JS_CALLS chain, its transitive C# caller (depth 2). Self-contained on its own
        // GUID branch, cleaned up here — never touches real data.
        var branch = $"test-js-{Guid.NewGuid():N}";
        var writer = new Neo4jGraphWriter(_fx.Driver, _fx.Database);
        const string load = "Method:Web.Page.Load()";
        const string refresh = "Method:Web.Widget.Refresh()";
        const string getWidget = "Method:js|wwwroot/js/widget.js#getWidget";
        try
        {
            await writer.UpsertNodesAsync(new[]
            {
                new NodeRow(branch, load, "Load()", SymbolKinds.Method, "Page.razor.cs", "Web", false),
                new NodeRow(branch, refresh, "Refresh()", SymbolKinds.Method, "Widget.razor.cs", "Web", false),
                new NodeRow(branch, getWidget, "getWidget", SymbolKinds.Method, "wwwroot/js/widget.js", "", false),
            });
            await writer.UpsertEdgesAsync(new[]
            {
                new EdgeRow(branch, load, refresh, EdgeTypes.Calls, "Page.razor.cs"),
                new EdgeRow(branch, refresh, getWidget, EdgeTypes.JsCalls, "Widget.razor.cs"),
            });

            var reader = new Neo4jGraphReader(_fx.Driver, _fx.Database);
            var depth1 = await reader.GetCallersAsync(branch, getWidget, depth: 1);
            Assert.Equal(refresh, Assert.Single(depth1).Id);

            var depth2 = await reader.GetCallersAsync(branch, getWidget, depth: 2);
            Assert.Equal(
                new HashSet<string> { refresh, load },
                depth2.Select(h => h.Id).ToHashSet(StringComparer.Ordinal));
        }
        finally
        {
            await DeleteBranchAsync(branch);
        }
    }

    [Neo4jFact]
    public async Task GetCallers_FollowsJsInvokesAcrossTheJsToCsBoundary()
    {
        // Round trip: Page.Load() -JS_CALLS-> js|app.js#run -JS_INVOKES-> Api.Compute()
        // ([JSInvokable]). get_callers on the C# invokable surfaces the JS caller (depth 1) and,
        // chaining JS_CALLS+JS_INVOKES, the C# method that reached into JS (depth 2). Self-contained
        // on its own GUID branch, cleaned up here.
        var branch = $"test-js-{Guid.NewGuid():N}";
        var writer = new Neo4jGraphWriter(_fx.Driver, _fx.Database);
        const string load = "Method:Web.Page.Load()";
        const string run = "Method:js|wwwroot/js/app.js#run";
        const string compute = "Method:Api.Compute()";
        try
        {
            await writer.UpsertNodesAsync(new[]
            {
                new NodeRow(branch, load, "Load()", SymbolKinds.Method, "Page.razor.cs", "Web", false),
                new NodeRow(branch, run, "run", SymbolKinds.Method, "wwwroot/js/app.js", "", false),
                new NodeRow(branch, compute, "Compute()", SymbolKinds.Method, "Api.cs", "Api", false),
            });
            await writer.UpsertEdgesAsync(new[]
            {
                new EdgeRow(branch, load, run, EdgeTypes.JsCalls, "Page.razor"),
                new EdgeRow(branch, run, compute, EdgeTypes.JsInvokes, "wwwroot/js/app.js"),
            });

            var reader = new Neo4jGraphReader(_fx.Driver, _fx.Database);
            var depth1 = await reader.GetCallersAsync(branch, compute, depth: 1);
            Assert.Equal(run, Assert.Single(depth1).Id);

            var depth2 = await reader.GetCallersAsync(branch, compute, depth: 2);
            Assert.Equal(
                new HashSet<string> { run, load },
                depth2.Select(h => h.Id).ToHashSet(StringComparer.Ordinal));
        }
        finally
        {
            await DeleteBranchAsync(branch);
        }
    }

    private async Task DeleteBranchAsync(string branch)
    {
        var session = _fx.Driver.AsyncSession(o => o.WithDatabase(_fx.Database));
        try
        {
            await session.ExecuteWriteAsync(async tx =>
            {
                var cursor = await tx.RunAsync(
                    "MATCH (s:Symbol {branch: $branch}) DETACH DELETE s",
                    new Dictionary<string, object> { ["branch"] = branch });
                return await cursor.ConsumeAsync();
            });
        }
        finally
        {
            await session.CloseAsync();
        }
    }
}
