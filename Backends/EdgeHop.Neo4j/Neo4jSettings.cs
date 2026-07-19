namespace EdgeHop.Core;

/// <summary>
/// Neo4j connection settings. Values come exclusively from environment variables set by
/// the developer — this codebase never stores or supplies credentials itself.
/// </summary>
/// <param name="Uri">Bolt URI, e.g. <c>bolt://localhost:7687</c> (from <c>NEO4J_URI</c>).</param>
/// <param name="User">User name (from <c>NEO4J_USER</c>).</param>
/// <param name="Password">Password (from <c>NEO4J_PASSWORD</c>). Never logged or written anywhere.</param>
/// <param name="Database">Database name (from <c>NEO4J_DATABASE</c>, default <c>"neo4j"</c>).</param>
public sealed record Neo4jSettings(string Uri, string User, string Password, string Database)
{
    private const string UriVar = "NEO4J_URI";
    private const string UserVar = "NEO4J_USER";
    private const string PasswordVar = "NEO4J_PASSWORD";
    private const string DatabaseVar = "NEO4J_DATABASE";
    private const string DefaultDatabase = "neo4j";

    /// <summary>
    /// True when all required environment variables (<c>NEO4J_URI</c>, <c>NEO4J_USER</c>,
    /// <c>NEO4J_PASSWORD</c>) are present and non-empty. Used by tests to skip
    /// Neo4j-dependent facts instead of failing when no database is reachable.
    /// </summary>
    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(UriVar)) &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(UserVar)) &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PasswordVar));

    /// <summary>
    /// Reads settings from the environment. Throws <see cref="InvalidOperationException"/>
    /// naming exactly which required variables are missing, so the developer knows what to set.
    /// </summary>
    public static Neo4jSettings FromEnvironment()
    {
        var uri = Environment.GetEnvironmentVariable(UriVar);
        var user = Environment.GetEnvironmentVariable(UserVar);
        var password = Environment.GetEnvironmentVariable(PasswordVar);
        var database = Environment.GetEnvironmentVariable(DatabaseVar);

        var missing = new List<string>(3);
        if (string.IsNullOrWhiteSpace(uri))
        {
            missing.Add(UriVar);
        }

        if (string.IsNullOrWhiteSpace(user))
        {
            missing.Add(UserVar);
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            missing.Add(PasswordVar);
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Neo4j is not configured: missing environment variable(s) {string.Join(", ", missing)}. " +
                $"Set {UriVar}, {UserVar} and {PasswordVar} (and optionally {DatabaseVar}, default '{DefaultDatabase}').");
        }

        return new Neo4jSettings(
            uri!,
            user!,
            password!,
            string.IsNullOrWhiteSpace(database) ? DefaultDatabase : database);
    }
}
