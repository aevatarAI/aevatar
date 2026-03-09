using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeGAgentRuntimePort : IGAgentRuntimePort
{
    private const string InvocationPublisherId = "scripting.gagent.invocation";
    private const string RoutingPublisherId = "scripting.gagent.routing";

    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;

    public RuntimeGAgentRuntimePort(IActorRuntime runtime, IActorDispatchPort dispatchPort)
    {
        _runtime = runtime;
        _dispatchPort = dispatchPort;
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

        var sourceAgent = await GetRequiredSourceAgentAsync(sourceActorId);
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

        var sourceAgent = await GetRequiredSourceAgentAsync(sourceActorId);
        var sourceEnvelope = BuildSourceEnvelope(sourceActorId, correlationId);
        await sourceAgent.EventPublisher.SendToAsync(targetActorId, eventPayload, ct, sourceEnvelope);
    }

    public async Task InvokeAsync(
        string targetAgentId,
        IMessage eventPayload,
        string correlationId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetAgentId);
        ArgumentNullException.ThrowIfNull(eventPayload);

        if (!await _runtime.ExistsAsync(targetAgentId))
            throw new InvalidOperationException($"Target GAgent not found: {targetAgentId}");

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(eventPayload),
            PublisherId = InvocationPublisherId,
            Direction = EventDirection.Self,
            TargetActorId = targetAgentId,
            CorrelationId = correlationId ?? string.Empty,
        };

        await _dispatchPort.DispatchAsync(targetAgentId, envelope, ct);
    }

    public async Task<string> CreateAsync(
        string agentTypeAssemblyQualifiedName,
        string? actorId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentTypeAssemblyQualifiedName);

        var agentType = global::System.Type.GetType(
            agentTypeAssemblyQualifiedName,
            throwOnError: false,
            ignoreCase: false);
        if (agentType == null)
            throw new InvalidOperationException(
                $"Unable to resolve GAgent type: {agentTypeAssemblyQualifiedName}");
        if (!typeof(IAgent).IsAssignableFrom(agentType))
            throw new InvalidOperationException(
                $"Resolved type does not implement IAgent: {agentTypeAssemblyQualifiedName}");

        var actor = await _runtime.CreateAsync(agentType, actorId, ct);
        return actor.Id;
    }

    public Task DestroyAsync(string actorId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        return _runtime.DestroyAsync(actorId, ct);
    }

    public Task LinkAsync(string parentActorId, string childActorId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(childActorId);
        return _runtime.LinkAsync(parentActorId, childActorId, ct);
    }

    public Task UnlinkAsync(string childActorId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childActorId);
        return _runtime.UnlinkAsync(childActorId, ct);
    }

    private async Task<GAgentBase> GetRequiredSourceAgentAsync(string sourceActorId)
    {
        var actor = await _runtime.GetAsync(sourceActorId)
            ?? throw new InvalidOperationException($"Source GAgent not found: {sourceActorId}");
        if (actor.Agent is not GAgentBase sourceAgent)
            throw new InvalidOperationException($"Source actor `{sourceActorId}` is not a GAgentBase.");

        return sourceAgent;
    }

    private static EventEnvelope BuildSourceEnvelope(string sourceActorId, string correlationId)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            PublisherId = string.IsNullOrWhiteSpace(sourceActorId) ? RoutingPublisherId : sourceActorId,
            CorrelationId = correlationId ?? string.Empty,
            Direction = EventDirection.Self,
        };
    }
}
