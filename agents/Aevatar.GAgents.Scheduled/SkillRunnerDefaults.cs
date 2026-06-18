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
    public const string StatusCompleted = "completed";
    public const string RejectionReasonRunnerDisabled = "runner_disabled";
    public const string ScheduleTriggerReason = "schedule";
    public const string ExternalTriggerReason = "external_trigger";
    public const string OneShotTriggerReason = "one_shot";
    public const string OneShotSkillName = "one-shot-reminder";
    public const string OneShotRetirementReasonCompleted = "one_shot_completed";
    public const string OneShotRetirementReasonFailed = "one_shot_failed";
    public const string OneShotRetirementReasonRejected = "one_shot_rejected";
    public const string ExternalTriggerRejectedReasonUnknownSource = "unknown";
    public const string ExternalTriggerRejectedReasonDisabledSource = "disabled";
    public const string ExternalTriggerRejectedReasonMalformedDelivery = "malformed_delivery";
    public const string ExternalTriggerRejectedReasonDispatchAttemptsExhausted = "dispatch_attempts_exhausted";
    public const string ExternalTriggerDuplicateReasonAlreadyAdmitted = "duplicate_delivery";
    public const string TriggerCallbackId = "skill-runner-next-fire";
    public const string RetryCallbackId = "skill-runner-retry";
    public const int MaxRetryAttempts = 1;
    public const int ExternalTriggerMaxDispatchAttempts = 3;
    public const int ExternalTriggerTerminalDeliveryRetention = 1000;
    public const int CronOccurrenceTerminalRetention = 1000;
    public static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ExternalTriggerTerminalDeliveryRetentionAge = TimeSpan.FromDays(30);
    public static readonly TimeSpan CronOccurrenceTerminalRetentionAge = TimeSpan.FromDays(30);

    /// <summary>
    /// Throttle for streaming-edit (Lark <c>PUT /open-apis/im/v1/messages/{id}</c>) deltas.
    /// PUT is the correct verb for editing text/post messages — PATCH on the same path is
    /// reserved for editing interactive cards. Lark's documented edit rate limit is ~5
    /// edits/sec; 300 ms gives us ~3.3 edits/sec which
    /// stays safely under the limit even when the LLM produces tokens in bursts. The first delta
    /// always dispatches immediately (the throttle gate is "elapsed since last emit" and starts
    /// from <c>DateTimeOffset.MinValue</c>) so the placeholder lands as soon as the LLM warms up.
    /// </summary>
    public static readonly TimeSpan StreamingEditThrottle = TimeSpan.FromMilliseconds(300);

    public static string GenerateActorId() => $"{ActorIdPrefix}-{Guid.NewGuid():N}";
}
