namespace EdgeHopExplorer.BlazorServer.Domain;

/// <summary>
/// A single entry in the EdgeHop feature catalog. Implemented (transitively) by every concrete
/// feature via <see cref="FeatureBase"/> — the class→interface link is an <b>IMPLEMENTS</b> edge.
/// An interface is a NamedType with <c>isAbstract = true</c>; its members are nodes too (the
/// accessors are not).
/// </summary>
public interface IFeature
{
    string Name { get; }

    /// <summary>The area this feature belongs to — a Property whose type REFERENCES <see cref="FeatureArea"/>.</summary>
    FeatureArea Area { get; }

    string Describe();
}

/// <summary>
/// A second capability some features add, so a single concrete type can carry TWO direct
/// IMPLEMENTS edges (only the directly-listed interfaces produce edges — interfaces reached only
/// through a base class do not).
/// </summary>
public interface ISearchable
{
    bool Matches(string term);
}

/// <summary>
/// Delegate raised when a feature is registered. A <c>delegate</c> is a NamedType node; the event
/// that uses it (see <see cref="FeatureCatalog.Registered"/>) REFERENCES it, and its parameter type
/// REFERENCES <see cref="FeatureBase"/>.
/// </summary>
public delegate void FeatureRegisteredHandler(FeatureBase feature);
