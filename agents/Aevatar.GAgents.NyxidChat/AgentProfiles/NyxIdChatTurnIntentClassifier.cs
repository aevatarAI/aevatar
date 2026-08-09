using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core.AgentProfiles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.NyxidChat.AgentProfiles;

public interface INyxIdChatTurnIntentClassifier
{
    Task<NyxIdChatTurnIntent> ClassifyAsync(
        string requestId,
        string userMessage,
        LLMControlContext? llmControl,
        CancellationToken ct = default);
}

public sealed class NyxIdChatTurnIntentClassifier : INyxIdChatTurnIntentClassifier
{
    internal const string ServiceConnectIntentId = "service_connect";
    internal const string ServiceConnectRoutingDescription =
        "Connect, add, or authorize a hosted external service account and verify that connection.";
    private static readonly TimeSpan ClassificationTimeout = TimeSpan.FromSeconds(15);
    internal static AgentProfileTurnClassificationCandidate ServiceConnectCandidate { get; } =
        new(
            ServiceConnectIntentId,
            ServiceConnectRoutingDescription,
            AgentProfileSideEffectClass.ExternalHandoff);
    private static readonly AgentProfileTurnClassificationCandidate[] Candidates =
    [
        ServiceConnectCandidate,
    ];

    private readonly IAgentProfileTurnClassifier _classifier;
    private readonly ILogger<NyxIdChatTurnIntentClassifier> _logger;

    public NyxIdChatTurnIntentClassifier(
        IAgentProfileTurnClassifier classifier,
        ILogger<NyxIdChatTurnIntentClassifier>? logger = null)
    {
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _logger = logger ?? NullLogger<NyxIdChatTurnIntentClassifier>.Instance;
    }

    public async Task<NyxIdChatTurnIntent> ClassifyAsync(
        string requestId,
        string userMessage,
        LLMControlContext? llmControl,
        CancellationToken ct = default)
    {
        var result = await _classifier.ClassifyAsync(
                new AgentProfileTurnClassificationRequest(
                    userMessage ?? string.Empty,
                    Candidates,
                    ClassificationTimeout,
                    llmControl,
                    requestId),
                ct)
            .ConfigureAwait(false);
        var intent = result.Status == AgentProfileTurnClassificationStatus.Matched &&
                     string.Equals(result.IntentId, ServiceConnectIntentId, StringComparison.Ordinal)
            ? NyxIdChatTurnIntent.ServiceConnect
            : NyxIdChatTurnIntent.Unspecified;
        _logger.LogInformation(
            "NyxID chat turn intent classification completed. request={RequestId} status={Status} intent={IntentId} failure={FailureCode}",
            requestId,
            result.Status,
            result.IntentId,
            result.FailureCode);
        return intent;
    }
}
