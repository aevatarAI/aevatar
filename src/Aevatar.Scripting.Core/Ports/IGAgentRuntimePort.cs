using Aevatar.Foundation.Abstractions;
using Google.Protobuf;

namespace Aevatar.Scripting.Core.Ports;

public interface IGAgentRuntimePort
{
    Task PublishAsync(
        string sourceActorId,
        IMessage eventPayload,
        EventDirection direction,
        string correlationId,
        CancellationToken ct);

    Task SendToAsync(
        string sourceActorId,
        string targetActorId,
        IMessage eventPayload,
        string correlationId,
        CancellationToken ct);

    Task InvokeAsync(
        string targetAgentId,
        IMessage eventPayload,
        string correlationId,
        CancellationToken ct);

    Task<string> CreateAsync(
        string agentTypeAssemblyQualifiedName,
        string? actorId,
        CancellationToken ct);

    Task DestroyAsync(
        string actorId,
        CancellationToken ct);

    Task LinkAsync(
        string parentActorId,
        string childActorId,
        CancellationToken ct);

    Task UnlinkAsync(
        string childActorId,
        CancellationToken ct);
}
