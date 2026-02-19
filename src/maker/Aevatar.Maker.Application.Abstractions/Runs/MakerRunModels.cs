namespace Aevatar.Maker.Application.Abstractions.Runs;

public sealed record MakerRunRequest(
    string WorkflowYaml,
    string WorkflowName,
    string Input,
    string? ActorId = null,
    TimeSpan? Timeout = null,
    bool DestroyActorAfterRun = false);

public sealed record MakerRunStarted(
    string ActorId,
    string WorkflowName,
    string CorrelationId,
    DateTimeOffset StartedAt);

public sealed record MakerRunExecutionResult(
    MakerRunStarted Started,
    string Output,
    bool Success,
    bool TimedOut,
    string? Error);
