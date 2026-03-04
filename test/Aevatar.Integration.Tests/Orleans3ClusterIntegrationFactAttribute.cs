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
        }
    }
}
