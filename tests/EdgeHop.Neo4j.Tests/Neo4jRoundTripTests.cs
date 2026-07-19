using EdgeHop.Core;
using Neo4j.Driver;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// Phase 1 checkpoint 3: proves the idempotent upsert on the composite key
/// <c>(branch, id)</c> against a live Neo4j. MERGE-ing the same node twice (with changed
/// mutable properties the second time) must yield exactly one node carrying the updated
/// values, and MERGE-ing the same relationship twice must yield exactly one relationship.
/// Skipped automatically when NEO4J_* environment variables are not set.
/// </summary>
public sealed class Neo4jRoundTripTests
{
    /// <summary>Test-only branch value so nothing here can ever collide with real
    /// 'main' graph data.</summary>
    private const string Branch = "test-roundtrip";

    [Neo4jFact]
    public async Task Upsert_OnCompositeKey_IsIdempotent_ForNodesAndEdges()
    {
        var settings = Neo4jSettings.FromEnvironment();
        var driver = GraphDatabase.Driver(settings.Uri, AuthTokens.Basic(settings.User, settings.Password));
        await using (driver.ConfigureAwait(false))
        {
            // Schema first — the constraint IS the thing under test (MERGE keys off it).
            await Neo4jSchema.ApplyAsync(driver, settings.Database);

            // GUID-suffixed ids: unique per run, and cleanup can target exactly these nodes.
            var callerId = $"Method:EdgeHop.RoundTrip.Caller()_{Guid.NewGuid():N}";
            var calleeId = $"Method:EdgeHop.RoundTrip.Callee()_{Guid.NewGuid():N}";
            var writer = new Neo4jGraphWriter(driver, settings.Database);

            try
            {
                // --- Node round-trip -------------------------------------------------
                var caller = new NodeRow(
                    Branch, callerId, "Caller()", SymbolKinds.Method,
                    "RoundTrip/Fixture.cs", "EdgeHop.Tests", IsAbstract: false);
                var callee = new NodeRow(
                    Branch, calleeId, "Callee()", SymbolKinds.Method,
                    "RoundTrip/Fixture.cs", "EdgeHop.Tests", IsAbstract: false);

                // First upsert...
                await writer.UpsertNodesAsync(new[] { caller, callee });
                // ...second upsert of the SAME (branch, id) with a changed Name. Idempotent
                // MERGE must update in place, not create a duplicate.
                await writer.UpsertNodesAsync(new[] { caller with { Name = "Caller(updated)" }, callee });

                var callerNames = await ReadNodeNamesAsync(driver, settings.Database, callerId);
                Assert.Single(callerNames);
                Assert.Equal("Caller(updated)", callerNames[0]);

                var calleeNames = await ReadNodeNamesAsync(driver, settings.Database, calleeId);
                Assert.Single(calleeNames);

                // --- Edge round-trip -------------------------------------------------
                var edge = new EdgeRow(Branch, callerId, calleeId, EdgeTypes.Calls, "RoundTrip/Fixture.cs");
                await writer.UpsertEdgesAsync(new[] { edge });
                await writer.UpsertEdgesAsync(new[] { edge }); // MERGE again — still one relationship.

                var relationshipCount = await CountEdgesAsync(
                    driver, settings.Database, "CALLS", callerId, calleeId);
                Assert.Equal(1L, relationshipCount);
            }
            finally
            {
                // Targeted cleanup: ONLY the two nodes this run created (test branch AND the
                // exact GUID-suffixed ids), plus their relationships. This is the surgical,
                // single-node DETACH DELETE the project rules allow — never a bulk delete.
                await CleanupAsync(driver, settings.Database, new[] { callerId, calleeId });
            }
        }
    }

    [Neo4jFact]
    public async Task Component_Properties_And_Renders_Edge_RoundTrip()
    {
        var settings = Neo4jSettings.FromEnvironment();
        var driver = GraphDatabase.Driver(settings.Uri, AuthTokens.Basic(settings.User, settings.Password));
        await using (driver.ConfigureAwait(false))
        {
            await Neo4jSchema.ApplyAsync(driver, settings.Database);

            var pageId = $"NamedType:EdgeHop.RoundTrip.Page_{Guid.NewGuid():N}";
            var childId = $"NamedType:EdgeHop.RoundTrip.Child_{Guid.NewGuid():N}";
            var writer = new Neo4jGraphWriter(driver, settings.Database);

            try
            {
                var routes = new[] { "/page", "/page/{Id:int}" };
                var page = new NodeRow(
                    Branch, pageId, "Page", SymbolKinds.NamedType,
                    "Pages/Page.razor", "EdgeHop.Tests", IsAbstract: false,
                    IsComponent: true, Routes: routes);
                var child = new NodeRow(
                    Branch, childId, "Child", SymbolKinds.NamedType,
                    "Shared/Child.razor", "EdgeHop.Tests", IsAbstract: false,
                    IsComponent: true);

                // --- isComponent + routes round-trip ---------------------------------
                await writer.UpsertNodesAsync(new[] { page, child });

                var (isComponent, readRoutes) = await ReadComponentPropsAsync(driver, settings.Database, pageId);
                Assert.True(isComponent);
                Assert.Equal(routes, readRoutes);

                // Re-upsert with Routes = null: SET s.routes = null must REMOVE the
                // property (the correct upsert semantics when a @page is deleted).
                await writer.UpsertNodesAsync(new[] { page with { Routes = null } });
                (_, readRoutes) = await ReadComponentPropsAsync(driver, settings.Database, pageId);
                Assert.Null(readRoutes);

                // --- RENDERS edge round-trip -----------------------------------------
                var renders = new EdgeRow(Branch, pageId, childId, EdgeTypes.Renders, "Pages/Page.razor");
                await writer.UpsertEdgesAsync(new[] { renders });
                await writer.UpsertEdgesAsync(new[] { renders }); // MERGE again — still one.

                var count = await CountEdgesAsync(
                    driver, settings.Database, "RENDERS", pageId, childId);
                Assert.Equal(1L, count);
            }
            finally
            {
                await CleanupAsync(driver, settings.Database, new[] { pageId, childId });
            }
        }
    }

    private static async Task<(bool IsComponent, IReadOnlyList<string>? Routes)> ReadComponentPropsAsync(
        IDriver driver, string database, string id)
    {
        var session = driver.AsyncSession(o => o.WithDatabase(database));
        try
        {
            return await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync(
                    "MATCH (s:Symbol {branch: $branch, id: $id}) RETURN s.isComponent AS c, s.routes AS r",
                    new Dictionary<string, object> { ["branch"] = Branch, ["id"] = id });
                var record = await cursor.SingleAsync();
                var routes = record.Get<List<object>?>("r")?.Cast<string>().ToList();
                return (record.Get<bool>("c"), (IReadOnlyList<string>?)routes);
            });
        }
        finally
        {
            await session.CloseAsync();
        }
    }

    private static async Task<IReadOnlyList<string>> ReadNodeNamesAsync(
        IDriver driver, string database, string id)
    {
        var session = driver.AsyncSession(o => o.WithDatabase(database));
        try
        {
            return await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync(
                    "MATCH (s:Symbol {branch: $branch, id: $id}) RETURN s.name AS name",
                    new Dictionary<string, object> { ["branch"] = Branch, ["id"] = id });
                var records = await cursor.ToListAsync();
                return (IReadOnlyList<string>)records.Select(r => r.Get<string>("name")).ToList();
            });
        }
        finally
        {
            await session.CloseAsync();
        }
    }

    private static async Task<long> CountEdgesAsync(
        IDriver driver, string database, string edgeType, string fromId, string toId)
    {
        // Relationship types cannot be parameters; validate against the whitelist
        // before splicing, mirroring the production writer's rule.
        Assert.True(EdgeTypes.IsValid(edgeType), $"'{edgeType}' is not a whitelisted edge type.");

        var session = driver.AsyncSession(o => o.WithDatabase(database));
        try
        {
            return await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync(
                    $$"""
                    MATCH (a:Symbol {branch: $branch, id: $fromId})
                          -[r:{{edgeType}} {branch: $branch}]->
                          (b:Symbol {branch: $branch, id: $toId})
                    RETURN count(r) AS c
                    """,
                    new Dictionary<string, object>
                    {
                        ["branch"] = Branch,
                        ["fromId"] = fromId,
                        ["toId"] = toId,
                    });
                var record = await cursor.SingleAsync();
                return record.Get<long>("c");
            });
        }
        finally
        {
            await session.CloseAsync();
        }
    }

    private static async Task CleanupAsync(IDriver driver, string database, IReadOnlyList<string> ids)
    {
        var session = driver.AsyncSession(o => o.WithDatabase(database));
        try
        {
            await session.ExecuteWriteAsync(async tx =>
            {
                var cursor = await tx.RunAsync(
                    "MATCH (s:Symbol {branch: $branch}) WHERE s.id IN $ids DETACH DELETE s",
                    new Dictionary<string, object> { ["branch"] = Branch, ["ids"] = ids.ToList() });
                return await cursor.ConsumeAsync();
            });
        }
        finally
        {
            await session.CloseAsync();
        }
    }
}
