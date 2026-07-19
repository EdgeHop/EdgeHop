namespace EdgeHop.Core;

/// <summary>
/// One <c>:Symbol</c> node row, keyed by the composite identity <c>(branch, id)</c>.
/// Property names map 1:1 onto the Neo4j node properties defined in the README schema.
/// </summary>
/// <param name="Branch">Branch this node belongs to. <c>"main"</c> in phases 1–3; part of the composite key.</param>
/// <param name="Id">Stable symbol ID (see <c>SymbolIdFormat</c>). Composite key with <paramref name="Branch"/>.</param>
/// <param name="Name">Short display name of the symbol.</param>
/// <param name="Kind">One of <see cref="SymbolKinds"/>.</param>
/// <param name="SourceDoc">Relative path of the declaring document; null for metadata symbols.
/// For Razor components (and their <c>@code</c> members) this is the authored <c>.razor</c>
/// document, not the generated <c>*_razor.g.cs</c> file.</param>
/// <param name="Assembly">Containing assembly name.</param>
/// <param name="IsAbstract">Whether the symbol is abstract (convenient for impact queries).</param>
/// <param name="IsComponent">True for NamedTypes that inherit (directly or transitively)
/// from <c>Microsoft.AspNetCore.Components.ComponentBase</c>.</param>
/// <param name="Routes">Route templates from <c>@page</c> directives (<c>[Route]</c>
/// attributes), in declaration order; null when the type has none. NOTE: a non-null
/// <see cref="Routes"/> makes record structural equality reference-based for this
/// property — nothing relies on <see cref="NodeRow"/> value equality (dedupe is by ID),
/// but don't start.</param>
public sealed record NodeRow(
    string Branch,
    string Id,
    string Name,
    string Kind,
    string? SourceDoc,
    string Assembly,
    bool IsAbstract,
    bool IsComponent = false,
    IReadOnlyList<string>? Routes = null);

/// <summary>
/// One relationship row between two <c>:Symbol</c> nodes identified by <c>(Branch, FromId)</c>
/// and <c>(Branch, ToId)</c>. <see cref="Type"/> must be one of <see cref="EdgeTypes"/>.
/// </summary>
/// <param name="Branch">Branch the edge belongs to; carried on the relationship as well.</param>
/// <param name="FromId">Stable ID of the source-side symbol.</param>
/// <param name="ToId">Stable ID of the target-side symbol.</param>
/// <param name="Type">Relationship type — one of the <see cref="EdgeTypes"/> constants.</param>
/// <param name="SourceDoc">The <c>sourceDoc</c> of the source-side declaration that produced the edge.</param>
public sealed record EdgeRow(
    string Branch,
    string FromId,
    string ToId,
    string Type,
    string? SourceDoc);

/// <summary>
/// The identity of one relationship in the graph: <c>(Type, FromId, ToId)</c> within a
/// branch — exactly what the writer MERGEs on. Used by snapshot reads and the reconciler.
/// </summary>
public readonly record struct EdgeKey(string Type, string FromId, string ToId);

/// <summary>
/// The closed set of relationship types. Keep this set small — every addition must be
/// justified by a query it enables (see README).
/// </summary>
public static class EdgeTypes
{
    /// <summary>Container → member (namespace→type, type→method/property/field).</summary>
    public const string Contains = "CONTAINS";

    /// <summary>Method → Method invocation, resolved via the semantic model.</summary>
    public const string Calls = "CALLS";

    /// <summary>NamedType → NamedType: class/struct implements interface.</summary>
    public const string Implements = "IMPLEMENTS";

    /// <summary>NamedType → NamedType: derived → base class.</summary>
    public const string Inherits = "INHERITS";

    /// <summary>Symbol → NamedType: uses a type (parameter/return/field type etc.).</summary>
    public const string References = "REFERENCES";

    /// <summary>Method → Method: override → overridden.</summary>
    public const string Overrides = "OVERRIDES";

    /// <summary>NamedType → NamedType: Razor component statically renders a child component
    /// in its markup. Source-declared targets only (framework/library components such as
    /// MudBlazor get no edge). Routable pages are reached at runtime via their
    /// <c>routes</c> node property, not via RENDERS — the Router dispatches dynamically.</summary>
    public const string Renders = "RENDERS";

    /// <summary>Method → Method: a client method invokes an HTTP endpoint served by the
    /// target — an <c>HttpClient</c> call whose route template matches (verb + template
    /// shape) a route registered by the target method (minimal-API <c>Map*</c> call or
    /// attribute-routed controller action). Crosses the process boundary compile-time
    /// edges cannot (Web→ApiService); enables <c>get_callers</c> to answer "who calls
    /// this API" across tiers.</summary>
    public const string HttpCalls = "HTTP_CALLS";

    /// <summary>C# Method → JS Method/Field: a Blazor JS-interop call
    /// (<c>IJSRuntime</c>/<c>IJSObjectReference</c> <c>InvokeAsync</c>/<c>InvokeVoidAsync</c>
    /// with a constant identifier) invokes the JS symbol the oxc extractor exported under
    /// that name. Crosses the C#→JS tier boundary compile-time edges cannot; derived in the
    /// host by matching the Roslyn-collected call sites against the oxc-emitted interop
    /// exports (precise = module + name, or opt-in broad = name only). Lets
    /// <c>get_callers</c> answer "who calls this JS function" across tiers.</summary>
    public const string JsCalls = "JS_CALLS";

    /// <summary>JS Method → C# Method: the reverse Blazor interop direction — a JavaScript
    /// <c>DotNet.invokeMethod[Async]("Assembly", "Identifier", …)</c> (static) or
    /// <c>objRef.invokeMethod[Async]("Identifier", …)</c> (instance) invokes a C#
    /// <c>[JSInvokable]</c> method. Crosses the JS→C# tier boundary; derived in the host by
    /// matching the oxc-collected JS call sites against the Roslyn-collected invokable methods
    /// (precise = assembly+identifier for static / unique identifier for instance, or opt-in
    /// broad = identifier only). Lets <c>get_callers</c> answer "which JS invokes this C#
    /// method" across tiers. Distinct from <see cref="JsCalls"/>, which is the opposite
    /// direction.</summary>
    public const string JsInvokes = "JS_INVOKES";

    /// <summary>Every valid edge type. This whitelist is the only source of relationship
    /// type tokens ever spliced into Cypher text (a relationship type cannot be a query
    /// parameter, so it must be validated here first).</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Contains,
        Calls,
        Implements,
        Inherits,
        References,
        Overrides,
        Renders,
        HttpCalls,
        JsCalls,
        JsInvokes,
    };

    /// <summary>Returns true when <paramref name="type"/> is one of the known edge types
    /// (exact, case-sensitive match).</summary>
    public static bool IsValid(string? type) => type is not null && All.Contains(type);
}

/// <summary>
/// The closed set of values for the <c>kind</c> node property (phases 1–3).
/// </summary>
public static class SymbolKinds
{
    public const string NamedType = "NamedType";
    public const string Method = "Method";
    public const string Property = "Property";
    public const string Field = "Field";
    public const string Event = "Event";
    public const string Namespace = "Namespace";
}

/// <summary>
/// Direction a <c>get_relationships</c> traversal follows relative to the anchor symbol:
/// <see cref="Out"/> walks outgoing edges (anchor is the source), <see cref="In"/> walks
/// incoming edges (anchor is the target), <see cref="Both"/> walks either endpoint.
/// </summary>
public enum RelationshipDirection
{
    /// <summary>Follow outgoing edges — neighbors are the targets of edges from the anchor.</summary>
    Out,

    /// <summary>Follow incoming edges — neighbors are the sources of edges into the anchor.</summary>
    In,

    /// <summary>Follow edges in either direction — the union of <see cref="Out"/> and <see cref="In"/>.</summary>
    Both,
}

/// <summary>
/// Wire tokens for <see cref="RelationshipDirection"/> plus the parse/format helpers front
/// ends use to translate the <c>direction</c> argument. The wire form is always lowercase
/// <c>out</c>/<c>in</c>/<c>both</c>; parsing is case-insensitive.
/// </summary>
public static class RelationshipDirections
{
    /// <summary>Wire token for <see cref="RelationshipDirection.Out"/>.</summary>
    public const string Out = "out";

    /// <summary>Wire token for <see cref="RelationshipDirection.In"/>.</summary>
    public const string In = "in";

    /// <summary>Wire token for <see cref="RelationshipDirection.Both"/>.</summary>
    public const string Both = "both";

    /// <summary>Parses <paramref name="s"/> (case-insensitive <c>out</c>/<c>in</c>/<c>both</c>)
    /// into <paramref name="direction"/>. Returns false for null, whitespace, or any other
    /// value; front ends turn a false result into a usage error.</summary>
    public static bool TryParse(string? s, out RelationshipDirection direction)
    {
        switch (s?.Trim().ToLowerInvariant())
        {
            case Out:
                direction = RelationshipDirection.Out;
                return true;
            case In:
                direction = RelationshipDirection.In;
                return true;
            case Both:
                direction = RelationshipDirection.Both;
                return true;
            default:
                direction = RelationshipDirection.Out;
                return false;
        }
    }

    /// <summary>The wire token for <paramref name="direction"/> (<c>out</c>/<c>in</c>/<c>both</c>).</summary>
    public static string ToWire(RelationshipDirection direction) => direction switch
    {
        RelationshipDirection.Out => Out,
        RelationshipDirection.In => In,
        RelationshipDirection.Both => Both,
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null),
    };
}
