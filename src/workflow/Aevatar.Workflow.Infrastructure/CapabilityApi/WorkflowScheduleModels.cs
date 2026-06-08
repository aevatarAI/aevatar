namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public sealed record WorkflowScheduleTargetInput
{
    public string? Prompt { get; init; }
    public WorkflowChatSourceInput? Source { get; init; }
    public string? SessionId { get; init; }
    public string? ScopeId { get; init; }
    public IDictionary<string, string>? Annotations { get; init; }
    public IDictionary<string, string>? Headers { get; init; }
}

public sealed record WorkflowScheduleCreateInput
{
    public string? ScheduleId { get; init; }
    public string? Name { get; init; }
    public string? Cron { get; init; }
    public string? Timezone { get; init; }
    public bool Enabled { get; init; } = true;
    public WorkflowScheduleTargetInput? Target { get; init; }
}

public sealed record WorkflowScheduleUpdateInput
{
    public string? Name { get; init; }
    public string? Cron { get; init; }
    public string? Timezone { get; init; }
    public WorkflowScheduleTargetInput? Target { get; init; }
}

public sealed record WorkflowSchedulePreviewInput
{
    public string? Cron { get; init; }
    public string? Timezone { get; init; }
    public DateTimeOffset? FromUtc { get; init; }
    public int Count { get; init; } = 10;
}

public sealed record WorkflowScheduleRunNowInput
{
    public DateTimeOffset? ScheduledFireAtUtc { get; init; }
    public bool Force { get; init; }
}
