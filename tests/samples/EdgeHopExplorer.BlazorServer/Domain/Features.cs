namespace EdgeHopExplorer.BlazorServer.Domain;

/// <summary>
/// Documents EdgeHop's six node kinds. INHERITS <see cref="FeatureBase"/>, additionally
/// IMPLEMENTS <see cref="ISearchable"/> (a second, direct interface), OVERRIDES
/// <see cref="FeatureBase.Describe"/>, and its override body CALLS the inherited
/// <see cref="FeatureBase.Prefix"/>.
/// </summary>
public sealed class NodeKindsFeature : FeatureBase, ISearchable
{
    private static readonly string[] Kinds =
        ["Namespace", "NamedType", "Method", "Property", "Field", "Event"];

    public NodeKindsFeature() : base("Node kinds", FeatureArea.Extraction)
    {
    }

    public override string Describe() => $"{Prefix()} {string.Join(", ", Kinds)}";

    public bool Matches(string term) =>
        Kinds.Any(k => k.Contains(term, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Documents EdgeHop's ten edge types. INHERITS <see cref="FeatureBase"/>,
/// OVERRIDES <see cref="FeatureBase.Describe"/>, CALLS <see cref="FeatureBase.Prefix"/>.</summary>
public sealed class EdgeTypesFeature : FeatureBase
{
    public EdgeTypesFeature() : base("Edge types", FeatureArea.Extraction)
    {
    }

    public override string Describe() =>
        $"{Prefix()} CONTAINS, CALLS, IMPLEMENTS, INHERITS, REFERENCES, OVERRIDES, RENDERS, HTTP_CALLS, JS_CALLS, JS_INVOKES";
}

/// <summary>Documents the five query tools. INHERITS <see cref="FeatureBase"/>,
/// OVERRIDES <see cref="FeatureBase.Describe"/>, CALLS <see cref="FeatureBase.Prefix"/>.</summary>
public sealed class QueryToolsFeature : FeatureBase
{
    public QueryToolsFeature() : base("Query tools", FeatureArea.Query)
    {
    }

    public override string Describe() =>
        $"{Prefix()} find_symbol, get_callers, get_relationships, get_path, graph_stats";
}
