namespace Aevatar.GAgents.Scheduled;

public sealed class ScheduledAgentCreatorOptions
{
    public const string DefaultOrnnServiceSlug = "ornn-api";
    public const int DefaultApiKeyLifetimeDays = 90;
    public const string DefaultNyxIdAuthority = "nyxid";

    public string OrnnServiceSlug { get; set; } = DefaultOrnnServiceSlug;

    public int ApiKeyLifetimeDays { get; set; } = DefaultApiKeyLifetimeDays;

    public string NyxIdAuthority { get; set; } = DefaultNyxIdAuthority;
}
