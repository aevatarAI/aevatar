namespace Aevatar.GAgents.Scheduled;

public static class SkillDefinitionDefaults
{
    public const string AgentType = "skill_runner";
    public const string ActorIdPrefix = "skill-runner";
    public const string DefaultProviderName = "nyxid";
    public const string DefaultPlatform = "lark";
    public const string DefaultTimezone = "UTC";
    public const int DefaultMaxToolRounds = 20;
    public const int DefaultMaxHistoryMessages = 32;
    public const string StatusRunning = "running";
    public const string StatusError = "error";
    public const string StatusDisabled = "disabled";
    public const string TriggerCallbackId = "skill-runner-next-fire";
    public const string RetryCallbackId = "skill-runner-retry";
    public const string ReportRetryCallbackId = "skill-runner-report-retry";
    public const int MaxRetryAttempts = 1;
    public const int MaxReportRetryAttempts = 3;
    public static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ReportRetryBackoff = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan StreamingEditThrottle = TimeSpan.FromMilliseconds(300);

    public static string GenerateActorId() => $"{ActorIdPrefix}-{Guid.NewGuid():N}";

    public static string GenerateExecutionId(string definitionId) =>
        $"{definitionId}-exec-{Guid.NewGuid():N}";
}
