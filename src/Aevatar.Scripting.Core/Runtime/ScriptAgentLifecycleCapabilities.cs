using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf;

namespace Aevatar.Scripting.Core.Runtime;

public sealed class ScriptAgentLifecycleCapabilities : IScriptAgentLifecycleCapabilities
{
    private readonly string _runtimeActorId;
    private readonly string _correlationId;
    private readonly IGAgentInvocationPort _invocationPort;
    private readonly IGAgentFactoryPort _factoryPort;

    public ScriptAgentLifecycleCapabilities(
        string runtimeActorId,
        string correlationId,
        IGAgentInvocationPort invocationPort,
        IGAgentFactoryPort factoryPort)
    {
        _runtimeActorId = runtimeActorId ?? string.Empty;
        _correlationId = correlationId ?? string.Empty;
        _invocationPort = invocationPort ?? throw new ArgumentNullException(nameof(invocationPort));
        _factoryPort = factoryPort ?? throw new ArgumentNullException(nameof(factoryPort));
    }

    public Task InvokeAgentAsync(string targetAgentId, IMessage eventPayload, CancellationToken ct) =>
        _invocationPort.InvokeAsync(targetAgentId, eventPayload, _correlationId, ct);

    public Task<string> CreateAgentAsync(string agentTypeAssemblyQualifiedName, string? actorId, CancellationToken ct) =>
        _factoryPort.CreateAsync(agentTypeAssemblyQualifiedName, actorId, ct);

    public Task DestroyAgentAsync(string actorId, CancellationToken ct) =>
        _factoryPort.DestroyAsync(actorId, ct);

    public Task LinkAgentsAsync(string parentActorId, string childActorId, CancellationToken ct) =>
        _factoryPort.LinkAsync(parentActorId, childActorId, ct);

    public Task UnlinkAgentAsync(string childActorId, CancellationToken ct) =>
        _factoryPort.UnlinkAsync(childActorId, ct);
}
