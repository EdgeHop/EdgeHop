using Microsoft.CodeAnalysis;

namespace EdgeHop.Core;

/// <summary>
/// Produces the stable, deterministic identifier used as the Neo4j <c>:Symbol.id</c>
/// property (composite-keyed with <c>branch</c>). The ID must be identical across
/// recompiles of the same source so that graph writes are idempotent upserts —
/// getting this subtly wrong (generics, overloads, partial types) produces duplicate
/// nodes instead of clean upserts.
/// </summary>
public static class SymbolIdFormat
{
    /// <summary>
    /// Fully-qualified display format: includes containing types and namespaces,
    /// parameter types, type parameters, and enough detail to disambiguate overloads.
    /// </summary>
    public static readonly SymbolDisplayFormat Format = new SymbolDisplayFormat(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle:
            SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions:
            SymbolDisplayGenericsOptions.IncludeTypeParameters |
            SymbolDisplayGenericsOptions.IncludeTypeConstraints,
        memberOptions:
            SymbolDisplayMemberOptions.IncludeParameters |
            SymbolDisplayMemberOptions.IncludeContainingType |
            SymbolDisplayMemberOptions.IncludeType,
        parameterOptions:
            SymbolDisplayParameterOptions.IncludeType |
            SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
            SymbolDisplayMiscellaneousOptions.ExpandNullable);

    /// <summary>
    /// Returns the stable ID for <paramref name="symbol"/> in the form
    /// <c>"{Kind}:{fully-qualified display string}"</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Normalization decision:</b> the symbol is normalized to
    /// <see cref="ISymbol.OriginalDefinition"/> <em>first</em>, before the display
    /// string is generated. Constructed generics therefore collapse onto their open
    /// definition: <c>List&lt;int&gt;</c> and <c>List&lt;string&gt;</c> both yield
    /// the ID of <c>List&lt;T&gt;</c>, and a member of a constructed generic type
    /// (e.g. <c>Box&lt;int&gt;.Get()</c>) yields the ID of the definition member
    /// (<c>Box&lt;T&gt;.Get()</c>). This keeps the graph at exactly one node per
    /// <em>declared</em> symbol — every usage of a generic points at the single
    /// definition node instead of spawning one node per instantiation. For
    /// non-generic symbols <see cref="ISymbol.OriginalDefinition"/> returns the
    /// symbol itself, so the normalization is a harmless no-op there.
    /// </para>
    /// <para>
    /// The <see cref="SymbolKind"/> prefix guards against symbols of different kinds
    /// (e.g. a field and a property with identical display strings) ever colliding.
    /// </para>
    /// </remarks>
    public static string GetId(ISymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        // Normalize FIRST so constructed generics key on their single definition node.
        symbol = symbol.OriginalDefinition;

        // Kind prefix guards against a method and a property (etc.) ever colliding.
        var kind = symbol.Kind.ToString();
        var display = symbol.ToDisplayString(Format);
        return $"{kind}:{display}";
    }
}
