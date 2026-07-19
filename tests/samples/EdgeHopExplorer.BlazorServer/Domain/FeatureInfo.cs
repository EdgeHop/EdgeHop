namespace EdgeHopExplorer.BlazorServer.Domain;

/// <summary>
/// The DTO the feature API serializes. A <c>record</c> is a NamedType and its primary constructor
/// is a Method node — but the positional parameters synthesize <b>implicitly-declared</b>
/// properties that are NOT emitted. <see cref="Summary"/> is declared explicitly in the body
/// precisely to show a real Property node living inside a record.
/// </summary>
public record FeatureInfo(string Name, string Area)
{
    public string Summary { get; init; } = $"{Name} - {Area}";
}

/// <summary>
/// The search-endpoint request body — another positional record (NamedType + a primary-constructor
/// Method node). Its single positional property is implicit, so this type adds no Property node.
/// </summary>
public record SearchRequest(string Term);
