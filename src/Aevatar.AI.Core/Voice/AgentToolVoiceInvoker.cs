using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.Core.Voice;

/// <summary>
/// Executes only the exact tool object from a voice catalog rematerialized against the session's
/// pinned proof. There is no process-wide name index or unrestricted fallback.
/// </summary>
public sealed class AgentToolVoiceInvoker : IVoiceToolInvoker
{
    private readonly IAgentToolExecutionPort _toolExecutionPort;
    private readonly VoiceAgentTurnToolCatalogMaterializer _materializer;

    public AgentToolVoiceInvoker(
        VoiceAgentTurnToolCatalogMaterializer materializer,
        IAgentToolExecutionPort toolExecutionPort)
    {
        _materializer = materializer ?? throw new ArgumentNullException(nameof(materializer));
        _toolExecutionPort = toolExecutionPort ?? throw new ArgumentNullException(nameof(toolExecutionPort));
    }

    public AgentToolVoiceInvoker(
        IEnumerable<IAgentToolSource> toolSources,
        IAgentToolExecutionPort toolExecutionPort,
        ICredentialProvider? credentialProvider = null,
        ILogger<AgentToolVoiceInvoker>? logger = null)
        : this(
            toolSources,
            toolExecutionPort,
            credentialProvider is null ? [] : [credentialProvider],
            logger)
    {
    }

    public AgentToolVoiceInvoker(
        IEnumerable<IAgentToolSource> toolSources,
        IAgentToolExecutionPort toolExecutionPort,
        IEnumerable<ICredentialProvider> credentialProviders,
        ILogger<AgentToolVoiceInvoker>? logger = null,
        IAgentToolDiscoveryService? toolDiscoveryService = null)
        : this(
            new VoiceAgentTurnToolCatalogMaterializer(
                toolSources,
                credentialProviders,
                toolDiscoveryService,
                logger: null),
            toolExecutionPort)
    {
        _ = logger;
    }

    public async Task<string> ExecuteAsync(
        string ownerActorId,
        string sessionId,
        string callId,
        long issuedAtUnixMs,
        string toolName,
        string argumentsJson,
        VoiceToolExecutionContext? toolContext = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerActorId))
            throw new ArgumentException("Owner actor id is required.", nameof(ownerActorId));
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Voice session id is required.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(callId))
            throw new ArgumentException("Provider call id is required.", nameof(callId));
        if (issuedAtUnixMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(issuedAtUnixMs));
        if (string.IsNullOrWhiteSpace(toolName))
            throw new ArgumentException("Tool name is required.", nameof(toolName));

        var materialized = await _materializer
            .MaterializeForExecutionAsync(toolContext, ct)
            .ConfigureAwait(false);
        if (!materialized.Catalog.ExactTools.TryGetValue(toolName.Trim(), out var tool))
            throw new InvalidOperationException($"Tool '{toolName}' not found");

        var normalizedOwnerActorId = ownerActorId.Trim();
        var normalizedSessionId = sessionId.Trim();
        var normalizedCallId = callId.Trim();
        var requestId = CreateRequestId(normalizedSessionId, normalizedCallId);
        var outcome = await _toolExecutionPort.ExecuteAsync(
            new AgentToolExecutionRequest(
                tool,
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson,
                materialized.ExecutionContext with
                {
                    Request = new AgentToolRequestIdentity(
                        requestId,
                        normalizedCallId,
                        requestId,
                        issuedAtUnixMs),
                    ExecutionOwner = AgentToolExecutionOwners.Actor(normalizedOwnerActorId),
                },
                AgentToolApprovalContinuationMode.None,
                null),
            ct).ConfigureAwait(false);
        return outcome.ResultJson;
    }

    private static string CreateRequestId(string sessionId, string callId) =>
        $"voice:v1:{Uri.EscapeDataString(sessionId)}:{Uri.EscapeDataString(callId)}";
}
