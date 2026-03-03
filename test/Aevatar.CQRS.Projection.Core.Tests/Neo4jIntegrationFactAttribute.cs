namespace Aevatar.CQRS.Projection.Core.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class Neo4jIntegrationFactAttribute : FactAttribute
{
    public Neo4jIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEVATAR_TEST_NEO4J_URI")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEVATAR_TEST_NEO4J_USERNAME")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEVATAR_TEST_NEO4J_PASSWORD")))
        {
            Skip = "Set AEVATAR_TEST_NEO4J_URI / AEVATAR_TEST_NEO4J_USERNAME / AEVATAR_TEST_NEO4J_PASSWORD to run Neo4j projection integration tests.";
        }
    }
}
