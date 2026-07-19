using EdgeHop.Core;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// Class fixture for <see cref="SqliteStoreConformanceTests"/>: one throwaway SQLite
/// store in a unique temp directory, deleted when the class finishes. Unlike the Neo4j
/// fixtures nothing is skippable — the SQLite backend needs no server, which is exactly
/// the property Handoff 3 buys — and the GUID-branch hygiene rule still applies: every
/// test seeds its own GUID branch inside the throwaway store, and the file lives nowhere
/// near the developer's real store (<c>EDGEHOP_SQLITE_PATH</c> is not consulted).
/// </summary>
public sealed class SqliteStoreFixture : IAsyncLifetime
{
    private string? _directory;

    public SqliteGraphStore Store { get; private set; } = null!;

    public Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"edgehop-sqlite-tests-{Guid.NewGuid():N}");
        Store = new SqliteGraphStore(new SqliteSettings(Path.Combine(_directory, "store.db")));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await Store.DisposeAsync();
        if (_directory is not null && Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

/// <summary>
/// Handoff 3 Phase D — the SQLite backend's conformance suite: the same observable
/// contract the Neo4j-backed tests pin (now in the EdgeHop.Neo4j.Tests project) and the
/// pure reconcile-plan tests in <see cref="GraphReconcilerTests"/>, exercised against
/// <see cref="SqliteGraphStore"/>. These always run — a machine with no Neo4j at all
/// still verifies the full graph semantics on this backend.
/// </summary>
public sealed class SqliteStoreConformanceTests : IClassFixture<SqliteStoreFixture>
{
    private readonly SqliteStoreFixture _fx;

    public SqliteStoreConformanceTests(SqliteStoreFixture fx) => _fx = fx;

    private static string NewBranch() => $"test-sqlite-{Guid.NewGuid():N}";

    // ------------------------------------------------------------ round-trip / upsert --

    [Fact]
    public async Task Upsert_is_idempotent_and_updates_properties()
    {
        var branch = NewBranch();
        var writer = _fx.Store.Writer;

        var node = new NodeRow(branch, "Method:A.M()", "M()", SymbolKinds.Method,
            "A.cs", "Asm", IsAbstract: false, IsComponent: true, Routes: ["/m", "/m2"]);
        await writer.UpsertNodesAsync([node]);
        await writer.UpsertNodesAsync([node]);

        var ids = await _fx.Store.Snapshot.GetNodeIdsAsync(branch);
        Assert.Equal(new[] { "Method:A.M()" }, ids);

        // Re-upsert with changed properties: same identity, latest values win.
        await writer.UpsertNodesAsync(
            [node with { Name = "Renamed()", SourceDoc = null, Routes = null }]);
        var hits = await _fx.Store.Reader.FindSymbolsAsync(branch, "renamed");
        var hit = Assert.Single(hits);
        Assert.Equal("Method:A.M()", hit.Id);
        Assert.Equal("Renamed()", hit.Name);
        Assert.Null(hit.SourceDoc);
        Assert.Single(await _fx.Store.Snapshot.GetNodeIdsAsync(branch));
    }

    [Fact]
    public async Task Edge_upsert_is_idempotent_and_skips_missing_endpoints()
    {
        var branch = NewBranch();
        var writer = _fx.Store.Writer;
        await writer.UpsertNodesAsync(
        [
            new NodeRow(branch, "Method:A.M()", "M()", SymbolKinds.Method, "A.cs", "Asm", false),
            new NodeRow(branch, "Method:B.N()", "N()", SymbolKinds.Method, "B.cs", "Asm", false),
        ]);

        var edge = new EdgeRow(branch, "Method:A.M()", "Method:B.N()", EdgeTypes.Calls, "A.cs");
        await writer.UpsertEdgesAsync([edge]);
        await writer.UpsertEdgesAsync([edge]);

        // An edge to a node that does not exist on the branch is silently skipped —
        // the Cypher MATCH…MATCH…MERGE behavior the reconciler relies on.
        await writer.UpsertEdgesAsync(
            [new EdgeRow(branch, "Method:A.M()", "Method:Ghost.G()", EdgeTypes.Calls, "A.cs")]);

        var keys = await _fx.Store.Snapshot.GetEdgeKeysAsync(branch);
        var key = Assert.Single(keys);
        Assert.Equal(new EdgeKey(EdgeTypes.Calls, "Method:A.M()", "Method:B.N()"), key);
    }

    [Fact]
    public async Task Unknown_edge_types_reject_the_whole_batch_before_writing()
    {
        var branch = NewBranch();
        var writer = _fx.Store.Writer;
        await writer.UpsertNodesAsync(
        [
            new NodeRow(branch, "Method:A.M()", "M()", SymbolKinds.Method, "A.cs", "Asm", false),
            new NodeRow(branch, "Method:B.N()", "N()", SymbolKinds.Method, "B.cs", "Asm", false),
        ]);

        var mixed = new[]
        {
            new EdgeRow(branch, "Method:A.M()", "Method:B.N()", EdgeTypes.Calls, "A.cs"),
            new EdgeRow(branch, "Method:A.M()", "Method:B.N()", "EXPLODES", "A.cs"),
        };
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => writer.UpsertEdgesAsync(mixed));
        Assert.Contains("EXPLODES", ex.Message);

        // Whole-batch rejection: the valid CALLS row must not have been written either.
        Assert.Empty(await _fx.Store.Snapshot.GetEdgeKeysAsync(branch));

        await Assert.ThrowsAsync<ArgumentException>(
            () => writer.DeleteEdgesAsync(branch, [new EdgeKey("EXPLODES", "x", "y")]));
    }

    [Fact]
    public async Task DeleteNodes_detaches_their_edges()
    {
        var branch = NewBranch();
        var writer = _fx.Store.Writer;
        await writer.UpsertNodesAsync(
        [
            new NodeRow(branch, "Method:A.M()", "M()", SymbolKinds.Method, "A.cs", "Asm", false),
            new NodeRow(branch, "Method:B.N()", "N()", SymbolKinds.Method, "B.cs", "Asm", false),
            new NodeRow(branch, "Method:C.O()", "O()", SymbolKinds.Method, "C.cs", "Asm", false),
        ]);
        await writer.UpsertEdgesAsync(
        [
            new EdgeRow(branch, "Method:A.M()", "Method:B.N()", EdgeTypes.Calls, "A.cs"),
            new EdgeRow(branch, "Method:B.N()", "Method:C.O()", EdgeTypes.Calls, "B.cs"),
        ]);

        await writer.DeleteNodesAsync(branch, ["Method:B.N()"]);

        Assert.Equal(
            new HashSet<string> { "Method:A.M()", "Method:C.O()" },
            (await _fx.Store.Snapshot.GetNodeIdsAsync(branch)).ToHashSet(StringComparer.Ordinal));
        // Both edges touched B and must be gone with it (the DETACH behavior).
        Assert.Empty(await _fx.Store.Snapshot.GetEdgeKeysAsync(branch));
    }

    [Fact]
    public async Task DeleteBranch_returns_node_count_and_leaves_sibling_branches_alone()
    {
        var branchA = NewBranch();
        var branchB = NewBranch();
        var writer = _fx.Store.Writer;
        foreach (var branch in new[] { branchA, branchB })
        {
            await writer.UpsertNodesAsync(
            [
                new NodeRow(branch, "Method:A.M()", "M()", SymbolKinds.Method, "A.cs", "Asm", false),
                new NodeRow(branch, "Method:B.N()", "N()", SymbolKinds.Method, "B.cs", "Asm", false),
            ]);
            await writer.UpsertEdgesAsync(
                [new EdgeRow(branch, "Method:A.M()", "Method:B.N()", EdgeTypes.Calls, "A.cs")]);
        }

        var deleted = await writer.DeleteBranchAsync(branchA);

        Assert.Equal(2, deleted);
        Assert.Empty(await _fx.Store.Snapshot.GetNodeIdsAsync(branchA));
        Assert.Empty(await _fx.Store.Snapshot.GetEdgeKeysAsync(branchA));
        Assert.Equal(2, (await _fx.Store.Snapshot.GetNodeIdsAsync(branchB)).Count);
        Assert.Single(await _fx.Store.Snapshot.GetEdgeKeysAsync(branchB));

        var branches = await _fx.Store.Snapshot.GetBranchesAsync();
        Assert.DoesNotContain(branches, b => b.Branch == branchA);
        Assert.Contains(branches, b => b.Branch == branchB && b.Nodes == 2);
    }

    // ------------------------------------------------------------------- reconciler --

    [Fact]
    public async Task Reconcile_removes_stale_rows_but_never_touches_sibling_branch()
    {
        var writer = _fx.Store.Writer;
        var snapshot = _fx.Store.Snapshot;
        var reconciler = new GraphReconciler(writer, snapshot);
        var branchA = NewBranch();
        var branchB = NewBranch();
        const string typeId = "NamedType:Recon.Holder";
        const string m1Id = "Method:Recon.Holder.M1()";
        const string m2Id = "Method:Recon.Holder.M2()";

        (List<NodeRow> Nodes, List<EdgeRow> Edges) BuildGraph(string branch) => (
            [
                new NodeRow(branch, typeId, "Holder", SymbolKinds.NamedType, "Holder.cs", "Asm", false),
                new NodeRow(branch, m1Id, "M1()", SymbolKinds.Method, "Holder.cs", "Asm", false),
                new NodeRow(branch, m2Id, "M2()", SymbolKinds.Method, "Holder.cs", "Asm", false),
            ],
            [
                new EdgeRow(branch, typeId, m1Id, EdgeTypes.Contains, "Holder.cs"),
                new EdgeRow(branch, typeId, m2Id, EdgeTypes.Contains, "Holder.cs"),
                new EdgeRow(branch, m1Id, m2Id, EdgeTypes.Calls, "Holder.cs"),
            ]);

        var (nodesA, edgesA) = BuildGraph(branchA);
        var (nodesB, edgesB) = BuildGraph(branchB);
        await writer.UpsertNodesAsync(nodesA.Concat(nodesB).ToList());
        await writer.UpsertEdgesAsync(edgesA.Concat(edgesB).ToList());

        // Desired state on A: M2 (and everything touching it) is gone.
        var desiredNodes = nodesA.Where(n => n.Id != m2Id).ToList();
        var desiredEdges = edgesA.Where(e => e.FromId != m2Id && e.ToId != m2Id).ToList();
        var report = await reconciler.ReconcileAsync(branchA, desiredNodes, desiredEdges);

        Assert.Equal(1, report.NodesDeleted);
        Assert.Equal(2, report.EdgesDeleted);
        Assert.Equal(
            new HashSet<string> { typeId, m1Id },
            (await snapshot.GetNodeIdsAsync(branchA)).ToHashSet(StringComparer.Ordinal));
        Assert.Equal(
            new HashSet<EdgeKey> { new(EdgeTypes.Contains, typeId, m1Id) },
            (await snapshot.GetEdgeKeysAsync(branchA)).ToHashSet());

        // Branch B: byte-for-byte untouched.
        Assert.Equal(3, (await snapshot.GetNodeIdsAsync(branchB)).Count);
        Assert.Equal(3, (await snapshot.GetEdgeKeysAsync(branchB)).Count);
    }

    [Fact]
    public async Task Reconcile_refuses_to_empty_a_branch_unless_allowed()
    {
        var reconciler = new GraphReconciler(_fx.Store.Writer, _fx.Store.Snapshot);
        var branch = NewBranch();
        await _fx.Store.Writer.UpsertNodesAsync(
            [new NodeRow(branch, "Method:A.M()", "M()", SymbolKinds.Method, "A.cs", "Asm", false)]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reconciler.ReconcileAsync(branch, [], []));
        Assert.Single(await _fx.Store.Snapshot.GetNodeIdsAsync(branch));

        var report = await reconciler.ReconcileAsync(branch, [], [], allowEmpty: true);
        Assert.Equal(1, report.NodesDeleted);
        Assert.Empty(await _fx.Store.Snapshot.GetNodeIdsAsync(branch));
    }

    // ------------------------------------------------------------------ find_symbol --

    /// <summary>Seeds the ReaderGraphFixture topology (TinyFixture CALLS shape) under a
    /// fresh GUID branch and returns the ids. When <paramref name="withDecoy"/> is set, a
    /// second branch holding the SAME target id with differently-suffixed callers is
    /// seeded too — the branch-isolation lens from <see cref="ReaderGraphFixture"/>.</summary>
    private async Task<(string Branch, string TypeId, string GreetId, string LoudId, string CallerId, string DecoratorId, string RunId, string DecoyBranch)>
        SeedReaderTopologyAsync(bool withDecoy = false)
    {
        var branch = NewBranch();
        var decoyBranch = NewBranch();
        const string typeId = "NamedType:TinyFixture.Greeter";
        const string greetId = "Method:string TinyFixture.Greeter.Greet(string)";

        async Task SeedAsync(string b, string suffix)
        {
            await _fx.Store.Writer.UpsertNodesAsync(
            [
                new NodeRow(b, typeId, "Greeter", SymbolKinds.NamedType, "Greeter.cs", "Tiny", false),
                new NodeRow(b, greetId, "string Greeter.Greet(string name)", SymbolKinds.Method, "Greeter.cs", "Tiny", false),
                new NodeRow(b, $"Method:string TinyFixture.LoudGreeter.Greet(string){suffix}", "string LoudGreeter.Greet(string name)", SymbolKinds.Method, "LoudGreeter.cs", "Tiny", false),
                new NodeRow(b, $"Method:string TinyFixture.Caller.CallGreet(){suffix}", "string Caller.CallGreet()", SymbolKinds.Method, "Caller.cs", "Tiny", false),
                new NodeRow(b, $"Method:string TinyFixture.Decorator.Decorate(){suffix}", "string Decorator.Decorate()", SymbolKinds.Method, "Sub/Decorator.cs", "Tiny", false),
                new NodeRow(b, $"Method:string TinyFixture.App.Run(){suffix}", "string App.Run()", SymbolKinds.Method, "App.cs", "Tiny", false),
            ]);
            await _fx.Store.Writer.UpsertEdgesAsync(
            [
                new EdgeRow(b, $"Method:string TinyFixture.LoudGreeter.Greet(string){suffix}", greetId, EdgeTypes.Calls, "LoudGreeter.cs"),
                new EdgeRow(b, $"Method:string TinyFixture.Caller.CallGreet(){suffix}", greetId, EdgeTypes.Calls, "Caller.cs"),
                new EdgeRow(b, $"Method:string TinyFixture.Decorator.Decorate(){suffix}", greetId, EdgeTypes.Calls, "Sub/Decorator.cs"),
                new EdgeRow(b, $"Method:string TinyFixture.App.Run(){suffix}", $"Method:string TinyFixture.Caller.CallGreet(){suffix}", EdgeTypes.Calls, "App.cs"),
            ]);
        }

        var suffix = $"|{branch}";
        await SeedAsync(branch, suffix);
        if (withDecoy)
        {
            // Same target id on a sibling branch, callers suffixed differently: a reader
            // missing a branch predicate would leak these into the result.
            await SeedAsync(decoyBranch, $"|{decoyBranch}");
        }

        return (branch, typeId, greetId,
            $"Method:string TinyFixture.LoudGreeter.Greet(string){suffix}",
            $"Method:string TinyFixture.Caller.CallGreet(){suffix}",
            $"Method:string TinyFixture.Decorator.Decorate(){suffix}",
            $"Method:string TinyFixture.App.Run(){suffix}",
            decoyBranch);
    }

    [Fact]
    public async Task FindSymbols_CaseInsensitiveSubstring_FindsGreetSymbols_OrderedByName()
    {
        var seed = await SeedReaderTopologyAsync();
        var hits = await _fx.Store.Reader.FindSymbolsAsync(seed.Branch, "greet");

        var ids = hits.Select(h => h.Id).ToList();
        Assert.Equal(4, hits.Count);
        Assert.Contains(seed.TypeId, ids);
        Assert.Contains(seed.GreetId, ids);
        Assert.Contains(seed.LoudId, ids);
        Assert.Contains(seed.CallerId, ids);

        // Ordered by name then id — the ordering contract shared with Neo4j.
        Assert.Equal(
            hits.OrderBy(h => h.Name, StringComparer.Ordinal)
                .ThenBy(h => h.Id, StringComparer.Ordinal)
                .Select(h => h.Id).ToList(),
            ids);

        var greet = Assert.Single(hits, h => h.Id == seed.GreetId);
        Assert.Equal("string Greeter.Greet(string name)", greet.Name);
        Assert.Equal(SymbolKinds.Method, greet.Kind);
        Assert.Equal("Greeter.cs", greet.SourceDoc);
    }

    [Fact]
    public async Task FindSymbols_KindFilter_LimitCap_AndEmptyQuery()
    {
        var seed = await SeedReaderTopologyAsync();
        var reader = _fx.Store.Reader;

        var typed = Assert.Single(await reader.FindSymbolsAsync(seed.Branch, "greet", kind: SymbolKinds.NamedType));
        Assert.Equal(seed.TypeId, typed.Id);

        Assert.Equal(2, (await reader.FindSymbolsAsync(seed.Branch, "greet", limit: 2)).Count);
        Assert.Equal(4, (await reader.FindSymbolsAsync(seed.Branch, "greet", limit: 1000)).Count);

        Assert.Empty(await reader.FindSymbolsAsync(seed.Branch, string.Empty));
        Assert.Empty(await reader.FindSymbolsAsync(seed.Branch, "   "));
    }

    [Fact]
    public async Task FindSymbols_WildcardsAndLikeMetacharacters_MatchLiterally()
    {
        var branch = NewBranch();
        await _fx.Store.Writer.UpsertNodesAsync(
        [
            new NodeRow(branch, "Method:W.P()", "Percent%Name", SymbolKinds.Method, "W.cs", "Asm", false),
            new NodeRow(branch, "Method:W.U()", "Under_Name", SymbolKinds.Method, "W.cs", "Asm", false),
            new NodeRow(branch, "Method:W.S()", "Back\\Slash", SymbolKinds.Method, "W.cs", "Asm", false),
            new NodeRow(branch, "Method:W.G()", "Greet", SymbolKinds.Method, "W.cs", "Asm", false),
        ]);
        var reader = _fx.Store.Reader;

        // LIKE metacharacters in the query must match themselves only — never act as
        // wildcards (the documented "every search behaves like *query*" semantics).
        Assert.Equal("Method:W.P()", Assert.Single(await reader.FindSymbolsAsync(branch, "percent%")).Id);
        Assert.Equal("Method:W.U()", Assert.Single(await reader.FindSymbolsAsync(branch, "under_")).Id);
        Assert.Equal("Method:W.S()", Assert.Single(await reader.FindSymbolsAsync(branch, "back\\")).Id);
        // '_' as a wildcard would also match 'GreeX'-style names; here it matches nothing.
        Assert.Empty(await reader.FindSymbolsAsync(branch, "Gree_"));
        // '*' is literal (no glob semantics): finds nothing.
        Assert.Empty(await reader.FindSymbolsAsync(branch, "greet*"));
    }

    [Fact]
    public async Task FindSymbols_InjectionAttemptInQuery_IsTreatedAsLiteral()
    {
        var seed = await SeedReaderTopologyAsync();
        Assert.Empty(await _fx.Store.Reader.FindSymbolsAsync(seed.Branch, "') OR 1=1; DROP TABLE nodes; --"));
        Assert.Equal(4, (await _fx.Store.Reader.FindSymbolsAsync(seed.Branch, "greet")).Count);
    }

    [Fact]
    public async Task FindSymbols_DoesNotLeakAcrossBranches()
    {
        var seed = await SeedReaderTopologyAsync(withDecoy: true);
        var hits = await _fx.Store.Reader.FindSymbolsAsync(seed.Branch, "greet");

        Assert.Equal(4, hits.Count);
        Assert.Equal(4, hits.Select(h => h.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(hits, h => h.Id.EndsWith($"|{seed.DecoyBranch}", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ get_callers --

    [Fact]
    public async Task GetCallers_Depth1_And_Depth2_MatchTheContract()
    {
        var seed = await SeedReaderTopologyAsync();
        var reader = _fx.Store.Reader;

        var depth1 = await reader.GetCallersAsync(seed.Branch, seed.GreetId);
        var ids1 = depth1.Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(3, depth1.Count);
        Assert.Contains(seed.LoudId, ids1);
        Assert.Contains(seed.CallerId, ids1);
        Assert.Contains(seed.DecoratorId, ids1);
        Assert.DoesNotContain(seed.RunId, ids1);
        Assert.DoesNotContain(seed.GreetId, ids1);

        var depth2 = await reader.GetCallersAsync(seed.Branch, seed.GreetId, depth: 2);
        var ids2 = depth2.Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(4, depth2.Count);
        Assert.Contains(seed.RunId, ids2);
        Assert.DoesNotContain(seed.GreetId, ids2);

        // Ordering contract, same as find_symbol.
        Assert.Equal(
            depth2.OrderBy(h => h.Name, StringComparer.Ordinal)
                .ThenBy(h => h.Id, StringComparer.Ordinal)
                .Select(h => h.Id).ToList(),
            depth2.Select(h => h.Id).ToList());
    }

    [Fact]
    public async Task GetCallers_CycleReachingBackToTarget_TerminatesAndExcludesTarget()
    {
        // A <-> B mutual recursion plus C -> A: BFS/CTE must terminate, dedupe, and
        // never report the target as its own caller even though the cycle reaches it.
        var branch = NewBranch();
        const string a = "Method:Cycle.A()";
        const string b = "Method:Cycle.B()";
        const string c = "Method:Cycle.C()";
        await _fx.Store.Writer.UpsertNodesAsync(
        [
            new NodeRow(branch, a, "A()", SymbolKinds.Method, "Cycle.cs", "Asm", false),
            new NodeRow(branch, b, "B()", SymbolKinds.Method, "Cycle.cs", "Asm", false),
            new NodeRow(branch, c, "C()", SymbolKinds.Method, "Cycle.cs", "Asm", false),
        ]);
        await _fx.Store.Writer.UpsertEdgesAsync(
        [
            new EdgeRow(branch, a, b, EdgeTypes.Calls, "Cycle.cs"),
            new EdgeRow(branch, b, a, EdgeTypes.Calls, "Cycle.cs"),
            new EdgeRow(branch, c, a, EdgeTypes.Calls, "Cycle.cs"),
        ]);

        var callers = await _fx.Store.Reader.GetCallersAsync(branch, a, depth: 10);
        Assert.Equal(
            new HashSet<string> { b, c },
            callers.Select(h => h.Id).ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public async Task GetCallers_And_GetRelationships_FollowJsCallsAcrossTheCsToJsBoundary()
    {
        // C#: Page.Load() -CALLS-> Widget.Refresh() -JS_CALLS-> js|…widget.js#getWidget.
        // get_callers on the JS function surfaces the direct C# caller (depth 1) and, via the
        // CALLS+JS_CALLS chain, its transitive C# caller (depth 2) — the cross-tier traversal
        // that mirrors HTTP_CALLS. The JS node id carries the mandatory js| tier tag.
        var branch = NewBranch();
        const string load = "Method:Web.Page.Load()";
        const string refresh = "Method:Web.Widget.Refresh()";
        const string getWidget = "Method:js|wwwroot/js/widget.js#getWidget";
        await _fx.Store.Writer.UpsertNodesAsync(
        [
            new NodeRow(branch, load, "Load()", SymbolKinds.Method, "Page.razor.cs", "Web", false),
            new NodeRow(branch, refresh, "Refresh()", SymbolKinds.Method, "Widget.razor.cs", "Web", false),
            new NodeRow(branch, getWidget, "getWidget", SymbolKinds.Method, "wwwroot/js/widget.js", "", false),
        ]);
        await _fx.Store.Writer.UpsertEdgesAsync(
        [
            new EdgeRow(branch, load, refresh, EdgeTypes.Calls, "Page.razor.cs"),
            new EdgeRow(branch, refresh, getWidget, EdgeTypes.JsCalls, "Widget.razor.cs"),
        ]);

        var depth1 = await _fx.Store.Reader.GetCallersAsync(branch, getWidget, depth: 1);
        Assert.Equal(refresh, Assert.Single(depth1).Id);

        var depth2 = await _fx.Store.Reader.GetCallersAsync(branch, getWidget, depth: 2);
        Assert.Equal(
            new HashSet<string> { refresh, load },
            depth2.Select(h => h.Id).ToHashSet(StringComparer.Ordinal));

        // JS_CALLS is a first-class edge type in get_relationships too.
        const int limit = EdgeHopQueryService.MaxRequestLimit;
        var rel = await _fx.Store.Reader.GetRelationshipsAsync(
            branch, refresh, RelationshipDirection.Out, EdgeTypes.JsCalls, 1, limit);
        Assert.Equal((getWidget, EdgeTypes.JsCalls, RelationshipDirections.Out), Edge(Assert.Single(rel)));
    }

    [Fact]
    public async Task GetCallers_And_GetRelationships_FollowJsInvokesAcrossTheJsToCsBoundary()
    {
        // The full round trip: Page.Load() -JS_CALLS-> js|app.js#run -JS_INVOKES-> Api.Compute()
        // (a [JSInvokable] method). get_callers on the C# invokable surfaces the JS caller (depth
        // 1) and, chaining JS_CALLS+JS_INVOKES, the C# method that reached into JS (depth 2).
        var branch = NewBranch();
        const string load = "Method:Web.Page.Load()";
        const string run = "Method:js|wwwroot/js/app.js#run";
        const string compute = "Method:Api.Compute()";
        await _fx.Store.Writer.UpsertNodesAsync(
        [
            new NodeRow(branch, load, "Load()", SymbolKinds.Method, "Page.razor.cs", "Web", false),
            new NodeRow(branch, run, "run", SymbolKinds.Method, "wwwroot/js/app.js", "", false),
            new NodeRow(branch, compute, "Compute()", SymbolKinds.Method, "Api.cs", "Api", false),
        ]);
        await _fx.Store.Writer.UpsertEdgesAsync(
        [
            new EdgeRow(branch, load, run, EdgeTypes.JsCalls, "Page.razor"),
            new EdgeRow(branch, run, compute, EdgeTypes.JsInvokes, "wwwroot/js/app.js"),
        ]);

        var depth1 = await _fx.Store.Reader.GetCallersAsync(branch, compute, depth: 1);
        Assert.Equal(run, Assert.Single(depth1).Id);

        var depth2 = await _fx.Store.Reader.GetCallersAsync(branch, compute, depth: 2);
        Assert.Equal(
            new HashSet<string> { run, load },
            depth2.Select(h => h.Id).ToHashSet(StringComparer.Ordinal));

        const int limit = EdgeHopQueryService.MaxRequestLimit;
        var rel = await _fx.Store.Reader.GetRelationshipsAsync(
            branch, run, RelationshipDirection.Out, EdgeTypes.JsInvokes, 1, limit);
        Assert.Equal((compute, EdgeTypes.JsInvokes, RelationshipDirections.Out), Edge(Assert.Single(rel)));
    }

    [Fact]
    public async Task GetCallers_Validation_UnknownId_AndBranchIsolation()
    {
        var seed = await SeedReaderTopologyAsync(withDecoy: true);
        var reader = _fx.Store.Reader;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => reader.GetCallersAsync(seed.Branch, seed.GreetId, depth: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => reader.GetCallersAsync(seed.Branch, seed.GreetId, depth: 11));
        await Assert.ThrowsAsync<ArgumentException>(
            () => reader.GetCallersAsync(seed.Branch, "   "));

        Assert.Empty(await reader.GetCallersAsync(seed.Branch, "Method:Missing.Nope()", depth: 2));

        // The decoy branch holds the SAME target id with its own callers; none of its
        // suffixed ids may appear here.
        foreach (var depth in new[] { 1, 2 })
        {
            var hits = await reader.GetCallersAsync(seed.Branch, seed.GreetId, depth);
            Assert.Equal(depth == 1 ? 3 : 4, hits.Count);
            Assert.All(hits, h =>
                Assert.EndsWith($"|{seed.Branch}", h.Id, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task GetCallers_InjectionAttemptInSymbolId_IsTreatedAsLiteral()
    {
        var seed = await SeedReaderTopologyAsync();
        var evil = "Method:evil'\"; DROP TABLE nodes; --\\";
        Assert.Empty(await _fx.Store.Reader.GetCallersAsync(seed.Branch, evil, depth: 2));
        Assert.Equal(4, (await _fx.Store.Reader.GetCallersAsync(seed.Branch, seed.GreetId, depth: 2)).Count);
    }

    // ---------------------------------------------------- query service integration --

    [Fact]
    public async Task QueryService_TruncationIsExact_OverSqliteReader()
    {
        var branch = NewBranch();
        var service = new EdgeHopQueryService(_fx.Store.Reader);

        // Exactly DefaultLimit matches: NOT truncated. One more: truncated.
        var nodes = Enumerable.Range(0, EdgeHopQueryService.DefaultLimit)
            .Select(i => new NodeRow(branch, $"Method:T.M{i:D3}()", $"Trunc{i:D3}()",
                SymbolKinds.Method, "T.cs", "Asm", false))
            .ToList();
        await _fx.Store.Writer.UpsertNodesAsync(nodes);

        var exact = await service.FindSymbolsAsync(branch, "trunc");
        Assert.Equal(EdgeHopQueryService.DefaultLimit, exact.Hits.Count);
        Assert.False(exact.Truncated);

        await _fx.Store.Writer.UpsertNodesAsync(
            [new NodeRow(branch, "Method:T.MX()", "TruncX()", SymbolKinds.Method, "T.cs", "Asm", false)]);
        var over = await service.FindSymbolsAsync(branch, "trunc");
        Assert.Equal(EdgeHopQueryService.DefaultLimit, over.Hits.Count);
        Assert.True(over.Truncated);
    }

    // -------------------------------------------------------------- SymbolHit metadata --

    [Fact]
    public async Task FindSymbols_SurfacesIsComponentAndRoutes_AndNullRoutesRoundTrip()
    {
        var branch = NewBranch();
        await _fx.Store.Writer.UpsertNodesAsync(
        [
            // A routable Razor component: isComponent true, two route templates.
            new NodeRow(branch, "NamedType:Meta.Home", "MetaHome", SymbolKinds.NamedType,
                "Home.razor", "Web", false, IsComponent: true, Routes: ["/x", "/y"]),
            // A plain method: not a component, no routes.
            new NodeRow(branch, "Method:Meta.Plain()", "MetaPlain", SymbolKinds.Method,
                "Plain.cs", "Web", false),
        ]);

        var hits = await _fx.Store.Reader.FindSymbolsAsync(branch, "meta");
        Assert.Equal(2, hits.Count);

        var home = Assert.Single(hits, h => h.Id == "NamedType:Meta.Home");
        Assert.True(home.IsComponent);
        Assert.Equal(new[] { "/x", "/y" }, home.Routes);

        var plain = Assert.Single(hits, h => h.Id == "Method:Meta.Plain()");
        Assert.False(plain.IsComponent);
        Assert.Null(plain.Routes);
    }

    // ------------------------------------------------------------- get_relationships --

    /// <summary>One related symbol (its id) with the edge type and direction that reached
    /// it — the observable projection of a <see cref="RelationshipHit"/>.</summary>
    private static (string Id, string EdgeType, string Direction) Edge(RelationshipHit h) =>
        (h.Symbol.Id, h.EdgeType, h.Direction);

    private sealed record RelTopology(
        string Branch, string DecoyBranch,
        string IGreeter, string Greeter, string Loud,
        string GreeterGreet, string LoudGreet,
        string Home, string Child, string Grandchild,
        string WebCall, string ApiEndpoint,
        string SelfRecurse, string DecoyOnly);

    /// <summary>Seeds a mixed-edge-type topology under a fresh GUID branch: IMPLEMENTS,
    /// INHERITS, OVERRIDES, CONTAINS, a two-hop RENDERS chain, HTTP_CALLS, and a CALLS
    /// self-loop (to prove self-exclusion). When <paramref name="withDecoy"/> is set, a
    /// sibling branch holds the SAME ids PLUS an extra <c>Greeter --REFERENCES--&gt;
    /// DecoyOnly</c> edge — a reader missing a branch predicate would leak DecoyOnly into
    /// the primary branch's out-neighbors.</summary>
    private async Task<RelTopology> SeedRelationshipTopologyAsync(bool withDecoy = false)
    {
        var topo = new RelTopology(
            Branch: NewBranch(), DecoyBranch: NewBranch(),
            IGreeter: "NamedType:Rel.IGreeter",
            Greeter: "NamedType:Rel.Greeter",
            Loud: "NamedType:Rel.LoudGreeter",
            GreeterGreet: "Method:Rel.Greeter.Greet()",
            LoudGreet: "Method:Rel.LoudGreeter.Greet()",
            Home: "NamedType:Rel.Home",
            Child: "NamedType:Rel.Child",
            Grandchild: "NamedType:Rel.Grandchild",
            WebCall: "Method:Rel.WebClient.Call()",
            ApiEndpoint: "Method:Rel.Api.Endpoint()",
            SelfRecurse: "Method:Rel.Self.Recurse()",
            DecoyOnly: "NamedType:Rel.DecoyOnly");

        async Task SeedAsync(string b, bool decoyEdge)
        {
            var nodes = new List<NodeRow>
            {
                new(b, topo.IGreeter, "IGreeter", SymbolKinds.NamedType, "IGreeter.cs", "Rel", true),
                new(b, topo.Greeter, "Greeter", SymbolKinds.NamedType, "Greeter.cs", "Rel", false),
                new(b, topo.Loud, "LoudGreeter", SymbolKinds.NamedType, "LoudGreeter.cs", "Rel", false),
                new(b, topo.GreeterGreet, "Greet()", SymbolKinds.Method, "Greeter.cs", "Rel", false),
                new(b, topo.LoudGreet, "Greet()", SymbolKinds.Method, "LoudGreeter.cs", "Rel", false),
                new(b, topo.Home, "Home", SymbolKinds.NamedType, "Home.razor", "Rel", false, IsComponent: true),
                new(b, topo.Child, "Child", SymbolKinds.NamedType, "Child.razor", "Rel", false, IsComponent: true),
                new(b, topo.Grandchild, "Grandchild", SymbolKinds.NamedType, "Grandchild.razor", "Rel", false, IsComponent: true),
                new(b, topo.WebCall, "Call()", SymbolKinds.Method, "WebClient.cs", "Rel", false),
                new(b, topo.ApiEndpoint, "Endpoint()", SymbolKinds.Method, "Api.cs", "Rel", false),
                new(b, topo.SelfRecurse, "Recurse()", SymbolKinds.Method, "Self.cs", "Rel", false),
            };
            var edges = new List<EdgeRow>
            {
                new(b, topo.Greeter, topo.IGreeter, EdgeTypes.Implements, "Greeter.cs"),
                new(b, topo.Loud, topo.Greeter, EdgeTypes.Inherits, "LoudGreeter.cs"),
                new(b, topo.LoudGreet, topo.GreeterGreet, EdgeTypes.Overrides, "LoudGreeter.cs"),
                new(b, topo.Greeter, topo.GreeterGreet, EdgeTypes.Contains, "Greeter.cs"),
                new(b, topo.Home, topo.Child, EdgeTypes.Renders, "Home.razor"),
                new(b, topo.Child, topo.Grandchild, EdgeTypes.Renders, "Child.razor"),
                new(b, topo.WebCall, topo.ApiEndpoint, EdgeTypes.HttpCalls, "WebClient.cs"),
                new(b, topo.SelfRecurse, topo.SelfRecurse, EdgeTypes.Calls, "Self.cs"),
            };
            if (decoyEdge)
            {
                nodes.Add(new NodeRow(b, topo.DecoyOnly, "DecoyOnly", SymbolKinds.NamedType, "Decoy.cs", "Rel", false));
                edges.Add(new EdgeRow(b, topo.Greeter, topo.DecoyOnly, EdgeTypes.References, "Greeter.cs"));
            }

            await _fx.Store.Writer.UpsertNodesAsync(nodes);
            await _fx.Store.Writer.UpsertEdgesAsync(edges);
        }

        await SeedAsync(topo.Branch, decoyEdge: false);
        if (withDecoy)
        {
            await SeedAsync(topo.DecoyBranch, decoyEdge: true);
        }

        return topo;
    }

    [Fact]
    public async Task GetRelationships_Depth1_Out_In_Both_CarryEdgeTypeAndDirection()
    {
        var t = await SeedRelationshipTopologyAsync();
        var reader = _fx.Store.Reader;
        const int limit = EdgeHopQueryService.MaxRequestLimit;

        // OUT of Greeter: IMPLEMENTS -> IGreeter, CONTAINS -> Greeter.Greet().
        var outHits = await reader.GetRelationshipsAsync(
            t.Branch, t.Greeter, RelationshipDirection.Out, null, 1, limit);
        Assert.Equal(
            new HashSet<(string, string, string)>
            {
                (t.IGreeter, EdgeTypes.Implements, RelationshipDirections.Out),
                (t.GreeterGreet, EdgeTypes.Contains, RelationshipDirections.Out),
            },
            outHits.Select(Edge).ToHashSet());

        // IN to Greeter: LoudGreeter --INHERITS--> Greeter.
        var inHits = await reader.GetRelationshipsAsync(
            t.Branch, t.Greeter, RelationshipDirection.In, null, 1, limit);
        var inHit = Assert.Single(inHits);
        Assert.Equal((t.Loud, EdgeTypes.Inherits, RelationshipDirections.In), Edge(inHit));

        // BOTH: the union of the two directions.
        var bothHits = await reader.GetRelationshipsAsync(
            t.Branch, t.Greeter, RelationshipDirection.Both, null, 1, limit);
        Assert.Equal(
            new HashSet<(string, string, string)>
            {
                (t.IGreeter, EdgeTypes.Implements, RelationshipDirections.Out),
                (t.GreeterGreet, EdgeTypes.Contains, RelationshipDirections.Out),
                (t.Loud, EdgeTypes.Inherits, RelationshipDirections.In),
            },
            bothHits.Select(Edge).ToHashSet());

        // Ordering contract: edgeType, direction, name, id.
        Assert.Equal(
            bothHits.OrderBy(h => h.EdgeType, StringComparer.Ordinal)
                .ThenBy(h => h.Direction, StringComparer.Ordinal)
                .ThenBy(h => h.Symbol.Name, StringComparer.Ordinal)
                .ThenBy(h => h.Symbol.Id, StringComparer.Ordinal)
                .Select(h => h.Symbol.Id).ToList(),
            bothHits.Select(h => h.Symbol.Id).ToList());
    }

    [Fact]
    public async Task GetRelationships_EdgeTypeFilter_KeepsOnlyThatType()
    {
        var t = await SeedRelationshipTopologyAsync();
        const int limit = EdgeHopQueryService.MaxRequestLimit;

        var contains = await _fx.Store.Reader.GetRelationshipsAsync(
            t.Branch, t.Greeter, RelationshipDirection.Out, EdgeTypes.Contains, 1, limit);
        var hit = Assert.Single(contains);
        Assert.Equal((t.GreeterGreet, EdgeTypes.Contains, RelationshipDirections.Out), Edge(hit));

        // HTTP_CALLS is a first-class edge type: it filters like any other.
        var http = await _fx.Store.Reader.GetRelationshipsAsync(
            t.Branch, t.WebCall, RelationshipDirection.Out, EdgeTypes.HttpCalls, 1, limit);
        Assert.Equal(t.ApiEndpoint, Assert.Single(http).Symbol.Id);
    }

    [Fact]
    public async Task GetRelationships_Depth2_SingleType_ReachesTheGrandchild()
    {
        var t = await SeedRelationshipTopologyAsync();
        const int limit = EdgeHopQueryService.MaxRequestLimit;

        // Home --RENDERS--> Child --RENDERS--> Grandchild: depth 2 reaches both.
        var depth2 = await _fx.Store.Reader.GetRelationshipsAsync(
            t.Branch, t.Home, RelationshipDirection.Out, EdgeTypes.Renders, 2, limit);
        Assert.Equal(
            new HashSet<string> { t.Child, t.Grandchild },
            depth2.Select(h => h.Symbol.Id).ToHashSet(StringComparer.Ordinal));
        Assert.All(depth2, h => Assert.Equal(EdgeTypes.Renders, h.EdgeType));

        // Depth 1 reaches only the direct child.
        var depth1 = await _fx.Store.Reader.GetRelationshipsAsync(
            t.Branch, t.Home, RelationshipDirection.Out, EdgeTypes.Renders, 1, limit);
        Assert.Equal(t.Child, Assert.Single(depth1).Symbol.Id);
    }

    [Fact]
    public async Task GetRelationships_ExcludesTheAnchorEvenAcrossASelfLoop()
    {
        var t = await SeedRelationshipTopologyAsync();
        const int limit = EdgeHopQueryService.MaxRequestLimit;

        // Self.Recurse() CALLS itself: the only neighbor is the anchor, which is excluded.
        Assert.Empty(await _fx.Store.Reader.GetRelationshipsAsync(
            t.Branch, t.SelfRecurse, RelationshipDirection.Both, null, 1, limit));
        Assert.Empty(await _fx.Store.Reader.GetRelationshipsAsync(
            t.Branch, t.SelfRecurse, RelationshipDirection.Out, EdgeTypes.Calls, 2, limit));
    }

    [Fact]
    public async Task GetRelationships_DoesNotLeakAcrossBranches()
    {
        var t = await SeedRelationshipTopologyAsync(withDecoy: true);
        const int limit = EdgeHopQueryService.MaxRequestLimit;

        // Primary branch: Greeter's out-neighbors are exactly the two seeded there — the
        // decoy branch's extra REFERENCES -> DecoyOnly edge must not leak in.
        var primary = await _fx.Store.Reader.GetRelationshipsAsync(
            t.Branch, t.Greeter, RelationshipDirection.Out, null, 1, limit);
        Assert.Equal(
            new HashSet<string> { t.IGreeter, t.GreeterGreet },
            primary.Select(h => h.Symbol.Id).ToHashSet(StringComparer.Ordinal));
        Assert.DoesNotContain(primary, h => h.Symbol.Id == t.DecoyOnly);

        // The decoy branch really does hold that extra edge (so the isolation above is real).
        var decoy = await _fx.Store.Reader.GetRelationshipsAsync(
            t.DecoyBranch, t.Greeter, RelationshipDirection.Out, null, 1, limit);
        Assert.Contains(decoy, h => h.Symbol.Id == t.DecoyOnly && h.EdgeType == EdgeTypes.References);
    }

    [Fact]
    public async Task GetRelationships_UnknownId_ReturnsEmpty()
    {
        var t = await SeedRelationshipTopologyAsync();
        Assert.Empty(await _fx.Store.Reader.GetRelationshipsAsync(
            t.Branch, "Method:Missing.Nope()", RelationshipDirection.Both, null, 1,
            EdgeHopQueryService.MaxRequestLimit));
    }

    [Fact]
    public async Task GetRelationships_InjectionAttemptInSymbolId_IsTreatedAsLiteral()
    {
        var t = await SeedRelationshipTopologyAsync();
        var evil = "Method:evil'\"; DROP TABLE edges; --\\";
        Assert.Empty(await _fx.Store.Reader.GetRelationshipsAsync(
            t.Branch, evil, RelationshipDirection.Both, null, 1, EdgeHopQueryService.MaxRequestLimit));
        // The graph is intact afterward.
        Assert.Equal(2, (await _fx.Store.Reader.GetRelationshipsAsync(
            t.Branch, t.Greeter, RelationshipDirection.Out, null, 1,
            EdgeHopQueryService.MaxRequestLimit)).Count);
    }

    [Fact]
    public async Task GetRelationships_Service_DepthAboveOneWithoutEdgeType_Throws()
    {
        var t = await SeedRelationshipTopologyAsync();
        var service = new EdgeHopQueryService(_fx.Store.Reader);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.GetRelationshipsAsync(t.Branch, t.Home, RelationshipDirection.Out, edgeType: null, depth: 2));
        Assert.Contains("depth > 1 requires a single --edge-type", ex.Message);
    }

    [Fact]
    public async Task GetRelationships_Service_UnknownEdgeType_Throws()
    {
        var t = await SeedRelationshipTopologyAsync();
        var service = new EdgeHopQueryService(_fx.Store.Reader);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.GetRelationshipsAsync(t.Branch, t.Greeter, RelationshipDirection.Out, edgeType: "BOGUS"));
        Assert.Contains("Unknown edge type 'BOGUS'", ex.Message);
    }

    // --------------------------------------------------------------------- get_path --

    /// <summary>Seeds a directed CALLS chain A -&gt; B -&gt; C plus an unreachable D under a
    /// fresh GUID branch.</summary>
    private async Task<(string Branch, string A, string B, string C, string D)> SeedPathChainAsync()
    {
        var branch = NewBranch();
        const string a = "Method:Path.A()";
        const string b = "Method:Path.B()";
        const string c = "Method:Path.C()";
        const string d = "Method:Path.D()";
        await _fx.Store.Writer.UpsertNodesAsync(
        [
            new NodeRow(branch, a, "A()", SymbolKinds.Method, "Path.cs", "Asm", false),
            new NodeRow(branch, b, "B()", SymbolKinds.Method, "Path.cs", "Asm", false),
            new NodeRow(branch, c, "C()", SymbolKinds.Method, "Path.cs", "Asm", false),
            new NodeRow(branch, d, "D()", SymbolKinds.Method, "Path.cs", "Asm", false),
        ]);
        await _fx.Store.Writer.UpsertEdgesAsync(
        [
            new EdgeRow(branch, a, b, EdgeTypes.Calls, "Path.cs"),
            new EdgeRow(branch, b, c, EdgeTypes.Calls, "Path.cs"),
        ]);
        return (branch, a, b, c, d);
    }

    [Fact]
    public async Task GetPath_MultiHop_ReturnsOrderedNodesAndEdgeTypes()
    {
        var s = await SeedPathChainAsync();

        var path = await _fx.Store.Reader.GetPathAsync(
            s.Branch, s.A, s.C, null, IGraphReader.DefaultPathLength);

        Assert.True(path.Found);
        Assert.Equal(s.A, path.FromId);
        Assert.Equal(s.C, path.ToId);
        Assert.Equal(new[] { s.A, s.B, s.C }, path.Nodes.Select(n => n.Symbol.Id).ToArray());
        // EdgeTypeFromPrev: null at the anchor, then the edge type into each subsequent node.
        Assert.Null(path.Nodes[0].EdgeTypeFromPrev);
        Assert.Equal(EdgeTypes.Calls, path.Nodes[1].EdgeTypeFromPrev);
        Assert.Equal(EdgeTypes.Calls, path.Nodes[2].EdgeTypeFromPrev);
    }

    [Fact]
    public async Task GetPath_FromIdEqualsToId_IsAFoundZeroLengthPath()
    {
        var s = await SeedPathChainAsync();

        var path = await _fx.Store.Reader.GetPathAsync(
            s.Branch, s.A, s.A, null, IGraphReader.DefaultPathLength);

        Assert.True(path.Found);
        var only = Assert.Single(path.Nodes);
        Assert.Equal(s.A, only.Symbol.Id);
        Assert.Null(only.EdgeTypeFromPrev);
    }

    [Fact]
    public async Task GetPath_Unreachable_ReturnsFoundFalseWithNoNodes()
    {
        var s = await SeedPathChainAsync();

        // D has no incoming edge; C cannot reach back to A over directed out-edges.
        var toD = await _fx.Store.Reader.GetPathAsync(s.Branch, s.A, s.D, null, IGraphReader.DefaultPathLength);
        Assert.False(toD.Found);
        Assert.Empty(toD.Nodes);

        var backward = await _fx.Store.Reader.GetPathAsync(s.Branch, s.C, s.A, null, IGraphReader.DefaultPathLength);
        Assert.False(backward.Found);
        Assert.Empty(backward.Nodes);
    }

    [Fact]
    public async Task GetPath_EdgeTypeFilter_ChangesReachability()
    {
        var s = await SeedPathChainAsync();

        // Restricting to CALLS keeps the chain reachable...
        var viaCalls = await _fx.Store.Reader.GetPathAsync(
            s.Branch, s.A, s.C, EdgeTypes.Calls, IGraphReader.DefaultPathLength);
        Assert.True(viaCalls.Found);
        Assert.Equal(3, viaCalls.Nodes.Count);

        // ...restricting to a type that no edge on the path carries makes C unreachable.
        var viaContains = await _fx.Store.Reader.GetPathAsync(
            s.Branch, s.A, s.C, EdgeTypes.Contains, IGraphReader.DefaultPathLength);
        Assert.False(viaContains.Found);
        Assert.Empty(viaContains.Nodes);
    }

    [Fact]
    public async Task GetPath_MaxLength_BoundsTheWalk()
    {
        var s = await SeedPathChainAsync();

        // The A -> C path is two hops long: maxLength 1 cannot reach it, maxLength 2 can.
        Assert.False((await _fx.Store.Reader.GetPathAsync(s.Branch, s.A, s.C, null, 1)).Found);
        Assert.True((await _fx.Store.Reader.GetPathAsync(s.Branch, s.A, s.C, null, 2)).Found);
    }

    [Fact]
    public async Task GetPath_InjectionAttemptInEndpoints_IsTreatedAsLiteral()
    {
        var s = await SeedPathChainAsync();
        var evil = "Method:evil'\"; DROP TABLE edges; --\\";

        Assert.False((await _fx.Store.Reader.GetPathAsync(
            s.Branch, evil, s.C, null, IGraphReader.DefaultPathLength)).Found);
        Assert.False((await _fx.Store.Reader.GetPathAsync(
            s.Branch, s.A, evil, null, IGraphReader.DefaultPathLength)).Found);
        // The real path is still reachable afterward.
        Assert.True((await _fx.Store.Reader.GetPathAsync(
            s.Branch, s.A, s.C, null, IGraphReader.DefaultPathLength)).Found);
    }

    // ------------------------------------------------------------------- graph_stats --

    /// <summary>Seeds a small graph where a NamedType has a high CONTAINS degree (4 members)
    /// but no other edges, while a method "Hub" is called by three others (non-CONTAINS
    /// degree 3). Returns the branch and the two ids the god-node ordering hinges on.</summary>
    private async Task<(string Branch, string Container, string Hub)> SeedStatsGraphAsync()
    {
        var branch = NewBranch();
        const string container = "NamedType:Stats.Container";
        const string hub = "Method:Stats.Hub()";
        var members = Enumerable.Range(1, 4).Select(i => $"Method:Stats.Container.M{i}()").ToArray();
        var callers = Enumerable.Range(1, 3).Select(i => $"Method:Stats.C{i}()").ToArray();

        var nodes = new List<NodeRow>
        {
            new(branch, container, "Container", SymbolKinds.NamedType, "Container.cs", "Asm", false),
            new(branch, hub, "Hub()", SymbolKinds.Method, "Hub.cs", "Asm", false),
        };
        nodes.AddRange(members.Select(m => new NodeRow(branch, m, "M()", SymbolKinds.Method, "Container.cs", "Asm", false)));
        nodes.AddRange(callers.Select(c => new NodeRow(branch, c, "C()", SymbolKinds.Method, "Callers.cs", "Asm", false)));

        var edges = new List<EdgeRow>();
        edges.AddRange(members.Select(m => new EdgeRow(branch, container, m, EdgeTypes.Contains, "Container.cs")));
        edges.AddRange(callers.Select(c => new EdgeRow(branch, c, hub, EdgeTypes.Calls, "Callers.cs")));

        await _fx.Store.Writer.UpsertNodesAsync(nodes);
        await _fx.Store.Writer.UpsertEdgesAsync(edges);
        return (branch, container, hub);
    }

    [Fact]
    public async Task GetStats_TotalsAndByKindAndByType_MatchTheSeededGraph()
    {
        var s = await SeedStatsGraphAsync();

        var stats = await _fx.Store.Reader.GetStatsAsync(s.Branch, topN: 10);

        Assert.Equal(s.Branch, stats.Branch);
        // Container + Hub + 4 members + 3 callers = 9 nodes; 4 CONTAINS + 3 CALLS = 7 edges.
        Assert.Equal(9, stats.TotalNodes);
        Assert.Equal(7, stats.TotalEdges);

        // Ordered count DESC, key ASC.
        Assert.Equal(
            new[] { new KindCount(SymbolKinds.Method, 8), new KindCount(SymbolKinds.NamedType, 1) },
            stats.NodesByKind.ToArray());
        Assert.Equal(
            new[] { new EdgeTypeCount(EdgeTypes.Contains, 4), new EdgeTypeCount(EdgeTypes.Calls, 3) },
            stats.EdgesByType.ToArray());
    }

    [Fact]
    public async Task GetStats_GodNodes_ExcludeContains_SoTheContainerIsNotNumberOne()
    {
        var s = await SeedStatsGraphAsync();

        var stats = await _fx.Store.Reader.GetStatsAsync(s.Branch, topN: 10);

        // Hub tops the list on its 3 incoming CALLS; the Container — which WOULD lead with
        // degree 4 if CONTAINS counted — is absent entirely (its only edges are CONTAINS).
        Assert.Equal(s.Hub, stats.GodNodes[0].Symbol.Id);
        Assert.Equal(3L, stats.GodNodes[0].Degree);
        Assert.DoesNotContain(stats.GodNodes, g => g.Symbol.Id == s.Container);
        // Only Hub and the three callers carry a non-CONTAINS edge.
        Assert.Equal(4, stats.GodNodes.Count);
        Assert.All(stats.GodNodes.Skip(1), g => Assert.Equal(1L, g.Degree));
    }

    [Fact]
    public async Task GetStats_Service_ClampsTopN_WithoutThrowing()
    {
        var s = await SeedStatsGraphAsync();
        var service = new EdgeHopQueryService(_fx.Store.Reader);

        // topN below 1 clamps up to 1 (not rejected): a single god node comes back.
        var clampedLow = await service.GetStatsAsync(s.Branch, topN: 0);
        Assert.Equal(s.Hub, Assert.Single(clampedLow.GodNodes).Symbol.Id);

        var clampedNegative = await service.GetStatsAsync(s.Branch, topN: -5);
        Assert.Single(clampedNegative.GodNodes);

        // A huge topN clamps down to MaxTopN and simply returns all god nodes that exist.
        var clampedHigh = await service.GetStatsAsync(s.Branch, topN: 1000);
        Assert.Equal(4, clampedHigh.GodNodes.Count);
    }
}

/// <summary>
/// The store-per-solution derivation of <see cref="SqliteSettings.FromEnvironment"/>:
/// explicit <c>EDGEHOP_SQLITE_PATH</c> &gt; repo of <c>EDGEHOP_REPO</c> &gt; repo of
/// the path hint &gt; shared default. Env-var mutating: each test sets exactly the
/// variables it needs and the fixture restores the originals; the shared named
/// collection serializes this class against <see cref="BranchResolverTests"/> (both
/// mutate the process-wide EDGEHOP_* vars — unserialized, xUnit's parallel classes
/// race). Pure path computation — no store file is created.
/// </summary>
[Collection("edgehop-env-vars")]
public sealed class SqliteSettingsDerivationTests : IDisposable
{
    private readonly string? _savedPath = Environment.GetEnvironmentVariable("EDGEHOP_SQLITE_PATH");
    private readonly string? _savedRepo = Environment.GetEnvironmentVariable(BranchResolver.RepoEnvVar);
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), $"edgehop-settings-{Guid.NewGuid():N}");

    public SqliteSettingsDerivationTests()
    {
        Environment.SetEnvironmentVariable("EDGEHOP_SQLITE_PATH", null);
        Environment.SetEnvironmentVariable(BranchResolver.RepoEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("EDGEHOP_SQLITE_PATH", _savedPath);
        Environment.SetEnvironmentVariable(BranchResolver.RepoEnvVar, _savedRepo);
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    /// <summary>A directory that looks like a git working-tree root (.git\HEAD on a
    /// branch), the same fabrication the branch-detector tests use — no git.exe.</summary>
    private string MakeRepo(string name)
    {
        var repo = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/main\n");
        return repo;
    }

    [Fact]
    public void Explicit_path_wins_over_everything()
    {
        var repo = MakeRepo("r-explicit");
        Environment.SetEnvironmentVariable(BranchResolver.RepoEnvVar, repo);
        Environment.SetEnvironmentVariable("EDGEHOP_SQLITE_PATH", @"X:\custom\store.db");

        Assert.Equal(@"X:\custom\store.db", SqliteSettings.FromEnvironment(repo).DatabasePath);
    }

    [Fact]
    public void Repo_env_derives_a_per_repo_store_and_beats_the_path_hint()
    {
        var repoA = MakeRepo("repo-a");
        var repoB = MakeRepo("repo-b");
        Environment.SetEnvironmentVariable(BranchResolver.RepoEnvVar, repoA);

        var path = SqliteSettings.FromEnvironment(pathHint: repoB).DatabasePath;

        Assert.Contains(Path.Combine("EdgeHop", "stores"), path);
        Assert.Contains("repo-a-", Path.GetFileName(path));
        Assert.EndsWith(".db", path, StringComparison.Ordinal);
    }

    [Fact]
    public void Path_hint_anywhere_inside_a_repo_derives_the_same_store()
    {
        var repo = MakeRepo("repo-hint");
        var nested = Path.Combine(repo, "src", "Deep", "Deeper");
        Directory.CreateDirectory(nested);

        var fromRoot = SqliteSettings.FromEnvironment(repo).DatabasePath;
        var fromNested = SqliteSettings.FromEnvironment(nested).DatabasePath;

        Assert.Equal(fromRoot, fromNested);
        Assert.Contains("repo-hint-", Path.GetFileName(fromRoot));
    }

    [Fact]
    public void Distinct_repos_derive_distinct_stores_deterministically()
    {
        var repoA = MakeRepo("solution-one");
        var repoB = MakeRepo("solution-two");

        var a1 = SqliteSettings.FromEnvironment(repoA).DatabasePath;
        var a2 = SqliteSettings.FromEnvironment(repoA).DatabasePath;
        var b = SqliteSettings.FromEnvironment(repoB).DatabasePath;

        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
    }

    [Fact]
    public void No_repo_found_falls_back_to_the_shared_default()
    {
        var bare = Path.Combine(_tempRoot, "not-a-repo");
        Directory.CreateDirectory(bare);

        Assert.Equal(SqliteSettings.DefaultDatabasePath, SqliteSettings.FromEnvironment(bare).DatabasePath);
        Assert.Equal(SqliteSettings.DefaultDatabasePath, SqliteSettings.FromEnvironment(null).DatabasePath);
    }

    /// <summary>A linked-worktree checkout of <paramref name="mainRepo"/>: a root whose
    /// <c>.git</c> is a FILE pointing at <c>&lt;main&gt;/.git/worktrees/&lt;name&gt;</c>,
    /// which carries the <c>commondir</c> back-pointer — exactly the layout
    /// <c>git worktree add</c> produces (fabricated by hand; no git.exe).</summary>
    private string MakeWorktree(string mainRepo, string name)
    {
        var gitDir = Path.Combine(mainRepo, ".git", "worktrees", name);
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/other\n");
        File.WriteAllText(Path.Combine(gitDir, "commondir"), "../..\n");

        var worktree = Path.Combine(_tempRoot, $"wt-{name}");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {gitDir}\n");
        return worktree;
    }

    [Fact]
    public void Worktree_hint_hashes_back_to_the_main_repos_store()
    {
        var main = MakeRepo("main-repo");
        var worktree = MakeWorktree(main, "feature");
        var nested = Path.Combine(worktree, "src", "Sub");
        Directory.CreateDirectory(nested);

        var mainStore = SqliteSettings.FromEnvironment(main).DatabasePath;

        // From the worktree root AND from deep inside it: the main repo's store.
        Assert.Equal(mainStore, SqliteSettings.FromEnvironment(worktree).DatabasePath);
        Assert.Equal(mainStore, SqliteSettings.FromEnvironment(nested).DatabasePath);
        Assert.Contains("main-repo-", Path.GetFileName(mainStore));
    }

    [Fact]
    public void Worktree_via_EDGEHOP_REPO_hashes_back_to_the_main_repos_store()
    {
        var main = MakeRepo("main-env");
        var worktree = MakeWorktree(main, "hotfix");
        Environment.SetEnvironmentVariable(BranchResolver.RepoEnvVar, worktree);

        Assert.Equal(
            SqliteSettings.FromEnvironment(main).DatabasePath,
            SqliteSettings.FromEnvironment(pathHint: null).DatabasePath);
    }

    [Fact]
    public void Submodule_layout_keeps_its_own_store()
    {
        // A submodule's .git-file gitdir (<super>/.git/modules/<name>) has NO commondir:
        // it is a distinct repository and must derive a distinct store.
        var super = MakeRepo("super-repo");
        var moduleGitDir = Path.Combine(super, ".git", "modules", "lib");
        Directory.CreateDirectory(moduleGitDir);
        File.WriteAllText(Path.Combine(moduleGitDir, "HEAD"), "ref: refs/heads/main\n");

        var submodule = Path.Combine(super, "lib");
        Directory.CreateDirectory(submodule);
        File.WriteAllText(Path.Combine(submodule, ".git"), $"gitdir: {moduleGitDir}\n");

        var superStore = SqliteSettings.FromEnvironment(super).DatabasePath;
        var submoduleStore = SqliteSettings.FromEnvironment(submodule).DatabasePath;

        Assert.NotEqual(superStore, submoduleStore);
        Assert.Contains("lib-", Path.GetFileName(submoduleStore));
    }
}
