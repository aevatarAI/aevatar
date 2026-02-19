namespace Aevatar.CQRS.Sagas.Abstractions.Actions;

public sealed record SagaEnqueueCommandAction(
    string Target,
    object Command,
    string? CommandId = null,
    string? CorrelationId = null,
    IReadOnlyDictionary<string, string>? Metadata = null) : ISagaAction;

public sealed record SagaScheduleCommandAction(
    string Target,
    object Command,
    TimeSpan Delay,
    string? CommandId = null,
    string? CorrelationId = null,
    IReadOnlyDictionary<string, string>? Metadata = null) : ISagaAction;

public sealed record SagaScheduleTimeoutAction(
    string TimeoutName,
    TimeSpan Delay,
    IReadOnlyDictionary<string, string>? Metadata = null) : ISagaAction;

public sealed record SagaCompleteAction : ISagaAction;
