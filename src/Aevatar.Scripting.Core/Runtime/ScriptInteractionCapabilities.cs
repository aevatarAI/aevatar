using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.AI;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf;

namespace Aevatar.Scripting.Core.Runtime;

public sealed class ScriptInteractionCapabilities : IScriptInteractionCapabilities
{
    private readonly string _runtimeActorId;
    private readonly string _runId;
    private readonly string _correlationId;
    private readonly IAICapability _aiCapability;
    private readonly IGAgentRuntimePort _agentRuntimePort;

    public ScriptInteractionCapabilities(
        string runtimeActorId,
        string runId,
        string correlationId,
        IAICapability aiCapability,
        IGAgentRuntimePort agentRuntimePort)
    {
        _runtimeActorId = runtimeActorId ?? string.Empty;
        _runId = runId ?? string.Empty;
        _correlationId = correlationId ?? string.Empty;
        _aiCapability = aiCapability ?? throw new ArgumentNullException(nameof(aiCapability));
        _agentRuntimePort = agentRuntimePort ?? throw new ArgumentNullException(nameof(agentRuntimePort));
    }

    public Task<string> AskAIAsync(string prompt, CancellationToken ct) =>
        _aiCapability.AskAsync(_runId, _correlationId, prompt ?? string.Empty, ct);

    public Task PublishAsync(IMessage eventPayload, EventDirection direction, CancellationToken ct) =>
        _agentRuntimePort.PublishAsync(_runtimeActorId, eventPayload, direction, _correlationId, ct);

    public Task SendToAsync(string targetActorId, IMessage eventPayload, CancellationToken ct) =>
        _agentRuntimePort.SendToAsync(_runtimeActorId, targetActorId, eventPayload, _correlationId, ct);
}
