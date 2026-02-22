namespace Aevatar.Foundation.Runtime.Hosting.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DistributedClusterIntegrationFactAttribute : FactAttribute
{
    private static readonly string[] RequiredEnvironmentVariables =
    [
        "AEVATAR_TEST_CLUSTER_NODE1_BASE_URL",
        "AEVATAR_TEST_CLUSTER_NODE2_BASE_URL",
        "AEVATAR_TEST_CLUSTER_NODE3_BASE_URL",
    ];

    public DistributedClusterIntegrationFactAttribute()
    {
        var missing = RequiredEnvironmentVariables
            .Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            .ToArray();

        if (missing.Length > 0)
        {
            Skip = "Set AEVATAR_TEST_CLUSTER_NODE1_BASE_URL / NODE2 / NODE3 to run distributed cluster integration tests.";
        }
    }
}
