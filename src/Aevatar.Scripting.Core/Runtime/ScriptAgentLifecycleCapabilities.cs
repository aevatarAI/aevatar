using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Core.Runtime;

public sealed class ScriptAgentLifecycleCapabilities : IScriptAgentLifecycleCapabilities
{
    private readonly IActorRuntime _runtime;

    public ScriptAgentLifecycleCapabilities(IActorRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public async Task<string> CreateAgentAsync(string agentTypeAssemblyQualifiedName, string? actorId, CancellationToken ct)
    {
        var agentType = ResolveRequiredAgentType(agentTypeAssemblyQualifiedName);
        var actor = await _runtime.CreateAsync(agentType, actorId, ct);
        return actor.Id;
    }

    public Task DestroyAgentAsync(string actorId, CancellationToken ct) =>
        _runtime.DestroyAsync(actorId, ct);

    public Task LinkAgentsAsync(string parentActorId, string childActorId, CancellationToken ct) =>
        _runtime.LinkAsync(parentActorId, childActorId, ct);

    public Task UnlinkAgentAsync(string childActorId, CancellationToken ct) =>
        _runtime.UnlinkAsync(childActorId, ct);

    private static Type ResolveRequiredAgentType(string agentTypeAssemblyQualifiedName)
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

        return agentType;
    }
}
