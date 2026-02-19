namespace Aevatar.CQRS.Sagas.Abstractions.Runtime;

public interface ISagaActionSink
{
    void EnqueueCommand(
        string target,
        object command,
        string? commandId = null,
        string? correlationId = null,
        IReadOnlyDictionary<string, string>? metadata = null);

    void ScheduleCommand(
        string target,
        object command,
        TimeSpan delay,
        string? commandId = null,
        string? correlationId = null,
        IReadOnlyDictionary<string, string>? metadata = null);

    void ScheduleTimeout(
        string timeoutName,
        TimeSpan delay,
        IReadOnlyDictionary<string, string>? metadata = null);

    void MarkCompleted();
}
