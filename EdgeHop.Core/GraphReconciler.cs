namespace EdgeHop.Core;

/// <summary>The four batches a reconcile run will apply; see <see cref="GraphReconciler.ComputePlan"/>.</summary>
/// <param name="NodesToUpsert">Every desired node (upserting all is what keeps the plan
/// independent of the node property list).</param>
/// <param name="EdgesToUpsert">Every desired edge.</param>
/// <param name="NodeIdsToDelete">Node ids present in the store but not in the desired set.</param>
/// <param name="EdgeKeysToDelete">Edge identities present in the store but not in the desired set.</param>
public sealed record ReconcilePlan(
    IReadOnlyCollection<NodeRow> NodesToUpsert,
    IReadOnlyCollection<EdgeRow> EdgesToUpsert,
    IReadOnlyCollection<string> NodeIdsToDelete,
    IReadOnlyCollection<EdgeKey> EdgeKeysToDelete);

/// <summary>What a reconcile run actually applied.</summary>
public sealed record ReconcileReport(
    int NodesUpserted, int EdgesUpserted, int NodesDeleted, int EdgesDeleted);

/// <summary>
/// Makes one branch of the graph exactly match a freshly extracted desired state:
/// upsert everything desired, then delete what exists but is no longer desired
/// (stale keys). This is the engine behind full builds, incremental re-index, and watch
/// cycles alike — "incremental" lives in the delete traffic, while correctness never
/// depends on knowing which files changed (renames and cross-file staleness are handled
/// by construction because the desired set is always extracted from the whole solution).
/// </summary>
public sealed class GraphReconciler
{
    private readonly IGraphWriter _writer;
    private readonly IGraphSnapshotReader _snapshot;

    public GraphReconciler(IGraphWriter writer, IGraphSnapshotReader snapshot)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(snapshot);
        _writer = writer;
        _snapshot = snapshot;
    }

    /// <summary>
    /// Pure set difference — unit-testable without a database. Upserts are always the
    /// full desired sets; deletes are <c>existing − desired</c> by node id / edge key.
    /// </summary>
    public static ReconcilePlan ComputePlan(
        IReadOnlyCollection<NodeRow> desiredNodes,
        IReadOnlyCollection<EdgeRow> desiredEdges,
        IReadOnlyCollection<string> existingNodeIds,
        IReadOnlyCollection<EdgeKey> existingEdgeKeys)
    {
        ArgumentNullException.ThrowIfNull(desiredNodes);
        ArgumentNullException.ThrowIfNull(desiredEdges);
        ArgumentNullException.ThrowIfNull(existingNodeIds);
        ArgumentNullException.ThrowIfNull(existingEdgeKeys);

        var desiredIds = desiredNodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var desiredKeys = desiredEdges
            .Select(e => new EdgeKey(e.Type, e.FromId, e.ToId))
            .ToHashSet();

        var staleIds = existingNodeIds
            .Where(id => !desiredIds.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var staleKeys = existingEdgeKeys
            .Where(k => !desiredKeys.Contains(k))
            .Distinct()
            .ToList();

        return new ReconcilePlan(desiredNodes, desiredEdges, staleIds, staleKeys);
    }

    /// <summary>
    /// Snapshot → plan → apply, in this order: upsert nodes, upsert edges, delete stale
    /// edges, delete stale nodes last (its <c>DETACH</c> sweeps anything the edge pass
    /// missed, and no desired edge ever dangles). Chunked, not one transaction; readers
    /// can observe a transiently mixed graph for the seconds a run takes.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="desiredNodes"/> is empty while the branch still holds data and
    /// <paramref name="allowEmpty"/> is false — the guard that stops a bad or partial
    /// extraction from mass-deleting a branch. Pass <paramref name="allowEmpty"/> (wired
    /// to an explicit CLI flag) to empty a branch on purpose.
    /// </exception>
    public async Task<ReconcileReport> ReconcileAsync(
        string branch,
        IReadOnlyCollection<NodeRow> desiredNodes,
        IReadOnlyCollection<EdgeRow> desiredEdges,
        bool allowEmpty = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentNullException.ThrowIfNull(desiredNodes);
        ArgumentNullException.ThrowIfNull(desiredEdges);

        var existingIds = await _snapshot.GetNodeIdsAsync(branch, cancellationToken).ConfigureAwait(false);
        var existingKeys = await _snapshot.GetEdgeKeysAsync(
            branch, onUnknownType: null, cancellationToken).ConfigureAwait(false);

        if (desiredNodes.Count == 0 && existingIds.Count > 0 && !allowEmpty)
        {
            throw new InvalidOperationException(
                $"Refusing to reconcile branch '{branch}': the desired graph is empty but "
                + $"the branch holds {existingIds.Count} nodes. A failed or partial "
                + "extraction must not empty a branch; pass allow-empty to do this on purpose.");
        }

        var plan = ComputePlan(desiredNodes, desiredEdges, existingIds, existingKeys);

        await _writer.UpsertNodesAsync(plan.NodesToUpsert).ConfigureAwait(false);
        await _writer.UpsertEdgesAsync(plan.EdgesToUpsert).ConfigureAwait(false);
        await _writer.DeleteEdgesAsync(branch, plan.EdgeKeysToDelete).ConfigureAwait(false);
        await _writer.DeleteNodesAsync(branch, plan.NodeIdsToDelete).ConfigureAwait(false);

        return new ReconcileReport(
            plan.NodesToUpsert.Count,
            plan.EdgesToUpsert.Count,
            plan.NodeIdsToDelete.Count,
            plan.EdgeKeysToDelete.Count);
    }
}
