using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Hosting.Ports;

public sealed class RuntimeGAgentFactoryPort : IGAgentFactoryPort
{
    private readonly IActorRuntime _runtime;

    public RuntimeGAgentFactoryPort(IActorRuntime runtime)
    {
        _runtime = runtime;
    }

    public async Task<string> CreateAsync(
        string agentTypeAssemblyQualifiedName,
        string? actorId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentTypeAssemblyQualifiedName);

        var agentType = Type.GetType(
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
}
