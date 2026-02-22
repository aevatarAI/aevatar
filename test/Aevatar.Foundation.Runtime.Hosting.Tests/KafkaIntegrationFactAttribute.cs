namespace Aevatar.Foundation.Runtime.Hosting.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class KafkaIntegrationFactAttribute : FactAttribute
{
    public KafkaIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEVATAR_TEST_KAFKA_BOOTSTRAP_SERVERS")))
        {
            Skip = "Set AEVATAR_TEST_KAFKA_BOOTSTRAP_SERVERS to run Orleans + Kafka integration tests.";
        }
    }
}
