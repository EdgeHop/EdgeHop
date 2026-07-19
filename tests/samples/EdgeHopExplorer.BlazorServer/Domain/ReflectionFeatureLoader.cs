using System.Reflection;

namespace EdgeHopExplorer.BlazorServer.Domain;

/// <summary>
/// Discovers every concrete <see cref="FeatureBase"/> at runtime via reflection.
/// <para>
/// This class is deliberately <b>invisible</b> to EdgeHop, and that is the teaching point. The
/// graph records static structure — declarations and semantically-resolved calls — not reflective
/// dispatch: <c>Assembly.GetTypes</c>, <c>Type.IsAssignableFrom</c>, and
/// <c>Activator.CreateInstance</c> all resolve to framework methods, so they produce no CALLS edge,
/// and the runtime coupling this method creates never appears in the graph. A code graph shows what
/// is wired at compile time; reflection is precisely the seam it cannot see. The only edge this
/// method contributes is a REFERENCES to <see cref="FeatureBase"/> from its return type.
/// </para>
/// </summary>
public static class ReflectionFeatureLoader
{
    public static IReadOnlyList<FeatureBase> DiscoverAll()
    {
        var discovered = new List<FeatureBase>();
        var baseType = typeof(FeatureBase);

        foreach (var type in baseType.Assembly.GetTypes())
        {
            if (!type.IsAbstract
                && baseType.IsAssignableFrom(type)
                && Activator.CreateInstance(type) is FeatureBase feature)
            {
                discovered.Add(feature);
            }
        }

        return discovered;
    }
}
