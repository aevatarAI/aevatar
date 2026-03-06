using Aevatar.Foundation.Abstractions;
using Google.Protobuf;

namespace Aevatar.Scripting.Abstractions.Definitions;

public interface IScriptInteractionCapabilities
{
    Task<string> AskAIAsync(
        string prompt,
        CancellationToken ct);

    Task PublishAsync(
        IMessage eventPayload,
        EventDirection direction,
        CancellationToken ct);

    Task SendToAsync(
        string targetActorId,
        IMessage eventPayload,
        CancellationToken ct);
}
