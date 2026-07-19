using EdgeHop.Core;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// Phase 5 checkpoint — the reconcile engine. These pure
/// <see cref="GraphReconciler.ComputePlan"/> tests need no database; the live,
/// backend-specific reconcile tests (which seed unique GUID branches and prove the
/// project's signature property that a reconcile on one branch can never touch another)
/// live beside their store — see <c>GraphReconcilerNeo4jTests</c> in
/// <c>EdgeHop.Neo4j.Tests</c> and <c>SqliteStoreConformanceTests</c>.
/// </summary>
public sealed class GraphReconcilerTests
{
    // ---------------------------------------------------------------------------
    // Pure ComputePlan tests (no database)
    // ---------------------------------------------------------------------------

    private static NodeRow Node(string id) =>
        new("b", id, id, SymbolKinds.Method, "Doc.cs", "Asm", IsAbstract: false);

    private static EdgeRow Edge(string from, string to, string type = EdgeTypes.Calls) =>
        new("b", from, to, type, "Doc.cs");

    [Fact]
    public void ComputePlan_deletes_exactly_the_stale_keys()
    {
        var desiredNodes = new[] { Node("A"), Node("B") };
        var desiredEdges = new[] { Edge("A", "B") };
        var existingIds = new[] { "A", "B", "GONE" };
        var existingKeys = new[]
        {
            new EdgeKey(EdgeTypes.Calls, "A", "B"),
            new EdgeKey(EdgeTypes.Calls, "A", "GONE"),
            new EdgeKey(EdgeTypes.References, "A", "B"), // same endpoints, other type: stale
        };

        var plan = GraphReconciler.ComputePlan(desiredNodes, desiredEdges, existingIds, existingKeys);

        Assert.Equal(desiredNodes, plan.NodesToUpsert);
        Assert.Equal(desiredEdges, plan.EdgesToUpsert);
        Assert.Equal(new[] { "GONE" }, plan.NodeIdsToDelete);
        Assert.Equal(
            new HashSet<EdgeKey>
            {
                new(EdgeTypes.Calls, "A", "GONE"),
                new(EdgeTypes.References, "A", "B"),
            },
            plan.EdgeKeysToDelete.ToHashSet());
    }

    [Fact]
    public void ComputePlan_with_identical_sets_deletes_nothing()
    {
        var nodes = new[] { Node("A"), Node("B") };
        var edges = new[] { Edge("A", "B") };

        var plan = GraphReconciler.ComputePlan(
            nodes, edges,
            nodes.Select(n => n.Id).ToList(),
            edges.Select(e => new EdgeKey(e.Type, e.FromId, e.ToId)).ToList());

        Assert.Empty(plan.NodeIdsToDelete);
        Assert.Empty(plan.EdgeKeysToDelete);
    }

    [Fact]
    public void ComputePlan_on_empty_store_only_upserts()
    {
        var plan = GraphReconciler.ComputePlan(
            new[] { Node("A") }, new[] { Edge("A", "A") },
            Array.Empty<string>(), Array.Empty<EdgeKey>());

        Assert.Single(plan.NodesToUpsert);
        Assert.Empty(plan.NodeIdsToDelete);
        Assert.Empty(plan.EdgeKeysToDelete);
    }
}
