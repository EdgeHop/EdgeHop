using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace EdgeHop.Core;

/// <summary>
/// SQLite implementation of <see cref="IGraphWriter"/>, observably equivalent to
/// <see cref="Neo4jGraphWriter"/>: upsert-by-key on the composite identity (idempotent
/// re-runs), edge upserts silently skipped when either endpoint node is absent (the
/// Cypher <c>MATCH … MATCH … MERGE</c> behavior), unknown edge types rejected whole
/// before anything is written, and every delete branch-scoped with the branch as a bound
/// parameter — no code path can express a cross-branch delete. Writes are chunked into
/// one transaction per ~2000 rows.
/// </summary>
public sealed class SqliteGraphWriter : IGraphWriter
{
    /// <summary>Rows per write transaction, matching <see cref="Neo4jGraphWriter"/>.</summary>
    private const int ChunkSize = 2000;

    private const string UpsertNodeSql = """
        INSERT INTO nodes (branch, id, name, kind, sourceDoc, assembly, isAbstract, isComponent, routes)
        VALUES ($branch, $id, $name, $kind, $sourceDoc, $assembly, $isAbstract, $isComponent, $routes)
        ON CONFLICT (branch, id) DO UPDATE SET
            name = excluded.name, kind = excluded.kind, sourceDoc = excluded.sourceDoc,
            assembly = excluded.assembly, isAbstract = excluded.isAbstract,
            isComponent = excluded.isComponent, routes = excluded.routes
        """;

    // The WHERE clause both enforces the endpoints-must-exist rule (Cypher MATCH
    // semantics) and satisfies SQLite's requirement that an upsert over INSERT…SELECT
    // carry a WHERE to disambiguate the ON CONFLICT clause.
    private const string UpsertEdgeSql = """
        INSERT INTO edges (branch, type, fromId, toId, sourceDoc)
        SELECT $branch, $type, $fromId, $toId, $sourceDoc
        WHERE EXISTS (SELECT 1 FROM nodes WHERE branch = $branch AND id = $fromId)
          AND EXISTS (SELECT 1 FROM nodes WHERE branch = $branch AND id = $toId)
        ON CONFLICT (branch, type, fromId, toId) DO UPDATE SET sourceDoc = excluded.sourceDoc
        """;

    private const string DeleteNodeEdgesSql =
        "DELETE FROM edges WHERE branch = $branch AND (fromId = $id OR toId = $id)";

    private const string DeleteNodeSql =
        "DELETE FROM nodes WHERE branch = $branch AND id = $id";

    private const string DeleteEdgeSql = """
        DELETE FROM edges
        WHERE branch = $branch AND type = $type AND fromId = $fromId AND toId = $toId
        """;

    private readonly Func<SqliteConnection> _openConnection;

    public SqliteGraphWriter(Func<SqliteConnection> openConnection)
    {
        ArgumentNullException.ThrowIfNull(openConnection);
        _openConnection = openConnection;
    }

    public async Task UpsertNodesAsync(IReadOnlyCollection<NodeRow> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (nodes.Count == 0)
        {
            return;
        }

        using var connection = _openConnection();
        foreach (var chunk in nodes.Chunk(ChunkSize))
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = UpsertNodeSql;
            var branch = AddParameter(command, "$branch");
            var id = AddParameter(command, "$id");
            var name = AddParameter(command, "$name");
            var kind = AddParameter(command, "$kind");
            var sourceDoc = AddParameter(command, "$sourceDoc");
            var assembly = AddParameter(command, "$assembly");
            var isAbstract = AddParameter(command, "$isAbstract");
            var isComponent = AddParameter(command, "$isComponent");
            var routes = AddParameter(command, "$routes");

            foreach (var node in chunk)
            {
                branch.Value = node.Branch;
                id.Value = node.Id;
                name.Value = node.Name;
                kind.Value = node.Kind;
                sourceDoc.Value = (object?)node.SourceDoc ?? DBNull.Value;
                assembly.Value = node.Assembly;
                isAbstract.Value = node.IsAbstract;
                isComponent.Value = node.IsComponent;
                // NULL removes the value, matching the Cypher writer's "SET s.routes =
                // null REMOVES the property" upsert semantics for deleted @page routes.
                routes.Value = node.Routes is null
                    ? DBNull.Value
                    : JsonSerializer.Serialize(node.Routes);
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            transaction.Commit();
        }
    }

    public async Task UpsertEdgesAsync(IReadOnlyCollection<EdgeRow> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);
        if (edges.Count == 0)
        {
            return;
        }

        // Same whole-batch rejection as the Neo4j writer: although SQLite binds the type
        // as a value (no splicing), the validation CONTRACT is backend-independent — a
        // bad batch fails whole before anything is written.
        ThrowOnUnknownEdgeTypes(edges.Select(e => e.Type));

        using var connection = _openConnection();
        foreach (var chunk in edges.Chunk(ChunkSize))
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = UpsertEdgeSql;
            var branch = AddParameter(command, "$branch");
            var type = AddParameter(command, "$type");
            var fromId = AddParameter(command, "$fromId");
            var toId = AddParameter(command, "$toId");
            var sourceDoc = AddParameter(command, "$sourceDoc");

            foreach (var edge in chunk)
            {
                branch.Value = edge.Branch;
                type.Value = edge.Type;
                fromId.Value = edge.FromId;
                toId.Value = edge.ToId;
                sourceDoc.Value = (object?)edge.SourceDoc ?? DBNull.Value;
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            transaction.Commit();
        }
    }

    public async Task DeleteNodesAsync(string branch, IReadOnlyCollection<string> ids)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return;
        }

        using var connection = _openConnection();
        foreach (var chunk in ids.Chunk(ChunkSize))
        {
            using var transaction = connection.BeginTransaction();

            // Edge sweep first (the DETACH in DETACH DELETE), then the node rows.
            using var deleteEdges = connection.CreateCommand();
            deleteEdges.Transaction = transaction;
            deleteEdges.CommandText = DeleteNodeEdgesSql;
            var edgeBranch = AddParameter(deleteEdges, "$branch");
            var edgeId = AddParameter(deleteEdges, "$id");

            using var deleteNode = connection.CreateCommand();
            deleteNode.Transaction = transaction;
            deleteNode.CommandText = DeleteNodeSql;
            var nodeBranch = AddParameter(deleteNode, "$branch");
            var nodeId = AddParameter(deleteNode, "$id");

            foreach (var id in chunk)
            {
                edgeBranch.Value = branch;
                edgeId.Value = id;
                await deleteEdges.ExecuteNonQueryAsync().ConfigureAwait(false);

                nodeBranch.Value = branch;
                nodeId.Value = id;
                await deleteNode.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            transaction.Commit();
        }
    }

    public async Task DeleteEdgesAsync(string branch, IReadOnlyCollection<EdgeKey> edges)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentNullException.ThrowIfNull(edges);
        if (edges.Count == 0)
        {
            return;
        }

        ThrowOnUnknownEdgeTypes(edges.Select(e => e.Type));

        using var connection = _openConnection();
        foreach (var chunk in edges.Chunk(ChunkSize))
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = DeleteEdgeSql;
            var branchParam = AddParameter(command, "$branch");
            var type = AddParameter(command, "$type");
            var fromId = AddParameter(command, "$fromId");
            var toId = AddParameter(command, "$toId");

            foreach (var edge in chunk)
            {
                branchParam.Value = branch;
                type.Value = edge.Type;
                fromId.Value = edge.FromId;
                toId.Value = edge.ToId;
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            transaction.Commit();
        }
    }

    public async Task<long> DeleteBranchAsync(string branch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        using var connection = _openConnection();
        using var transaction = connection.BeginTransaction();

        using var deleteEdges = connection.CreateCommand();
        deleteEdges.Transaction = transaction;
        deleteEdges.CommandText = "DELETE FROM edges WHERE branch = $branch";
        deleteEdges.Parameters.AddWithValue("$branch", branch);
        await deleteEdges.ExecuteNonQueryAsync().ConfigureAwait(false);

        using var deleteNodes = connection.CreateCommand();
        deleteNodes.Transaction = transaction;
        deleteNodes.CommandText = "DELETE FROM nodes WHERE branch = $branch";
        deleteNodes.Parameters.AddWithValue("$branch", branch);
        var deleted = await deleteNodes.ExecuteNonQueryAsync().ConfigureAwait(false);

        transaction.Commit();
        return deleted;
    }

    /// <summary>Whole-batch rejection of unknown edge types — identical message shape and
    /// exception type to <see cref="Neo4jGraphWriter"/>.</summary>
    private static void ThrowOnUnknownEdgeTypes(IEnumerable<string> types)
    {
        var unknown = types
            .Where(t => !EdgeTypes.IsValid(t))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException(
                $"Unknown edge type(s): {string.Join(", ", unknown)}. " +
                $"Valid types: {string.Join(", ", EdgeTypes.All)}.",
                "edges");
        }
    }

    private static SqliteParameter AddParameter(SqliteCommand command, string name)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        command.Parameters.Add(parameter);
        return parameter;
    }
}
