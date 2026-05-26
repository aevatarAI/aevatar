namespace Aevatar.Foundation.VoicePresence.OpenAI.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class OpenAIRealtimeIntegrationFactAttribute : FactAttribute
{
    public OpenAIRealtimeIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
        {
            Skip = "Set OPENAI_API_KEY to run OpenAI realtime integration tests.";
        }
    }
}
