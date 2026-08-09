using Aevatar.AI.Abstractions;
using Aevatar.AI.Core.AgentProfiles;

namespace Aevatar.GAgents.NyxidChat.AgentProfiles;

public interface INyxIdChatTurnIntentClassifier
{
    Task<NyxIdChatTurnIntent> ClassifyAsync(
        string userMessage,
        CancellationToken ct = default);
}

public sealed class NyxIdChatTurnIntentClassifier : INyxIdChatTurnIntentClassifier
{
    internal const string ServiceConnectIntentId = "service_connect";
    private static readonly TimeSpan ClassificationTimeout = TimeSpan.FromSeconds(2);
    private static readonly AgentProfileTurnClassificationCandidate[] Candidates =
    [
        new(
            ServiceConnectIntentId,
            "Connect, add, or authorize a hosted external service account and verify that connection.",
            AgentProfileSideEffectClass.ExternalHandoff),
    ];

    private readonly IAgentProfileTurnClassifier _classifier;

    public NyxIdChatTurnIntentClassifier(IAgentProfileTurnClassifier classifier)
    {
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
    }

    public async Task<NyxIdChatTurnIntent> ClassifyAsync(
        string userMessage,
        CancellationToken ct = default)
    {
        var result = await _classifier.ClassifyAsync(
                new AgentProfileTurnClassificationRequest(
                    userMessage ?? string.Empty,
                    Candidates,
                    ClassificationTimeout),
                ct)
            .ConfigureAwait(false);
        return result.Status == AgentProfileTurnClassificationStatus.Matched &&
               string.Equals(result.IntentId, ServiceConnectIntentId, StringComparison.Ordinal)
            ? NyxIdChatTurnIntent.ServiceConnect
            : NyxIdChatTurnIntent.Unspecified;
    }
}
