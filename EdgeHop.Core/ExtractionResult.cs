namespace EdgeHop.Core;

/// <summary>
/// The complete set of graph rows extracted from one source tier (one <see cref="IExtractor"/>
/// run): nodes deduped by <c>(Branch, Id)</c> and edges deduped by
/// <c>(Branch, FromId, ToId, Type)</c>, with every edge guaranteed to connect two emitted
/// nodes. Multiple extractors' results are merged into one set and reconciled once per branch
/// (the reconciler diffs the WHOLE branch, so a per-extractor reconcile would mutually prune).
/// </summary>
/// <param name="Nodes">All emitted <c>:Symbol</c> node rows.</param>
/// <param name="Edges">All emitted relationship rows; both endpoints exist in <paramref name="Nodes"/>.</param>
/// <param name="Interop">Cross-tier JS-interop surface this extractor contributes (C# call sites
/// and/or JS exports) — used by the host to derive <c>JS_CALLS</c> edges after every extractor
/// runs. <c>null</c> when the extractor has no interop half to offer (treated as empty).</param>
public sealed record ExtractionResult(
    IReadOnlyList<NodeRow> Nodes,
    IReadOnlyList<EdgeRow> Edges,
    InteropSurface? Interop = null);
