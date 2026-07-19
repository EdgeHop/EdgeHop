using System.Reflection;

namespace EdgeHop.Core;

/// <summary>
/// The single place source extractors are discovered and constructed. Mirrors
/// <see cref="GraphStoreFactory"/>: each extractor lives in its own <c>EdgeHop.&lt;Name&gt;</c>
/// assembly (under <c>Extractors/</c>) that owns its toolchain dependency (MSBuild for Roslyn,
/// the oxc native binary for oxc); this Core assembly and the indexer host reference none of
/// them, so nothing on the host path can JIT-resolve MSBuild before the Roslyn plugin itself
/// registers the locator.
/// <para>
/// Selection: the <c>EDGEHOP_EXTRACTORS</c> environment variable (comma-separated names)
/// overrides the default. The default is ALL known extractors — currently <c>roslyn,oxc</c> —
/// and any whose assembly is not deployed is skipped (logged), so the set is simply "every
/// extractor plugin present." That is the owner's "both loaded by default for now."
/// </para>
/// </summary>
public static class ExtractorFactory
{
    private const string SelectorVar = "EDGEHOP_EXTRACTORS";

    /// <summary>Known extractor names, in run order. A name maps to assembly
    /// <c>EdgeHop.&lt;Name&gt;</c> (capitalized). Missing assemblies are skipped.</summary>
    private static readonly string[] DefaultExtractors = { "roslyn", "oxc" };

    /// <summary>
    /// Loads every selected, deployed extractor (default: all known ones that are present).
    /// Returns them in the configured order; the caller runs each and merges the rows. A
    /// requested extractor whose assembly is absent is logged and skipped — NOT an error —
    /// so a build without the oxc plugin still indexes C# with Roslyn alone.
    /// </summary>
    /// <exception cref="InvalidOperationException"><c>EDGEHOP_EXTRACTORS</c> selected a name
    /// whose assembly IS present but exposes no usable <see cref="IExtractorProvider"/>, or the
    /// provider declares the wrong name — a real misconfiguration, unlike a simply-absent plugin.</exception>
    public static IReadOnlyList<IExtractor> LoadAll(Action<string>? log = null)
    {
        var extractors = new List<IExtractor>();
        foreach (var name in ResolveSelection())
        {
            var provider = TryLoadProvider(name, log);
            if (provider is not null)
            {
                extractors.Add(provider.Create());
            }
        }

        if (extractors.Count == 0)
        {
            throw new InvalidOperationException(
                "No extractor plugins were found. Expected at least one of " +
                $"{string.Join(", ", DefaultExtractors)} (assembly EdgeHop.<Name>) deployed " +
                "alongside the indexer.");
        }

        return extractors;
    }

    private static IEnumerable<string> ResolveSelection()
    {
        var value = Environment.GetEnvironmentVariable(SelectorVar);
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultExtractors;
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Loads the provider for <paramref name="name"/>, or returns null when its
    /// assembly is simply not deployed (skip-and-continue). A present-but-broken plugin throws.</summary>
    private static IExtractorProvider? TryLoadProvider(string name, Action<string>? log)
    {
        var assemblyName = "EdgeHop." + char.ToUpperInvariant(name[0]) + name[1..];
        Assembly assembly;
        try
        {
            assembly = Assembly.Load(new AssemblyName(assemblyName));
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            log?.Invoke($"Extractor '{name}' (assembly '{assemblyName}') not deployed; skipping.");
            return null;
        }

        var providerType = assembly.GetTypes().FirstOrDefault(t =>
            typeof(IExtractorProvider).IsAssignableFrom(t)
            && t is { IsAbstract: false, IsInterface: false }
            && t.GetConstructor(Type.EmptyTypes) is not null);

        if (providerType is null)
        {
            throw new InvalidOperationException(
                $"Extractor assembly '{assemblyName}' contains no usable IExtractorProvider implementation.");
        }

        var provider = (IExtractorProvider)Activator.CreateInstance(providerType)!;
        if (!string.Equals(provider.ExtractorName, name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Extractor assembly '{assemblyName}' declares name '{provider.ExtractorName}', expected '{name}'.");
        }

        return provider;
    }
}
