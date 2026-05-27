using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions.Execution;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Integration.AI.Tests;

internal sealed class TestWorkflowExecutionContext : IWorkflowExecutionContext
{
    private readonly Dictionary<string, IMessage> _states = new(StringComparer.Ordinal);

    public EventEnvelope InboundEnvelope { get; } = new();

    public string AgentId { get; init; } = "workflow-run-1";

    public IServiceProvider Services { get; init; } = new ServiceCollection().BuildServiceProvider();

    public ILogger Logger { get; init; } = NullLogger.Instance;

    public string RunId { get; init; } = "run-1";

    public List<(IMessage Event, TopologyAudience Audience)> Published { get; } = [];

    public List<(string TargetActorId, IMessage Event)> Sent { get; } = [];

    public Task PublishAsync<TEvent>(
        TEvent evt,
        TopologyAudience audience = TopologyAudience.Children,
        CancellationToken ct = default,
        EventEnvelopePublishOptions? options = null)
        where TEvent : IMessage
    {
        _ = ct;
        _ = options;
        Published.Add((evt, audience));
        return Task.CompletedTask;
    }

    public Task SendToAsync<TEvent>(
        string targetActorId,
        TEvent evt,
        CancellationToken ct = default,
        EventEnvelopePublishOptions? options = null)
        where TEvent : IMessage
    {
        _ = ct;
        _ = options;
        Sent.Add((targetActorId, evt));
        return Task.CompletedTask;
    }

    public TState LoadState<TState>(string scopeKey)
        where TState : class, IMessage<TState>, new() =>
        _states.TryGetValue(scopeKey, out var state) && state is TState typed
            ? typed.Clone()
            : new TState();

    public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
        where TState : class, IMessage<TState>, new() =>
        _states
            .Where(x => string.IsNullOrEmpty(scopeKeyPrefix) || x.Key.StartsWith(scopeKeyPrefix, StringComparison.Ordinal))
            .Where(x => x.Value is TState)
            .Select(x => new KeyValuePair<string, TState>(x.Key, ((TState)x.Value).Clone()))
            .ToList();

    public Task SaveStateAsync<TState>(
        string scopeKey,
        TState state,
        CancellationToken ct = default)
        where TState : class, IMessage<TState>
    {
        _ = ct;
        _states[scopeKey] = state.Clone();
        return Task.CompletedTask;
    }

    public Task ClearStateAsync(string scopeKey, CancellationToken ct = default)
    {
        _ = ct;
        _states.Remove(scopeKey);
        return Task.CompletedTask;
    }

    public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
        string callbackId,
        TimeSpan dueTime,
        IMessage evt,
        EventEnvelopePublishOptions? options = null,
        CancellationToken ct = default)
    {
        _ = dueTime;
        _ = evt;
        _ = options;
        _ = ct;
        return Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, 1, RuntimeCallbackBackend.InMemory));
    }

    public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
        string callbackId,
        TimeSpan dueTime,
        TimeSpan period,
        IMessage evt,
        EventEnvelopePublishOptions? options = null,
        CancellationToken ct = default)
    {
        _ = dueTime;
        _ = period;
        _ = evt;
        _ = options;
        _ = ct;
        return Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, 1, RuntimeCallbackBackend.InMemory));
    }

    public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
    {
        _ = lease;
        _ = ct;
        return Task.CompletedTask;
    }
}
