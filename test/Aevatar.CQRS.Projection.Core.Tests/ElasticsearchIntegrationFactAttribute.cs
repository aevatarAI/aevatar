namespace Aevatar.CQRS.Projection.Core.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ElasticsearchIntegrationFactAttribute : FactAttribute
{
    public ElasticsearchIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEVATAR_TEST_ELASTICSEARCH_ENDPOINT")))
        {
            Skip = "Set AEVATAR_TEST_ELASTICSEARCH_ENDPOINT to run Elasticsearch projection integration tests.";
        }
    }
}
