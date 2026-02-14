// LocalActor - IActor implementation.
// Focuses on two responsibilities: mailbox serialization and stream subscription management.

using System.Diagnostics;
using System.Text.Json;
using Aevatar.Routing;
using Aevatar.Observability;
using Microsoft.Extensions.Logging;

namespace Aevatar.Actor;

public sealed class LocalActor : IActor
{
    private readonly SemaphoreSlim _mailbox = new(1, 1);
    private readonly EventRouter _router;
    private readonly IStreamProvider _streams;
    private readonly ILogger _logger;
    private IAsyncDisposable? _parentSubscription;
    private IAsyncDisposable? _selfSubscription;

    public LocalActor(IAgent agent, string id, EventRouter router, IStreamProvider streams, ILogger logger)
    {
        Agent = agent; Id = id; _router = router; _streams = streams; _logger = logger;
    }

    public string Id { get; }

    /// <summary>
    /// Agent instance. Available on LocalActor but NOT on IActor interface —
    /// use IActor.GetDescriptionAsync / ConfigureAsync etc. for runtime-agnostic code.
    /// </summary>
    public IAgent Agent { get; }

    internal EventRouter Router => _router;

    public async Task ActivateAsync(CancellationToken ct = default)
    {
        // Subscribe to self stream (handle Self events and Up events from children).
        var selfStream = _streams.GetStream(Id);
        _selfSubscription = await selfStream.SubscribeAsync<EventEnvelope>(async envelope =>
        {
            // Handle Self events directly.
            if (envelope.Direction == EventDirection.Self)
            {
                await EnqueueAsync(envelope);
                return;
            }

            // Handle Up events from children (they produce to parent's stream).
            if (envelope.Direction == EventDirection.Up)
            {
                await EnqueueAsync(envelope);
                return;
            }
            // Down events are not handled on self-stream (child actors handle them).
        }, ct);

        await Agent.ActivateAsync(ct);
    }

    public async Task DeactivateAsync(CancellationToken ct = default)
    {
        if (_parentSubscription != null) { await _parentSubscription.DisposeAsync(); _parentSubscription = null; }
        if (_selfSubscription != null) { await _selfSubscription.DisposeAsync(); _selfSubscription = null; }
        await Agent.DeactivateAsync(ct);
    }

    public Task<string> GetDescriptionAsync() => Agent.GetDescriptionAsync();

    public Task<string> GetAgentTypeNameAsync() =>
        Task.FromResult(Agent.GetType().Name);

    public Task ConfigureAsync(string configJson, CancellationToken ct = default)
    {
        // Walk the type hierarchy to find GAgentBase<TState, TConfig>
        var agentType = Agent.GetType();
        var current = agentType;
        while (current != null)
        {
            if (current.IsGenericType)
            {
                var genDef = current.GetGenericTypeDefinition();
                // Match GAgentBase<TState, TConfig> (3 generic params at the open type)
                if (genDef.GetGenericArguments().Length == 2 &&
                    genDef.FullName?.Contains("GAgentBase") == true)
                {
                    var configType = current.GetGenericArguments()[1];
                    var config = JsonSerializer.Deserialize(configJson, configType);
                    if (config != null)
                    {
                        var method = current.GetMethod("ConfigureAsync",
                            [configType, typeof(CancellationToken)]);
                        if (method != null)
                            return (Task)method.Invoke(Agent, [config, ct])!;
                    }
                    break;
                }
            }
            current = current.BaseType;
        }

        _logger.LogWarning("Agent {Id} ({Type}) does not support ConfigureAsync",
            Id, agentType.Name);
        return Task.CompletedTask;
    }

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
        EnqueueAsync(envelope);

    public Task<string?> GetParentIdAsync() => Task.FromResult(_router.ParentId);
    public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
        Task.FromResult<IReadOnlyList<string>>(_router.ChildrenIds.ToList());

    // Hierarchy operations (called by LocalActorRuntime)

    internal void AddChild(string childId) => _router.AddChild(childId);
    internal void RemoveChild(string childId) => _router.RemoveChild(childId);

    internal async Task SubscribeToParentAsync(string parentId, CancellationToken ct)
    {
        if (_parentSubscription != null) { await _parentSubscription.DisposeAsync(); _parentSubscription = null; }
        _router.SetParent(parentId);

        var parentStream = _streams.GetStream(parentId);
        _parentSubscription = await parentStream.SubscribeAsync<EventEnvelope>(async envelope =>
        {
            // Filter: Self and Up events are not propagated to children.
            if (envelope.Direction is EventDirection.Up or EventDirection.Self) return;
            if (envelope.Metadata.TryGetValue("__publishers", out var pubs) && pubs.Contains(Id)) return;

            await EnqueueAsync(envelope);

            // Propagate downward to own children.
            if (envelope.Direction is EventDirection.Down or EventDirection.Both && _router.ChildrenIds.Count > 0)
            {
                var down = envelope.Clone();
                down.Direction = EventDirection.Down;
                var p = down.Metadata.GetValueOrDefault("__publishers", "");
                down.Metadata["__publishers"] = string.IsNullOrEmpty(p) ? Id : $"{p},{Id}";
                await _streams.GetStream(Id).ProduceAsync(down, CancellationToken.None);
            }
        }, ct);
    }

    internal async Task UnsubscribeFromParentAsync()
    {
        if (_parentSubscription != null) { await _parentSubscription.DisposeAsync(); _parentSubscription = null; }
        _router.ClearParent();
    }

    // ─── Mailbox ───

    private async Task EnqueueAsync(EventEnvelope envelope)
    {
        using var activity = AevatarActivitySource.StartHandleEvent(Id, envelope.Id);
        activity?.SetTag("aevatar.event.direction", envelope.Direction.ToString());
        activity?.SetTag("aevatar.event.type", envelope.Payload?.TypeUrl);
        activity?.SetTag("aevatar.event.publisher", envelope.PublisherId);

        var sw = Stopwatch.StartNew();
        var status = "ok";
        await _mailbox.WaitAsync();
        try
        {
            await Agent.HandleEventAsync(envelope);
        }
        catch (Exception ex)
        {
            status = "error";
            activity?.SetTag("aevatar.error", true);
            activity?.SetTag("aevatar.error.message", ex.Message);
            _logger.LogError(ex, "LocalActor {Id} failed to handle event", Id);
        }
        finally
        {
            _mailbox.Release();
            sw.Stop();
            AgentMetrics.EventsHandled.Add(1,
            [
                new("agent.id", Id),
                new("event.direction", envelope.Direction.ToString()),
                new("event.type", envelope.Payload?.TypeUrl ?? "unknown"),
                new("status", status),
            ]);
            AgentMetrics.EventHandleDuration.Record(sw.Elapsed.TotalMilliseconds,
            [
                new("agent.id", Id),
                new("event.direction", envelope.Direction.ToString()),
                new("status", status),
            ]);
        }
    }
}
