namespace Aevatar.Workflow.Core.Primitives;

internal sealed class WorkflowRawDefinition
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    public List<WorkflowRawRole>? Roles { get; set; }

    public List<WorkflowRawStep>? Steps { get; set; }

    public WorkflowRawConfiguration? Configuration { get; set; }
}

internal sealed class WorkflowRawRole
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? SystemPrompt { get; set; }

    public string? Provider { get; set; }

    public string? Model { get; set; }

    public double? Temperature { get; set; }

    public int? MaxTokens { get; set; }

    public int? MaxToolRounds { get; set; }

    public int? MaxHistoryMessages { get; set; }

    public int? StreamBufferCapacity { get; set; }

    public string? EventModules { get; set; }

    public string? EventRoutes { get; set; }

    public WorkflowRawRoleExtensions? Extensions { get; set; }

    public List<string>? Connectors { get; set; }
}

internal sealed class WorkflowRawStep
{
    public string? Id { get; set; }

    public string? Type { get; set; }

    public string? TargetRole { get; set; }

    public string? Role { get; set; }

    public object? Workers { get; set; }

    public object? ParallelCount { get; set; }

    public object? Count { get; set; }

    public string? VoteStepType { get; set; }

    public object? Delimiter { get; set; }

    public string? SubStepType { get; set; }

    public string? SubTargetRole { get; set; }

    public string? MapStepType { get; set; }

    public string? MapTargetRole { get; set; }

    public string? ReduceStepType { get; set; }

    public string? ReduceTargetRole { get; set; }

    public string? ReducePromptPrefix { get; set; }

    public string? SignalName { get; set; }

    public string? Prompt { get; set; }

    public object? Timeout { get; set; }

    public object? TimeoutSeconds { get; set; }

    public object? DurationMs { get; set; }

    public string? Variable { get; set; }

    public string? OnTimeout { get; set; }

    public string? OnReject { get; set; }

    public string? Workflow { get; set; }

    public string? Lifecycle { get; set; }

    public string? Query { get; set; }

    public object? TopK { get; set; }

    public object? Facts { get; set; }

    public Dictionary<string, object?>? Parameters { get; set; }

    public string? Next { get; set; }

    public List<WorkflowRawStep>? Children { get; set; }

    public object? Branches { get; set; }

    public WorkflowRawRetry? Retry { get; set; }

    public WorkflowRawOnError? OnError { get; set; }

    public int? TimeoutMs { get; set; }
}

internal sealed class WorkflowRawRetry
{
    public int? MaxAttempts { get; set; }

    public string? Backoff { get; set; }

    public int? DelayMs { get; set; }
}

internal sealed class WorkflowRawOnError
{
    public string? Strategy { get; set; }

    public string? FallbackStep { get; set; }

    public string? DefaultOutput { get; set; }
}

internal sealed class WorkflowRawConfiguration
{
    public bool? ClosedWorldMode { get; set; }
}

internal sealed class WorkflowRawRoleExtensions
{
    public string? EventModules { get; set; }

    public string? EventRoutes { get; set; }
}
