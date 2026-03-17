namespace Aevatar.Integration.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class Orleans3ClusterIntegrationFactAttribute : FactAttribute
{
    public Orleans3ClusterIntegrationFactAttribute()
    {
        var enabled = Environment.GetEnvironmentVariable("AEVATAR_TEST_ORLEANS_3NODE");
        if (!string.Equals(enabled, "1", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
        {
            Skip = "Set AEVATAR_TEST_ORLEANS_3NODE=1 to run Orleans 3-node cluster integration tests.";
            return;
        }

        var requiredVariables = new[]
        {
            "AEVATAR_TEST_GARNET_CONNECTION_STRING",
            "AEVATAR_TEST_KAFKA_BOOTSTRAP_SERVERS",
            "AEVATAR_TEST_ELASTICSEARCH_ENDPOINT",
            "AEVATAR_TEST_NEO4J_URI",
            "AEVATAR_TEST_NEO4J_USERNAME",
            "AEVATAR_TEST_NEO4J_PASSWORD",
        };
        var missing = requiredVariables
            .Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            .ToArray();
        if (missing.Length > 0)
        {
            Skip =
                "Set AEVATAR_TEST_GARNET_CONNECTION_STRING / AEVATAR_TEST_KAFKA_BOOTSTRAP_SERVERS / " +
                "AEVATAR_TEST_ELASTICSEARCH_ENDPOINT / AEVATAR_TEST_NEO4J_URI / " +
                "AEVATAR_TEST_NEO4J_USERNAME / AEVATAR_TEST_NEO4J_PASSWORD to run Orleans 3-node cluster integration tests.";
        }
    }
}
