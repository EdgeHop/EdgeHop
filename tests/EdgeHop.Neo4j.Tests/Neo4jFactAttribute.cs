using EdgeHop.Core;
using Xunit;

namespace EdgeHop.Tests;

/// <summary>
/// An xUnit fact that runs only when a Neo4j instance is configured via environment
/// variables. When <see cref="Neo4jSettings.IsConfigured"/> is false the test is skipped
/// (not failed) with a message telling the developer what to set — the build must never
/// block on a database being reachable.
/// </summary>
public sealed class Neo4jFactAttribute : FactAttribute
{
    public Neo4jFactAttribute()
    {
        if (!Neo4jSettings.IsConfigured)
        {
            Skip = "Requires a live Neo4j instance. Set the NEO4J_URI, NEO4J_USER and " +
                   "NEO4J_PASSWORD environment variables (and optionally NEO4J_DATABASE, " +
                   "default 'neo4j') to run this test.";
        }
    }
}
