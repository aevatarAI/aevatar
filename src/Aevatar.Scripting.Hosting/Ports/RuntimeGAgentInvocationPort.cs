using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Hosting.Ports;

public sealed class RuntimeGAgentInvocationPort : IGAgentInvocationPort
{
    private const string PublisherId = "scripting.gagent.invocation";

    private readonly IActorRuntime _runtime;

    public RuntimeGAgentInvocationPort(IActorRuntime runtime)
    {
        _runtime = runtime;
    }

    public async Task InvokeAsync(
        string targetAgentId,
        IMessage eventPayload,
        string correlationId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetAgentId);
        ArgumentNullException.ThrowIfNull(eventPayload);

        var actor = await _runtime.GetAsync(targetAgentId);
        if (actor == null)
            throw new InvalidOperationException($"Target GAgent not found: {targetAgentId}");

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(eventPayload),
            PublisherId = PublisherId,
            Direction = EventDirection.Self,
            TargetActorId = targetAgentId,
            CorrelationId = correlationId ?? string.Empty,
        };

        await actor.HandleEventAsync(envelope, ct);
    }
}
