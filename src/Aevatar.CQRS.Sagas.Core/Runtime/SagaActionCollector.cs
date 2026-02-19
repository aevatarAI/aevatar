using Aevatar.CQRS.Sagas.Abstractions.Actions;
using Aevatar.CQRS.Sagas.Abstractions.Runtime;

namespace Aevatar.CQRS.Sagas.Core.Runtime;

internal sealed class SagaActionCollector : ISagaActionSink
{
    private readonly int _maxActions;
    private readonly List<ISagaAction> _actions = [];

    public SagaActionCollector(int maxActions)
    {
        _maxActions = Math.Max(maxActions, 1);
    }

    public IReadOnlyList<ISagaAction> Actions => _actions;

    public bool ShouldComplete { get; private set; }

    public void EnqueueCommand(
        string target,
        object command,
        string? commandId = null,
        string? correlationId = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(command);

        AddAction(new SagaEnqueueCommandAction(target, command, commandId, correlationId, metadata));
    }

    public void ScheduleCommand(
        string target,
        object command,
        TimeSpan delay,
        string? commandId = null,
        string? correlationId = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(command);

        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;

        AddAction(new SagaScheduleCommandAction(target, command, delay, commandId, correlationId, metadata));
    }

    public void ScheduleTimeout(
        string timeoutName,
        TimeSpan delay,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeoutName);

        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;

        AddAction(new SagaScheduleTimeoutAction(timeoutName, delay, metadata));
    }

    public void MarkCompleted()
    {
        ShouldComplete = true;
        AddAction(new SagaCompleteAction());
    }

    private void AddAction(ISagaAction action)
    {
        if (_actions.Count >= _maxActions)
            throw new InvalidOperationException($"Saga action limit exceeded (max={_maxActions}).");

        _actions.Add(action);
    }
}
