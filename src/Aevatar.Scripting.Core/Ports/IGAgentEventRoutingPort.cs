using Aevatar.Foundation.Abstractions;
using Google.Protobuf;

namespace Aevatar.Scripting.Core.Ports;

public interface IGAgentEventRoutingPort
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
}
