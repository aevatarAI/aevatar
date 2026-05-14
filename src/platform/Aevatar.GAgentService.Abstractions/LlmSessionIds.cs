namespace Aevatar.GAgentService.Abstractions;

public static class LlmSessionIds
{
    public static string BuildKey(string responseId)
    {
        if (string.IsNullOrWhiteSpace(responseId))
            throw new ArgumentException("responseId is required.", nameof(responseId));

        return responseId.Trim();
    }

    public static string NewActorId() => "response-session-" + Guid.NewGuid().ToString("N");
}
