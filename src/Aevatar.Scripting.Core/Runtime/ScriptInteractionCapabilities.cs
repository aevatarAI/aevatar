using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.AI;
using Google.Protobuf;

namespace Aevatar.Scripting.Core.Runtime;

public sealed class ScriptInteractionCapabilities : IScriptInteractionCapabilities
{
    private readonly string _runId;
    private readonly string _correlationId;
    private readonly IAICapability _aiCapability;
    private readonly ScriptExecutionMessageContext _messageContext;

    public ScriptInteractionCapabilities(
        string runId,
        string correlationId,
        IAICapability aiCapability,
        ScriptExecutionMessageContext messageContext)
    {
        _runId = runId ?? string.Empty;
        _correlationId = correlationId ?? string.Empty;
        _aiCapability = aiCapability ?? throw new ArgumentNullException(nameof(aiCapability));
        _messageContext = messageContext ?? throw new ArgumentNullException(nameof(messageContext));
    }

    public Task<string> AskAIAsync(string prompt, CancellationToken ct) =>
        _aiCapability.AskAsync(_runId, _correlationId, prompt ?? string.Empty, ct);

    public Task PublishAsync(IMessage eventPayload, EventDirection direction, CancellationToken ct) =>
        _messageContext.PublishAsync(eventPayload, direction, ct);

    public Task SendToAsync(string targetActorId, IMessage eventPayload, CancellationToken ct) =>
        _messageContext.SendToAsync(targetActorId, eventPayload, ct);
}
