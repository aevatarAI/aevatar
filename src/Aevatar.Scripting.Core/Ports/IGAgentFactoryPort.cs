namespace Aevatar.Scripting.Core.Ports;

public interface IGAgentFactoryPort
{
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
