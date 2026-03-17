namespace Aevatar.Foundation.Runtime.Hosting.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class GarnetIntegrationFactAttribute : FactAttribute
{
    public GarnetIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEVATAR_TEST_GARNET_CONNECTION_STRING")))
        {
            Skip = "Set AEVATAR_TEST_GARNET_CONNECTION_STRING to run Orleans + Garnet integration tests.";
        }
    }
}
