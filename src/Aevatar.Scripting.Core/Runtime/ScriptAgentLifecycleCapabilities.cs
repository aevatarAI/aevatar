using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf;

namespace Aevatar.Scripting.Core.Runtime;

public sealed class ScriptAgentLifecycleCapabilities : IScriptAgentLifecycleCapabilities
{
    private readonly string _correlationId;
    private readonly IGAgentRuntimePort _agentRuntimePort;

    public ScriptAgentLifecycleCapabilities(
        string correlationId,
        IGAgentRuntimePort agentRuntimePort)
    {
        _correlationId = correlationId ?? string.Empty;
        _agentRuntimePort = agentRuntimePort ?? throw new ArgumentNullException(nameof(agentRuntimePort));
    }

    public Task InvokeAgentAsync(string targetAgentId, IMessage eventPayload, CancellationToken ct) =>
        _agentRuntimePort.InvokeAsync(targetAgentId, eventPayload, _correlationId, ct);

    public Task<string> CreateAgentAsync(string agentTypeAssemblyQualifiedName, string? actorId, CancellationToken ct) =>
        _agentRuntimePort.CreateAsync(agentTypeAssemblyQualifiedName, actorId, ct);

    public Task DestroyAgentAsync(string actorId, CancellationToken ct) =>
        _agentRuntimePort.DestroyAsync(actorId, ct);

    public Task LinkAgentsAsync(string parentActorId, string childActorId, CancellationToken ct) =>
        _agentRuntimePort.LinkAsync(parentActorId, childActorId, ct);

    public Task UnlinkAgentAsync(string childActorId, CancellationToken ct) =>
        _agentRuntimePort.UnlinkAsync(childActorId, ct);
}
