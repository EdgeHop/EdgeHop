namespace EdgeHopExplorer.BlazorServer.Domain;

/// <summary>
/// Abstract base for every catalog entry. This one type demonstrates several graph facts at once:
/// <list type="bullet">
///   <item>it IMPLEMENTS <see cref="IFeature"/> (a class→interface edge);</item>
///   <item>it is an <c>abstract</c> type, so its node carries <c>isAbstract = true</c>;</item>
///   <item><see cref="Describe"/> is <c>virtual</c>, so each subclass's <c>override</c> produces an
///         OVERRIDES edge back to it;</item>
///   <item>the explicit constructor is a Method node whose parameter type REFERENCES
///         <see cref="FeatureArea"/>.</item>
/// </list>
/// </summary>
public abstract class FeatureBase : IFeature
{
    protected FeatureBase(string name, FeatureArea area)
    {
        Name = name;
        Area = area;
    }

    public string Name { get; }

    public FeatureArea Area { get; }

    /// <summary>Virtual so concrete features can OVERRIDE it; several of them call
    /// <see cref="Prefix"/> from their override body, which is a resolved CALLS edge.</summary>
    public virtual string Describe() => $"{Name} ({Area})";

    /// <summary>A source-declared helper the overrides invoke — the target of those CALLS edges.</summary>
    protected string Prefix() => $"[{Area}]";
}
