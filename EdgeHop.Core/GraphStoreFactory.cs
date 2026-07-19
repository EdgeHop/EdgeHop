using System.Collections.Concurrent;
using System.Reflection;

namespace EdgeHop.Core;

/// <summary>
/// The single place a graph backend is chosen and constructed. Every front end (extractor,
/// CLI, MCP server) obtains its <see cref="IGraphStore"/> here — selection logic must not be
/// duplicated elsewhere. Selection comes from the <c>EDGEHOP_BACKEND</c> environment
/// variable: <c>sqlite</c> (the default when unset — embedded, serverless, credential-free)
/// or <c>neo4j</c>; anything else is a clear startup error naming the valid values.
/// <para>
/// Each backend lives in its OWN assembly (<c>EdgeHop.Sqlite</c> / <c>EdgeHop.Neo4j</c>)
/// that references its driver package; this Core assembly references neither. The backend is
/// loaded REFLECTIVELY — the selected assembly is <see cref="Assembly.Load(AssemblyName)"/>ed
/// and its single <see cref="IGraphStoreProvider"/> located by a type scan. Because Core's IL
/// carries no token for either driver, JIT-compiling this factory on the sqlite path cannot
/// resolve the Neo4j driver: the "not loaded on the sqlite path" guarantee is structural. The
/// MCP server resolves the backend once at startup; branch stays per-call.
/// </para>
/// </summary>
public static class GraphStoreFactory
{
    private const string BackendVar = "EDGEHOP_BACKEND";
    private const string Neo4jBackend = "neo4j";
    private const string SqliteBackend = "sqlite";

    /// <summary>Resolved providers, cached by backend name. Providers are stateless
    /// (<see cref="IGraphStoreProvider.IsConfigured"/> reads the environment live), so a
    /// single instance is safe to reuse and avoids re-scanning the assembly.</summary>
    private static readonly ConcurrentDictionary<string, IGraphStoreProvider> ProviderCache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Creates the store selected by <c>EDGEHOP_BACKEND</c>. Store creation never opens a
    /// connection.
    /// </summary>
    /// <param name="pathHint">The caller's natural path, used only by the SQLite backend's
    /// store-per-solution derivation (see <see cref="SqliteSettings"/>): the solution
    /// directory for the indexer, the current directory for the CLI, null for the MCP server
    /// (its repo comes from <c>EDGEHOP_REPO</c>).</param>
    /// <exception cref="InvalidOperationException">The selected backend's required
    /// environment variables are missing (the message names exactly which, never any value),
    /// <c>EDGEHOP_BACKEND</c> holds an unknown value (the message names the valid ones), or
    /// the backend's plugin assembly could not be loaded.</exception>
    public static IGraphStore FromEnvironment(string? pathHint = null) =>
        GetProvider(ResolveBackend()).Create(pathHint);

    /// <summary>True when the environment is sufficiently configured for
    /// <see cref="FromEnvironment"/> to succeed (used by dry-run paths and test skips). The
    /// SQLite backend needs no configuration, so it is always configured; an invalid
    /// <c>EDGEHOP_BACKEND</c> or an unloadable plugin also reports true so the caller
    /// proceeds into <see cref="FromEnvironment"/> and gets the clear error instead of a
    /// silent skip.</summary>
    public static bool IsConfigured
    {
        get
        {
            try
            {
                return GetProvider(ResolveBackend()).IsConfigured;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    /// <summary>Normalizes <c>EDGEHOP_BACKEND</c> (trimmed, case-insensitive; unset or
    /// blank means SQLite) or throws the clear startup error for unknown values.</summary>
    private static string ResolveBackend()
    {
        var value = Environment.GetEnvironmentVariable(BackendVar);
        if (string.IsNullOrWhiteSpace(value))
        {
            return SqliteBackend;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            Neo4jBackend or SqliteBackend => normalized,
            _ => throw new InvalidOperationException(
                $"Unknown {BackendVar} value '{value.Trim()}'. " +
                $"Valid values: {SqliteBackend} (default when unset), {Neo4jBackend}."),
        };
    }

    private static IGraphStoreProvider GetProvider(string backend) =>
        ProviderCache.GetOrAdd(backend, LoadProvider);

    /// <summary>
    /// Reflectively loads the <c>EdgeHop.&lt;Backend&gt;</c> assembly and returns its single
    /// <see cref="IGraphStoreProvider"/>. The provider is found by scanning for the one
    /// concrete, default-constructible implementer (NOT a hard-coded type name — the
    /// backend's namespace is then irrelevant). Every load/scan failure maps to one friendly
    /// <see cref="InvalidOperationException"/> naming the missing plugin.
    /// </summary>
    private static IGraphStoreProvider LoadProvider(string backend)
    {
        // "sqlite" -> "EdgeHop.Sqlite", "neo4j" -> "EdgeHop.Neo4j". Only the known
        // backends reach here (ResolveBackend rejects the rest), so a simple capitalization
        // is enough; the CLR compares assembly simple names case-insensitively regardless.
        var assemblyName = "EdgeHop." + char.ToUpperInvariant(backend[0]) + backend[1..];
        try
        {
            var assembly = Assembly.Load(new AssemblyName(assemblyName));

            var providerType = assembly.GetTypes().FirstOrDefault(t =>
                typeof(IGraphStoreProvider).IsAssignableFrom(t)
                && t is { IsAbstract: false, IsInterface: false }
                && t.GetConstructor(Type.EmptyTypes) is not null);

            if (providerType is null)
            {
                throw new InvalidOperationException(
                    $"The '{backend}' graph store backend (assembly '{assemblyName}') " +
                    "contains no usable IGraphStoreProvider implementation.");
            }

            var provider = (IGraphStoreProvider)Activator.CreateInstance(providerType)!;

            if (!string.Equals(provider.BackendName, backend, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The '{assemblyName}' provider declares backend '{provider.BackendName}', " +
                    $"expected '{backend}'.");
            }

            return provider;
        }
        catch (InvalidOperationException)
        {
            throw; // already a friendly, specific message
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not load the '{backend}' graph store backend (assembly '{assemblyName}'). " +
                $"Ensure it is deployed alongside this executable. {ex.Message}",
                ex);
        }
    }
}
