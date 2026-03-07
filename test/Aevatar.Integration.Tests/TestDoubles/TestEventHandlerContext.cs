using System.Globalization;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Core;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Integration.Tests;

internal sealed class TestEventHandlerContext
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

    public WorkflowPrimitiveExecutionContext CreatePrimitiveContext(
        IReadOnlySet<string>? knownStepTypes = null)
    {
        var stepTypes = knownStepTypes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new WorkflowPrimitiveExecutionContext(
            AgentId,
            Services,
            Logger,
            stepTypes,
            (evt, direction, ct) =>
            {
                Published.Add((evt, direction));
                OnPublish?.Invoke(evt, direction);
                return Task.CompletedTask;
            });
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

internal sealed class TestAgent(string id) : IAgent
{
    public string Id { get; } = id;

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

    public Task<string> GetDescriptionAsync() => Task.FromResult("stub");

    public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
        Task.FromResult<IReadOnlyList<System.Type>>([]);

    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
}
