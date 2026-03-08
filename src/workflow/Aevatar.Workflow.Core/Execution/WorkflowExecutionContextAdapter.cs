using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Execution;

internal sealed class WorkflowExecutionContextAdapter : IWorkflowExecutionContext
{
    private readonly IEventHandlerContext _inner;
    private readonly IWorkflowExecutionStateHost _stateHost;

    private WorkflowExecutionContextAdapter(
        IEventHandlerContext inner,
        IWorkflowExecutionStateHost stateHost)
    {
        _inner = inner;
        _stateHost = stateHost;
    }

    public EventEnvelope InboundEnvelope => _inner.InboundEnvelope;

    public string AgentId => _inner.AgentId;

    public string RunId => _stateHost.RunId;

    public IServiceProvider Services => _inner.Services;

    public ILogger Logger => _inner.Logger;

    public static WorkflowExecutionContextAdapter Create(
        IEventHandlerContext context,
        IWorkflowExecutionStateHost stateHost)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(stateHost);
        return new WorkflowExecutionContextAdapter(context, stateHost);
    }

    public TState LoadState<TState>(string scopeKey)
        where TState : class, IMessage<TState>, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        var packed = _stateHost.GetExecutionState(scopeKey);
        var descriptor = new TState().Descriptor;
        if (packed == null || !packed.Is(descriptor))
            return new TState();

        return packed.Unpack<TState>() ?? new TState();
    }

    public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
        where TState : class, IMessage<TState>, new()
    {
        var prefix = scopeKeyPrefix?.Trim() ?? string.Empty;
        var states = new List<KeyValuePair<string, TState>>();
        foreach (var (scopeKey, packed) in _stateHost.GetExecutionStates())
        {
            if (!string.IsNullOrEmpty(prefix) &&
                !scopeKey.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var descriptor = new TState().Descriptor;
            if (!packed.Is(descriptor))
                continue;

            states.Add(new KeyValuePair<string, TState>(scopeKey, packed.Unpack<TState>() ?? new TState()));
        }

        return states;
    }

    public Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
        where TState : class, IMessage<TState>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        ArgumentNullException.ThrowIfNull(state);
        return _stateHost.UpsertExecutionStateAsync(scopeKey, Any.Pack(state), ct);
    }

    public Task ClearStateAsync(string scopeKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        return _stateHost.ClearExecutionStateAsync(scopeKey, ct);
    }

    public Task PublishAsync<TEvent>(TEvent evt, EventDirection direction = EventDirection.Down, CancellationToken ct = default)
        where TEvent : IMessage =>
        _inner.PublishAsync(evt, direction, ct);

    public Task SendToAsync<TEvent>(string targetActorId, TEvent evt, CancellationToken ct = default)
        where TEvent : IMessage =>
        _inner.SendToAsync(targetActorId, evt, ct);

    public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
        string callbackId,
        TimeSpan dueTime,
        IMessage evt,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default) =>
        _inner.ScheduleSelfDurableTimeoutAsync(callbackId, dueTime, evt, metadata, ct);

    public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
        string callbackId,
        TimeSpan dueTime,
        TimeSpan period,
        IMessage evt,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default) =>
        _inner.ScheduleSelfDurableTimerAsync(callbackId, dueTime, period, evt, metadata, ct);

    public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
        _inner.CancelDurableCallbackAsync(lease, ct);
}
