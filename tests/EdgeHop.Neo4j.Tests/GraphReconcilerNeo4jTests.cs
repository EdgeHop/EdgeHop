using EdgeHop.Core;
using Neo4j.Driver;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// Phase 5 checkpoint — the live Neo4j reconcile tests. These seed unique GUID branches
/// and prove the project's signature property: a reconcile on one branch can never touch
/// another. The pure <see cref="GraphReconciler.ComputePlan"/> tests that need no database
/// live in <c>GraphReconcilerTests</c> in <c>EdgeHop.Tests</c>. Skipped automatically
/// when NEO4J_* environment variables are not set.
/// </summary>
public sealed class GraphReconcilerNeo4jTests
{
    private static (string TypeId, string M1Id, string M2Id) SeedIds() =>
        ($"NamedType:Recon.Holder_{Guid.NewGuid():N}",
         $"Method:Recon.M1()_{Guid.NewGuid():N}",
         $"Method:Recon.M2()_{Guid.NewGuid():N}");

    private static (IReadOnlyList<NodeRow> Nodes, IReadOnlyList<EdgeRow> Edges) BuildGraph(
        string branch, string typeId, string m1Id, string m2Id)
    {
        var nodes = new List<NodeRow>
        {
            new(branch, typeId, "Holder", SymbolKinds.NamedType, "Recon/Holder.cs", "T", false),
            new(branch, m1Id, "M1()", SymbolKinds.Method, "Recon/Holder.cs", "T", false),
            new(branch, m2Id, "M2()", SymbolKinds.Method, "Recon/Holder.cs", "T", false),
        };
        var edges = new List<EdgeRow>
        {
            new(branch, typeId, m1Id, EdgeTypes.Contains, "Recon/Holder.cs"),
            new(branch, typeId, m2Id, EdgeTypes.Contains, "Recon/Holder.cs"),
            new(branch, m1Id, m2Id, EdgeTypes.Calls, "Recon/Holder.cs"),
        };
        return (nodes, edges);
    }

    [Neo4jFact]
    public async Task Reconcile_removes_stale_node_and_edges_but_never_touches_sibling_branch()
    {
        var settings = Neo4jSettings.FromEnvironment();
        var driver = GraphDatabase.Driver(settings.Uri, AuthTokens.Basic(settings.User, settings.Password));
        await using (driver.ConfigureAwait(false))
        {
            await Neo4jSchema.ApplyAsync(driver, settings.Database);
            var writer = new Neo4jGraphWriter(driver, settings.Database);
            var snapshot = new Neo4jGraphSnapshotReader(driver, settings.Database);
            var reconciler = new GraphReconciler(writer, snapshot);

            var branchA = $"test-recon-{Guid.NewGuid():N}";
            var branchB = $"test-recon-{Guid.NewGuid():N}";
            var (typeId, m1Id, m2Id) = SeedIds();

            try
            {
                // Identical seed on both branches — SAME ids, different branch values.
                var (nodesA, edgesA) = BuildGraph(branchA, typeId, m1Id, m2Id);
                var (nodesB, edgesB) = BuildGraph(branchB, typeId, m1Id, m2Id);
                await writer.UpsertNodesAsync(nodesA.Concat(nodesB).ToList());
                await writer.UpsertEdgesAsync(edgesA.Concat(edgesB).ToList());

                // Desired state on A: M2 (and everything touching it) is gone.
                var desiredNodes = nodesA.Where(n => n.Id != m2Id).ToList();
                var desiredEdges = edgesA.Where(e => e.FromId != m2Id && e.ToId != m2Id).ToList();

                var report = await reconciler.ReconcileAsync(branchA, desiredNodes, desiredEdges);

                Assert.Equal(1, report.NodesDeleted);
                Assert.Equal(2, report.EdgesDeleted); // CONTAINS type→M2 and CALLS M1→M2

                var idsA = await snapshot.GetNodeIdsAsync(branchA);
                Assert.Equal(
                    new HashSet<string> { typeId, m1Id },
                    idsA.ToHashSet(StringComparer.Ordinal));
                var keysA = await snapshot.GetEdgeKeysAsync(branchA);
                Assert.Equal(
                    new HashSet<EdgeKey> { new(EdgeTypes.Contains, typeId, m1Id) },
                    keysA.ToHashSet());

                // Branch B: byte-for-byte untouched.
                var idsB = await snapshot.GetNodeIdsAsync(branchB);
                Assert.Equal(
                    new HashSet<string> { typeId, m1Id, m2Id },
                    idsB.ToHashSet(StringComparer.Ordinal));
                var keysB = await snapshot.GetEdgeKeysAsync(branchB);
                Assert.Equal(3, keysB.Count);
            }
            finally
            {
                await writer.DeleteBranchAsync(branchA);
                await writer.DeleteBranchAsync(branchB);
            }
        }
    }

    [Neo4jFact]
    public async Task Reconcile_repoints_callers_when_a_node_id_changes()
    {
        var settings = Neo4jSettings.FromEnvironment();
        var driver = GraphDatabase.Driver(settings.Uri, AuthTokens.Basic(settings.User, settings.Password));
        await using (driver.ConfigureAwait(false))
        {
            await Neo4jSchema.ApplyAsync(driver, settings.Database);
            var writer = new Neo4jGraphWriter(driver, settings.Database);
            var snapshot = new Neo4jGraphSnapshotReader(driver, settings.Database);
            var reconciler = new GraphReconciler(writer, snapshot);

            var branch = $"test-recon-{Guid.NewGuid():N}";
            var (typeId, m1Id, m2Id) = SeedIds();

            try
            {
                var (nodes, edges) = BuildGraph(branch, typeId, m1Id, m2Id);
                await reconciler.ReconcileAsync(branch, nodes, edges);

                // "Rename" M2: its id changes, and M1's CALLS edge follows — exactly what
                // a fresh whole-solution extraction produces after a signature change.
                var renamedId = $"{m2Id}_renamed";
                var renamedNodes = nodes
                    .Select(n => n.Id == m2Id ? n with { Id = renamedId, Name = "M2(int)" } : n)
                    .ToList();
                var renamedEdges = edges
                    .Select(e => e.ToId == m2Id ? e with { ToId = renamedId } : e)
                    .ToList();

                var report = await reconciler.ReconcileAsync(branch, renamedNodes, renamedEdges);
                Assert.Equal(1, report.NodesDeleted); // the old id

                var keys = await snapshot.GetEdgeKeysAsync(branch);
                Assert.Contains(new EdgeKey(EdgeTypes.Calls, m1Id, renamedId), keys);
                Assert.DoesNotContain(keys, k => k.FromId == m2Id || k.ToId == m2Id);
            }
            finally
            {
                await writer.DeleteBranchAsync(branch);
            }
        }
    }

    [Neo4jFact]
    public async Task Reconcile_is_idempotent_and_guards_against_empty_desired_sets()
    {
        var settings = Neo4jSettings.FromEnvironment();
        var driver = GraphDatabase.Driver(settings.Uri, AuthTokens.Basic(settings.User, settings.Password));
        await using (driver.ConfigureAwait(false))
        {
            await Neo4jSchema.ApplyAsync(driver, settings.Database);
            var writer = new Neo4jGraphWriter(driver, settings.Database);
            var snapshot = new Neo4jGraphSnapshotReader(driver, settings.Database);
            var reconciler = new GraphReconciler(writer, snapshot);

            var branch = $"test-recon-{Guid.NewGuid():N}";
            var (typeId, m1Id, m2Id) = SeedIds();

            try
            {
                var (nodes, edges) = BuildGraph(branch, typeId, m1Id, m2Id);
                await reconciler.ReconcileAsync(branch, nodes, edges);

                // Idempotence: an immediate second run deletes nothing.
                var second = await reconciler.ReconcileAsync(branch, nodes, edges);
                Assert.Equal(0, second.NodesDeleted);
                Assert.Equal(0, second.EdgesDeleted);

                // Guard: an empty desired set against a non-empty branch throws...
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    reconciler.ReconcileAsync(branch, Array.Empty<NodeRow>(), Array.Empty<EdgeRow>()));
                Assert.Contains("allow-empty", ex.Message, StringComparison.Ordinal);

                // ...and nothing was deleted by the refused run.
                Assert.Equal(3, (await snapshot.GetNodeIdsAsync(branch)).Count);

                // allowEmpty: true empties the branch on purpose.
                var emptied = await reconciler.ReconcileAsync(
                    branch, Array.Empty<NodeRow>(), Array.Empty<EdgeRow>(), allowEmpty: true);
                Assert.Equal(3, emptied.NodesDeleted);
                Assert.Empty(await snapshot.GetNodeIdsAsync(branch));
            }
            finally
            {
                await writer.DeleteBranchAsync(branch);
            }
        }
    }

    [Neo4jFact]
    public async Task Snapshot_reader_filters_unknown_edge_types_out_of_delete_plans()
    {
        var settings = Neo4jSettings.FromEnvironment();
        var driver = GraphDatabase.Driver(settings.Uri, AuthTokens.Basic(settings.User, settings.Password));
        await using (driver.ConfigureAwait(false))
        {
            await Neo4jSchema.ApplyAsync(driver, settings.Database);
            var writer = new Neo4jGraphWriter(driver, settings.Database);
            var snapshot = new Neo4jGraphSnapshotReader(driver, settings.Database);

            var branch = $"test-recon-{Guid.NewGuid():N}";
            var (typeId, m1Id, m2Id) = SeedIds();

            try
            {
                var (nodes, edges) = BuildGraph(branch, typeId, m1Id, m2Id);
                await writer.UpsertNodesAsync(nodes);
                await writer.UpsertEdgesAsync(edges);

                // Plant a relationship type this build does not know (raw Cypher — the
                // writer itself would reject it), simulating a newer schema revision.
                var session = driver.AsyncSession(o => o.WithDatabase(settings.Database));
                try
                {
                    await session.ExecuteWriteAsync(async tx =>
                    {
                        var cursor = await tx.RunAsync(
                            """
                            MATCH (a:Symbol {branch: $branch, id: $m1}), (b:Symbol {branch: $branch, id: $m2})
                            MERGE (a)-[r:FUTURE_TYPE {branch: $branch}]->(b)
                            """,
                            new Dictionary<string, object>
                            {
                                ["branch"] = branch,
                                ["m1"] = m1Id,
                                ["m2"] = m2Id,
                            });
                        return await cursor.ConsumeAsync();
                    });
                }
                finally
                {
                    await session.CloseAsync();
                }

                var unknown = new List<string>();
                var keys = await snapshot.GetEdgeKeysAsync(branch, unknown.Add);

                Assert.Equal(new[] { "FUTURE_TYPE" }, unknown);
                Assert.DoesNotContain(keys, k => k.Type == "FUTURE_TYPE");
                Assert.Equal(3, keys.Count); // the three known edges only

                // And DeleteEdgesAsync rejects unknown types outright, whole-batch.
                await Assert.ThrowsAsync<ArgumentException>(() => writer.DeleteEdgesAsync(
                    branch, new[] { new EdgeKey("FUTURE_TYPE", m1Id, m2Id) }));
            }
            finally
            {
                await writer.DeleteBranchAsync(branch);
            }
        }
    }
}
