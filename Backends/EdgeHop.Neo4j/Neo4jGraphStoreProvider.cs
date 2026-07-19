namespace EdgeHop.Core;

/// <summary>
/// The <see cref="IGraphStoreProvider"/> for the Neo4j backend — discovered reflectively by
/// <see cref="GraphStoreFactory"/> from this assembly (<c>EdgeHop.Neo4j</c>).
/// <see cref="IsConfigured"/> checks the <c>NEO4J_*</c> environment variables without
/// touching the driver, so a false result is a cheap probe; <see cref="Create"/> reads them
/// (throwing a clear <see cref="InvalidOperationException"/> naming any missing one).
/// </summary>
public sealed class Neo4jGraphStoreProvider : IGraphStoreProvider
{
    public string BackendName => "neo4j";

    public bool IsConfigured => Neo4jSettings.IsConfigured;

    public IGraphStore Create(string? pathHint) =>
        new Neo4jGraphStore(Neo4jSettings.FromEnvironment());
}
