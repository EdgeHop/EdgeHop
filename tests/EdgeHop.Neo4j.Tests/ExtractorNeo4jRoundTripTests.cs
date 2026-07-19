using EdgeHop.Core;
using Neo4j.Driver;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// Phase 6 checkpoint — the real C# extraction round-tripped through the Neo4j reconcile
/// engine. Shares the in-process <see cref="MsBuildFixture"/> (the same TinyFixture
/// extraction the general extractor tests use, linked from EdgeHop.Tests) so the graph
/// under test is the genuine extractor output, not a hand-built stand-in. Seeds and prunes
/// only a throwaway GUID branch; skipped automatically when NEO4J_* is not configured.
/// </summary>
[Collection(MsBuildTestCollection.Name)]
public sealed class ExtractorNeo4jRoundTripTests
{
    private readonly MsBuildFixture _fx;

    public ExtractorNeo4jRoundTripTests(MsBuildFixture fx) => _fx = fx;

    [Neo4jFact]
    public async Task Real_extraction_reconciles_and_a_deleted_file_is_surgically_pruned()
    {
        var settings = Neo4jSettings.FromEnvironment();
        var driver = GraphDatabase.Driver(
            settings.Uri, AuthTokens.Basic(settings.User, settings.Password));
        await using (driver.ConfigureAwait(false))
        {
            await Neo4jSchema.ApplyAsync(driver, settings.Database);
            var writer = new Neo4jGraphWriter(driver, settings.Database);
            var snapshot = new Neo4jGraphSnapshotReader(driver, settings.Database);
            var reconciler = new GraphReconciler(writer, snapshot);

            var branch = $"test-extrecon-{Guid.NewGuid():N}";
            try
            {
                // Re-stamp the real TinyFixture extraction onto a throwaway branch.
                var nodes = _fx.Extraction.Nodes.Select(n => n with { Branch = branch }).ToList();
                var edges = _fx.Extraction.Edges.Select(e => e with { Branch = branch }).ToList();
                await reconciler.ReconcileAsync(branch, nodes, edges);
                Assert.Equal(19, (await snapshot.GetNodeIdsAsync(branch)).Count);
                Assert.Equal(28, (await snapshot.GetEdgeKeysAsync(branch)).Count);

                // Simulate deleting Sub/Decorator.cs: a fresh extraction would contain no
                // symbols declared there and no edges touching them.
                var goneIds = nodes
                    .Where(n => n.SourceDoc == "Sub/Decorator.cs")
                    .Select(n => n.Id)
                    .ToHashSet(StringComparer.Ordinal);
                Assert.NotEmpty(goneIds);
                var remainingNodes = nodes.Where(n => !goneIds.Contains(n.Id)).ToList();
                var remainingEdges = edges
                    .Where(e => !goneIds.Contains(e.FromId) && !goneIds.Contains(e.ToId))
                    .ToList();

                var report = await reconciler.ReconcileAsync(branch, remainingNodes, remainingEdges);
                Assert.Equal(goneIds.Count, report.NodesDeleted);

                var idsAfter = (await snapshot.GetNodeIdsAsync(branch)).ToHashSet(StringComparer.Ordinal);
                Assert.Empty(idsAfter.Intersect(goneIds));
                Assert.Equal(remainingNodes.Count, idsAfter.Count);
                var keysAfter = await snapshot.GetEdgeKeysAsync(branch);
                Assert.DoesNotContain(keysAfter, k => goneIds.Contains(k.FromId) || goneIds.Contains(k.ToId));
                Assert.Equal(remainingEdges.Count, keysAfter.Count);
            }
            finally
            {
                await writer.DeleteBranchAsync(branch);
            }
        }
    }
}
