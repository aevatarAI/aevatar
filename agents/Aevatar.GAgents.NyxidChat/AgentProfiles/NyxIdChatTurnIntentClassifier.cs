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
    internal const string KeyCreateIntentId = "key_create";
    internal const string KeyCreateRoutingDescription =
        "Create a least-scope NyxID API key for an exact nonempty set of caller-visible services.";
    private static readonly TimeSpan ClassificationTimeout = TimeSpan.FromSeconds(15);
    internal static AgentProfileTurnClassificationCandidate ServiceConnectCandidate { get; } =
        new(
            ServiceConnectIntentId,
            ServiceConnectRoutingDescription,
            AgentProfileSideEffectClass.ExternalHandoff);
    internal static AgentProfileTurnClassificationCandidate KeyCreateCandidate { get; } =
        new(
            KeyCreateIntentId,
            KeyCreateRoutingDescription,
            AgentProfileSideEffectClass.ExternalHandoff);
    private static readonly AgentProfileTurnClassificationCandidate[] Candidates =
    [
        ServiceConnectCandidate,
        KeyCreateCandidate,
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
        var intent = result.Status == AgentProfileTurnClassificationStatus.Matched
            ? result.IntentId switch
            {
                ServiceConnectIntentId => NyxIdChatTurnIntent.ServiceConnect,
                KeyCreateIntentId => NyxIdChatTurnIntent.KeyCreate,
                _ => NyxIdChatTurnIntent.Unspecified,
            }
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
