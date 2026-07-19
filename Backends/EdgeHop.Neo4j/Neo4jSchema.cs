using Neo4j.Driver;

namespace EdgeHop.Core;

/// <summary>
/// The Neo4j schema DDL from the README, verbatim. The uniqueness constraint on the
/// composite identity <c>(branch, id)</c> is the foundation everything keys off —
/// apply this before any writer code touches the database.
/// </summary>
public static class Neo4jSchema
{
    /// <summary>
    /// Uniqueness constraint on the composite identity. Even though phases 1–3 are
    /// single-branch, <c>branch</c> is baked into identity NOW so the later multi-branch
    /// handoff is a data change, not a schema migration.
    /// </summary>
    public const string SymbolIdConstraint = """
        CREATE CONSTRAINT symbol_id IF NOT EXISTS
        FOR (s:Symbol)
        REQUIRE (s.branch, s.id) IS UNIQUE;
        """;

    public const string SymbolNameIndex =
        "CREATE INDEX symbol_name IF NOT EXISTS FOR (s:Symbol) ON (s.name);";

    public const string SymbolKindIndex =
        "CREATE INDEX symbol_kind IF NOT EXISTS FOR (s:Symbol) ON (s.kind);";

    public const string SymbolSourceDocIndex =
        "CREATE INDEX symbol_sourcedoc IF NOT EXISTS FOR (s:Symbol) ON (s.sourceDoc);";

    /// <summary>All DDL statements, in application order (constraint first).</summary>
    public static readonly IReadOnlyList<string> AllStatements = new[]
    {
        SymbolIdConstraint,
        SymbolNameIndex,
        SymbolKindIndex,
        SymbolSourceDocIndex,
    };

    /// <summary>
    /// Applies the DDL to <paramref name="database"/>. Every statement uses
    /// <c>IF NOT EXISTS</c>, so this is idempotent and safe to run on every startup.
    /// Schema commands run as auto-commit statements (one per implicit transaction),
    /// which is what Neo4j requires for DDL.
    /// </summary>
    public static async Task ApplyAsync(IDriver driver, string database)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);

        var session = driver.AsyncSession(o => o.WithDatabase(database));
        try
        {
            foreach (var statement in AllStatements)
            {
                // The constants keep the README's trailing ';' (script form); the Bolt
                // protocol wants exactly one statement with no terminator, so strip it here.
                var text = statement.Trim().TrimEnd(';');
                var cursor = await session.RunAsync(text).ConfigureAwait(false);
                await cursor.ConsumeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await session.CloseAsync().ConfigureAwait(false);
        }
    }
}
