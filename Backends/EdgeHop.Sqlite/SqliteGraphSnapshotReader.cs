using Microsoft.Data.Sqlite;

namespace EdgeHop.Core;

/// <summary>
/// SQLite implementation of <see cref="IGraphSnapshotReader"/>: the key-set reads behind
/// <see cref="GraphReconciler"/> and the <c>branches</c> verb. Branch-scoped, every value
/// a bound parameter; unknown edge types are filtered out client-side (reported via the
/// callback) exactly like the Neo4j snapshot reader, so an edge type this build does not
/// know about can never end up in a delete plan.
/// </summary>
public sealed class SqliteGraphSnapshotReader : IGraphSnapshotReader
{
    private readonly Func<SqliteConnection> _openConnection;

    public SqliteGraphSnapshotReader(Func<SqliteConnection> openConnection)
    {
        ArgumentNullException.ThrowIfNull(openConnection);
        _openConnection = openConnection;
    }

    public async Task<IReadOnlyList<string>> GetNodeIdsAsync(
        string branch, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        using var connection = _openConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM nodes WHERE branch = $branch";
        command.Parameters.AddWithValue("$branch", branch);

        var ids = new List<string>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    public async Task<IReadOnlyList<EdgeKey>> GetEdgeKeysAsync(
        string branch,
        Action<string>? onUnknownType = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        using var connection = _openConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, fromId, toId FROM edges WHERE branch = $branch";
        command.Parameters.AddWithValue("$branch", branch);

        var keys = new List<EdgeKey>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var type = reader.GetString(0);
            if (!EdgeTypes.IsValid(type))
            {
                onUnknownType?.Invoke(type);
                continue;
            }

            keys.Add(new EdgeKey(type, reader.GetString(1), reader.GetString(2)));
        }

        return keys;
    }

    public async Task<IReadOnlyList<(string Branch, long Nodes)>> GetBranchesAsync(
        CancellationToken cancellationToken = default)
    {
        using var connection = _openConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT branch, COUNT(*) FROM nodes
            GROUP BY branch
            ORDER BY branch
            """;

        var branches = new List<(string, long)>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            branches.Add((reader.GetString(0), reader.GetInt64(1)));
        }

        return branches;
    }
}
