namespace EdgeHopExplorer.BlazorServer.Domain;

/// <summary>The catalog contract the Blazor UI and the minimal API both depend on.</summary>
public interface IFeatureCatalog
{
    IReadOnlyList<IFeature> All { get; }

    IFeature? Find(string name);
}

/// <summary>
/// A generic container — a NamedType with a type parameter. Note the type parameter <c>T</c> never
/// itself produces a REFERENCES edge (type parameters are skipped); only method/field/property
/// signatures that name an authored, non-parameter type do.
/// </summary>
public sealed class Registry<T>
    where T : IFeature
{
    private readonly List<T> _items = [];

    public void Add(T item) => _items.Add(item);

    public IReadOnlyList<T> Items => _items;
}

/// <summary>
/// The concrete catalog. IMPLEMENTS <see cref="IFeatureCatalog"/>; exposes an <c>event</c> (an
/// <b>Event</b> node — the node kind none of the older fixtures exercised); its constructor CALLS
/// <see cref="Register"/>, which in turn CALLS the generic <see cref="Registry{T}.Add"/> (a
/// resolved call across a generic type, normalized to the open definition).
/// </summary>
public sealed class FeatureCatalog : IFeatureCatalog
{
    private readonly Registry<FeatureBase> _registry = new();

    /// <summary>Raised whenever a feature is registered — an Event node whose type REFERENCES the
    /// <see cref="FeatureRegisteredHandler"/> delegate.</summary>
    public event FeatureRegisteredHandler? Registered;

    public FeatureCatalog()
    {
        Register(new NodeKindsFeature());
        Register(new EdgeTypesFeature());
        Register(new QueryToolsFeature());
    }

    public IReadOnlyList<IFeature> All => _registry.Items.Cast<IFeature>().ToList();

    public IFeature? Find(string name) => _registry.Items.FirstOrDefault(f => f.Name == name);

    private void Register(FeatureBase feature)
    {
        _registry.Add(feature);
        Registered?.Invoke(feature);
    }
}
