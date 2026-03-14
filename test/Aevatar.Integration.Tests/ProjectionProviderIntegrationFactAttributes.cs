namespace Aevatar.Integration.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ElasticsearchIntegrationFactAttribute : FactAttribute
{
    public ElasticsearchIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEVATAR_TEST_ELASTICSEARCH_ENDPOINT")))
        {
            Skip = "Set AEVATAR_TEST_ELASTICSEARCH_ENDPOINT to run Elasticsearch-backed scripting integration tests.";
        }
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class Neo4jIntegrationFactAttribute : FactAttribute
{
    public Neo4jIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEVATAR_TEST_NEO4J_URI")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEVATAR_TEST_NEO4J_USERNAME")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEVATAR_TEST_NEO4J_PASSWORD")))
        {
            Skip = "Set AEVATAR_TEST_NEO4J_URI / AEVATAR_TEST_NEO4J_USERNAME / AEVATAR_TEST_NEO4J_PASSWORD to run Neo4j-backed scripting integration tests.";
        }
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ProjectionProvidersIntegrationFactAttribute : FactAttribute
{
    public ProjectionProvidersIntegrationFactAttribute()
    {
        var hasElasticsearch =
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEVATAR_TEST_ELASTICSEARCH_ENDPOINT"));
        var hasNeo4j =
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEVATAR_TEST_NEO4J_URI")) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEVATAR_TEST_NEO4J_USERNAME")) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEVATAR_TEST_NEO4J_PASSWORD"));
        if (!hasElasticsearch || !hasNeo4j)
        {
            Skip =
                "Set both Elasticsearch and Neo4j integration environment variables to run combined scripting provider integration tests.";
        }
    }
}
