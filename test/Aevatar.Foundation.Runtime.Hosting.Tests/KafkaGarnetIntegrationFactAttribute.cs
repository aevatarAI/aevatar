namespace Aevatar.Foundation.Runtime.Hosting.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class KafkaGarnetIntegrationFactAttribute : FactAttribute
{
    public KafkaGarnetIntegrationFactAttribute()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEVATAR_TEST_KAFKA_BOOTSTRAP_SERVERS")))
            missing.Add("AEVATAR_TEST_KAFKA_BOOTSTRAP_SERVERS");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEVATAR_TEST_GARNET_CONNECTION_STRING")))
            missing.Add("AEVATAR_TEST_GARNET_CONNECTION_STRING");

        if (missing.Count > 0)
            Skip = $"Set {string.Join(" and ", missing)} to run Orleans + Kafka + Garnet integration tests.";
    }
}
