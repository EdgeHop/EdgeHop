namespace EdgeHop.Core;

/// <summary>
/// The <see cref="IGraphStoreProvider"/> for the SQLite backend — discovered reflectively by
/// <see cref="GraphStoreFactory"/> from this assembly (<c>EdgeHop.Sqlite</c>). Needs no
/// configuration (the store is a local file), so <see cref="IsConfigured"/> is always true.
/// </summary>
public sealed class SqliteGraphStoreProvider : IGraphStoreProvider
{
    public string BackendName => "sqlite";

    public bool IsConfigured => true;

    public IGraphStore Create(string? pathHint) =>
        new SqliteGraphStore(SqliteSettings.FromEnvironment(pathHint));
}
