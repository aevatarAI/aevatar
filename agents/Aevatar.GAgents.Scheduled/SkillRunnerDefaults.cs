namespace Aevatar.GAgents.Scheduled;

public static class SkillRunnerDefaults
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
    public const int MaxRetryAttempts = 1;
    public static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Throttle for streaming-edit (Lark <c>PATCH /open-apis/im/v1/messages/{id}</c>) deltas.
    /// Lark's documented edit rate limit is ~5 edits/sec; 300 ms gives us ~3.3 edits/sec which
    /// stays safely under the limit even when the LLM produces tokens in bursts. The first delta
    /// always dispatches immediately (the throttle gate is "elapsed since last emit" and starts
    /// from <c>DateTimeOffset.MinValue</c>) so the placeholder lands as soon as the LLM warms up.
    /// </summary>
    public static readonly TimeSpan StreamingEditThrottle = TimeSpan.FromMilliseconds(300);

    public static string GenerateActorId() => $"{ActorIdPrefix}-{Guid.NewGuid():N}";
}
