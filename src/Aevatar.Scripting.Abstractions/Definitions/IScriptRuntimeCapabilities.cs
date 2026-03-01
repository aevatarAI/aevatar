using Google.Protobuf;

namespace Aevatar.Scripting.Abstractions.Definitions;

public interface IScriptRuntimeCapabilities
{
    Task<string> AskAIAsync(
        string prompt,
        CancellationToken ct);

    Task InvokeAgentAsync(
        string targetAgentId,
        IMessage eventPayload,
        CancellationToken ct);

    Task<string> CreateAgentAsync(
        string agentTypeAssemblyQualifiedName,
        string? actorId,
        CancellationToken ct);

    Task DestroyAgentAsync(
        string actorId,
        CancellationToken ct);

    Task LinkAgentsAsync(
        string parentActorId,
        string childActorId,
        CancellationToken ct);

    Task UnlinkAgentAsync(
        string childActorId,
        CancellationToken ct);
}
