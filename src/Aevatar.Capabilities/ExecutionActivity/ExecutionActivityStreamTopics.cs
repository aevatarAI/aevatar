namespace Aevatar.Capabilities.ExecutionActivity;

public static class ExecutionActivityStreamTopics
{
    public static string ForScope(string scopeId)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
            throw new ArgumentException("scopeId is required.", nameof(scopeId));

        return $"aevatar://scopes/{Uri.EscapeDataString(scopeId.Trim())}/execution-events";
    }
}
