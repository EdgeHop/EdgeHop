namespace EdgeHop.Core;

/// <summary>
/// One symbol returned by a read-side graph query: the stable id, display name, kind and
/// declaring document of a <c>:Symbol</c> node, plus the two component-graph facets
/// (<paramref name="IsComponent"/> / <paramref name="Routes"/>). This is the shared result
/// shape for the read-side MCP tools (<c>find_symbol</c> / <c>get_callers</c> /
/// <c>get_relationships</c> / <c>get_path</c> / <c>graph_stats</c>), so it lives in Core
/// alongside the <see cref="IGraphReader"/> contract — not in any one backend.
/// </summary>
/// <param name="Id">Stable symbol ID (see <c>SymbolIdFormat</c>); composite key with branch.</param>
/// <param name="Name">Short display name of the symbol.</param>
/// <param name="Kind">One of <see cref="SymbolKinds"/>.</param>
/// <param name="SourceDoc">Relative path of the declaring document; may be null (namespace/metadata symbols).</param>
/// <param name="IsComponent">True for Razor component types (see <see cref="NodeRow.IsComponent"/>).
/// Defaults to false so a store indexed before this facet existed still deserializes.</param>
/// <param name="Routes">Route templates from <c>@page</c>/<c>[Route]</c> directives, in
/// declaration order; null when the type has none. NOTE: a non-null <see cref="Routes"/>
/// makes record structural equality reference-based for this property — nothing compares
/// <see cref="SymbolHit"/> by value, but don't start.</param>
public sealed record SymbolHit(
    string Id,
    string Name,
    string Kind,
    string? SourceDoc,
    bool IsComponent = false,
    IReadOnlyList<string>? Routes = null);
