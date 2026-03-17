namespace Aevatar.Bootstrap.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class OpenAIIntegrationFactAttribute : FactAttribute
{
    public OpenAIIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
        {
            Skip = "Set OPENAI_API_KEY to run real OpenAI provider integration tests.";
        }
    }
}
