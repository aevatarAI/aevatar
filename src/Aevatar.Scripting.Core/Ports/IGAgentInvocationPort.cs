using Google.Protobuf;

namespace Aevatar.Scripting.Core.Ports;

public interface IGAgentInvocationPort
{
    Task InvokeAsync(
        string targetAgentId,
        IMessage eventPayload,
        string correlationId,
        CancellationToken ct);
}
