using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.AI;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf;

namespace Aevatar.Scripting.Core.Runtime;

public sealed class ScriptRuntimeCapabilities : IScriptRuntimeCapabilities
{
    private readonly string _runtimeActorId;
    private readonly string _runId;
    private readonly string _correlationId;
    private readonly IAICapability _aiCapability;
    private readonly IGAgentEventRoutingPort _eventRoutingPort;
    private readonly IGAgentInvocationPort _invocationPort;
    private readonly IGAgentFactoryPort _factoryPort;

    public ScriptRuntimeCapabilities(
        string runtimeActorId,
        string runId,
        string correlationId,
        IAICapability aiCapability,
        IGAgentEventRoutingPort eventRoutingPort,
        IGAgentInvocationPort invocationPort,
        IGAgentFactoryPort factoryPort)
    {
        _runtimeActorId = runtimeActorId ?? string.Empty;
        _runId = runId ?? string.Empty;
        _correlationId = correlationId ?? string.Empty;
        _aiCapability = aiCapability ?? throw new ArgumentNullException(nameof(aiCapability));
        _eventRoutingPort = eventRoutingPort ?? throw new ArgumentNullException(nameof(eventRoutingPort));
        _invocationPort = invocationPort ?? throw new ArgumentNullException(nameof(invocationPort));
        _factoryPort = factoryPort ?? throw new ArgumentNullException(nameof(factoryPort));
    }

    public Task<string> AskAIAsync(string prompt, CancellationToken ct)
    {
        return _aiCapability.AskAsync(_runId, _correlationId, prompt ?? string.Empty, ct);
    }

    public Task PublishAsync(IMessage eventPayload, EventDirection direction, CancellationToken ct)
    {
        return _eventRoutingPort.PublishAsync(_runtimeActorId, eventPayload, direction, _correlationId, ct);
    }

    public Task SendToAsync(string targetActorId, IMessage eventPayload, CancellationToken ct)
    {
        return _eventRoutingPort.SendToAsync(_runtimeActorId, targetActorId, eventPayload, _correlationId, ct);
    }

    public Task InvokeAgentAsync(string targetAgentId, IMessage eventPayload, CancellationToken ct)
    {
        return _invocationPort.InvokeAsync(targetAgentId, eventPayload, _correlationId, ct);
    }

    public Task<string> CreateAgentAsync(string agentTypeAssemblyQualifiedName, string? actorId, CancellationToken ct)
    {
        return _factoryPort.CreateAsync(agentTypeAssemblyQualifiedName, actorId, ct);
    }

    public Task DestroyAgentAsync(string actorId, CancellationToken ct)
    {
        return _factoryPort.DestroyAsync(actorId, ct);
    }

    public Task LinkAgentsAsync(string parentActorId, string childActorId, CancellationToken ct)
    {
        return _factoryPort.LinkAsync(parentActorId, childActorId, ct);
    }

    public Task UnlinkAgentAsync(string childActorId, CancellationToken ct)
    {
        return _factoryPort.UnlinkAsync(childActorId, ct);
    }
}
