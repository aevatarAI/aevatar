using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Hosting.Ports;

public sealed class RuntimeGAgentEventRoutingPort : IGAgentEventRoutingPort
{
    private const string PublisherId = "scripting.gagent.routing";

    private readonly IActorRuntime _runtime;

    public RuntimeGAgentEventRoutingPort(IActorRuntime runtime)
    {
        _runtime = runtime;
    }

    public async Task PublishAsync(
        string sourceActorId,
        IMessage eventPayload,
        EventDirection direction,
        string correlationId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceActorId);
        ArgumentNullException.ThrowIfNull(eventPayload);

        var actor = await _runtime.GetAsync(sourceActorId)
            ?? throw new InvalidOperationException($"Source GAgent not found: {sourceActorId}");
        if (actor.Agent is not GAgentBase sourceAgent)
            throw new InvalidOperationException($"Source actor `{sourceActorId}` is not a GAgentBase.");

        var sourceEnvelope = BuildSourceEnvelope(sourceActorId, correlationId);
        await sourceAgent.EventPublisher.PublishAsync(eventPayload, direction, ct, sourceEnvelope);
    }

    public async Task SendToAsync(
        string sourceActorId,
        string targetActorId,
        IMessage eventPayload,
        string correlationId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetActorId);
        ArgumentNullException.ThrowIfNull(eventPayload);

        var actor = await _runtime.GetAsync(sourceActorId)
            ?? throw new InvalidOperationException($"Source GAgent not found: {sourceActorId}");
        if (actor.Agent is not GAgentBase sourceAgent)
            throw new InvalidOperationException($"Source actor `{sourceActorId}` is not a GAgentBase.");

        var sourceEnvelope = BuildSourceEnvelope(sourceActorId, correlationId);
        await sourceAgent.EventPublisher.SendToAsync(targetActorId, eventPayload, ct, sourceEnvelope);
    }

    private static EventEnvelope BuildSourceEnvelope(string sourceActorId, string correlationId)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            PublisherId = string.IsNullOrWhiteSpace(sourceActorId) ? PublisherId : sourceActorId,
            CorrelationId = correlationId ?? string.Empty,
            Direction = EventDirection.Self,
        };
    }
}
