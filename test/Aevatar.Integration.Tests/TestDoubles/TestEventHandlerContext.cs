using System.Globalization;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Execution;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Integration.Tests;

internal sealed class TestEventHandlerContext : IEventHandlerContext, IWorkflowExecutionContext
{
    private readonly Dictionary<string, long> _generations = new(StringComparer.Ordinal);

    public TestEventHandlerContext(IServiceProvider services, IAgent agent, ILogger logger)
    {
        Services = services;
        Agent = agent;
        Logger = logger;
        InboundEnvelope = new EventEnvelope();
    }

    public List<(IMessage evt, EventDirection direction)> Published { get; } = [];
    public List<ScheduledCallback> Scheduled { get; } = [];
    public List<CanceledCallback> Canceled { get; } = [];
    public Action<IMessage, EventDirection>? OnPublish { get; set; }

    public EventEnvelope InboundEnvelope { get; }
    public string AgentId => Agent.Id;
    public IAgent Agent { get; }
    public IServiceProvider Services { get; }
    public ILogger Logger { get; }
    public string RunId => Agent is IWorkflowExecutionStateHost host ? host.RunId : Agent.Id;

    public TState LoadState<TState>(string scopeKey)
        where TState : class, IMessage<TState>, new()
    {
        if (Agent is not IWorkflowExecutionStateHost host)
            return new TState();

        var packed = host.GetExecutionState(scopeKey);
        if (packed == null || !packed.Is(new TState().Descriptor))
            return new TState();

        return packed.Unpack<TState>() ?? new TState();
    }

    public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
        where TState : class, IMessage<TState>, new()
    {
        if (Agent is not IWorkflowExecutionStateHost host)
            return [];

        var states = new List<KeyValuePair<string, TState>>();
        foreach (var (scopeKey, packed) in host.GetExecutionStates())
        {
            if (!string.IsNullOrEmpty(scopeKeyPrefix) &&
                !scopeKey.StartsWith(scopeKeyPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (!packed.Is(new TState().Descriptor))
                continue;

            states.Add(new KeyValuePair<string, TState>(scopeKey, packed.Unpack<TState>() ?? new TState()));
        }

        return states;
    }

    public Task SaveStateAsync<TState>(
        string scopeKey,
        TState state,
        CancellationToken ct = default)
        where TState : class, IMessage<TState>
    {
        if (Agent is not IWorkflowExecutionStateHost host)
            throw new InvalidOperationException("Workflow execution state host is required.");

        return host.UpsertExecutionStateAsync(scopeKey, Any.Pack(state), ct);
    }

    public Task ClearStateAsync(
        string scopeKey,
        CancellationToken ct = default)
    {
        if (Agent is not IWorkflowExecutionStateHost host)
            throw new InvalidOperationException("Workflow execution state host is required.");

        return host.ClearExecutionStateAsync(scopeKey, ct);
    }

    public Task PublishAsync<TEvent>(
        TEvent evt,
        EventDirection direction = EventDirection.Down,
        CancellationToken ct = default)
        where TEvent : IMessage
    {
        Published.Add((evt, direction));
        OnPublish?.Invoke(evt, direction);
        return Task.CompletedTask;
    }

    public Task SendToAsync<TEvent>(
        string targetActorId,
        TEvent evt,
        CancellationToken ct = default)
        where TEvent : IMessage
    {
        _ = targetActorId;
        return PublishAsync(evt, EventDirection.Self, ct);
    }

    public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
        string callbackId,
        TimeSpan dueTime,
        IMessage evt,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        var lease = Schedule(callbackId, evt, dueTime, period: null, metadata);
        return Task.FromResult(lease);
    }

    public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
        string callbackId,
        TimeSpan dueTime,
        TimeSpan period,
        IMessage evt,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        var lease = Schedule(callbackId, evt, dueTime, period, metadata);
        return Task.FromResult(lease);
    }

    public Task CancelDurableCallbackAsync(
        RuntimeCallbackLease lease,
        CancellationToken ct = default)
    {
        _ = ct;
        Canceled.Add(new CanceledCallback(lease));
        return Task.CompletedTask;
    }

    public EventEnvelope CreateScheduledEnvelope(
        ScheduledCallback callback,
        string? publisherId = null)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(callback.Event),
            PublisherId = publisherId ?? AgentId,
            Direction = EventDirection.Self,
        };

        foreach (var pair in callback.Metadata)
            envelope.Metadata[pair.Key] = pair.Value;

        envelope.Metadata[RuntimeCallbackMetadataKeys.CallbackId] = callback.CallbackId;
        envelope.Metadata[RuntimeCallbackMetadataKeys.CallbackGeneration] =
            callback.Generation.ToString(CultureInfo.InvariantCulture);
        envelope.Metadata[RuntimeCallbackMetadataKeys.CallbackFireIndex] = "0";
        envelope.Metadata[RuntimeCallbackMetadataKeys.CallbackFiredAtUnixTimeMs] =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        return envelope;
    }

    private RuntimeCallbackLease Schedule(
        string callbackId,
        IMessage evt,
        TimeSpan dueTime,
        TimeSpan? period,
        IReadOnlyDictionary<string, string>? metadata)
    {
        var generation = _generations.GetValueOrDefault(callbackId, 0) + 1;
        _generations[callbackId] = generation;

        var copiedMetadata = metadata is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(metadata, StringComparer.Ordinal);

        Scheduled.Add(new ScheduledCallback(
            callbackId,
            generation,
            evt,
            dueTime,
            period,
            copiedMetadata));

        return new RuntimeCallbackLease(AgentId, callbackId, generation, RuntimeCallbackBackend.InMemory);
    }
}

internal sealed record ScheduledCallback(
    string CallbackId,
    long Generation,
    IMessage Event,
    TimeSpan DueTime,
    TimeSpan? Period,
    IReadOnlyDictionary<string, string> Metadata);

internal sealed record CanceledCallback(
    RuntimeCallbackLease Lease)
{
    public string CallbackId => Lease.CallbackId;

    public long ExpectedGeneration => Lease.Generation;
}

internal sealed class TestAgent(string id, string? runId = null) : IAgent, IWorkflowExecutionStateHost
{
    private readonly Dictionary<string, Any> _executionStates = new(StringComparer.Ordinal);

    public string Id { get; } = id;

    public string RunId { get; } = string.IsNullOrWhiteSpace(runId) ? id : runId;

    public Any? GetExecutionState(string scopeKey) =>
        _executionStates.TryGetValue(scopeKey, out var state) ? state : null;

    public IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates() =>
        _executionStates.ToList();

    public Task UpsertExecutionStateAsync(
        string scopeKey,
        Any state,
        CancellationToken ct = default)
    {
        _ = ct;
        _executionStates[scopeKey] = state;
        return Task.CompletedTask;
    }

    public Task ClearExecutionStateAsync(
        string scopeKey,
        CancellationToken ct = default)
    {
        _ = ct;
        _executionStates.Remove(scopeKey);
        return Task.CompletedTask;
    }

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

    public Task<string> GetDescriptionAsync() => Task.FromResult("stub");

    public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
        Task.FromResult<IReadOnlyList<System.Type>>([]);

    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class TestWorkflowRunAgent(string id, string runId) : IAgent, IWorkflowExecutionStateHost
{
    private readonly Dictionary<string, Any> _executionStates = new(StringComparer.Ordinal);

    public string Id { get; } = id;

    public string RunId { get; } = runId;

    public Any? GetExecutionState(string scopeKey) =>
        _executionStates.TryGetValue(scopeKey, out var state) ? state : null;

    public IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates() =>
        _executionStates.ToList();

    public Task UpsertExecutionStateAsync(
        string scopeKey,
        Any state,
        CancellationToken ct = default)
    {
        _ = ct;
        _executionStates[scopeKey] = state;
        return Task.CompletedTask;
    }

    public Task ClearExecutionStateAsync(
        string scopeKey,
        CancellationToken ct = default)
    {
        _ = ct;
        _executionStates.Remove(scopeKey);
        return Task.CompletedTask;
    }

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

    public Task<string> GetDescriptionAsync() => Task.FromResult("stub-workflow-run");

    public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
        Task.FromResult<IReadOnlyList<System.Type>>([]);

    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
}
