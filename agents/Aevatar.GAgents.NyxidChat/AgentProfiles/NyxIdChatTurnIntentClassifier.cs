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
        "Establish, add, reauthorize, or repair a missing hosted external service account " +
        "connection, then verify that connection. Do not select this intent when the user asks " +
        "to invoke, read from, or write through an already-connected exact UserService; that " +
        "request must use the ordinary task route for the connected service operation.";
    internal const string KeyCreateIntentId = "key_create";
    internal const string KeyCreateRoutingDescription =
        "Create a least-scope NyxID API key for an exact nonempty set of caller-visible services.";
    internal const string KeyRotateIntentId = "key_rotate";
    internal const string KeyRotateRoutingDescription =
        "Rotate one exact caller-visible NyxID API key through the browser-owned secure journey.";
    internal const string WorkflowAuthoringIntentId = "workflow_authoring";
    internal const string WorkflowAuthoringRoutingDescription =
        "Discover or inspect exact external capabilities while drafting, revising, mounting, " +
        "binding, or publishing Workflow YAML. Do not select this intent for invoking an existing " +
        "workflow, calling an already-connected service directly, or connecting a missing service.";
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
    internal static AgentProfileTurnClassificationCandidate KeyRotateCandidate { get; } =
        new(
            KeyRotateIntentId,
            KeyRotateRoutingDescription,
            AgentProfileSideEffectClass.ExternalHandoff);
    internal static AgentProfileTurnClassificationCandidate WorkflowAuthoringCandidate { get; } =
        new(
            WorkflowAuthoringIntentId,
            WorkflowAuthoringRoutingDescription,
            AgentProfileSideEffectClass.ReadOnly);
    private static readonly AgentProfileTurnClassificationCandidate[] Candidates =
    [
        ServiceConnectCandidate,
        KeyCreateCandidate,
        KeyRotateCandidate,
        WorkflowAuthoringCandidate,
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
                KeyRotateIntentId => NyxIdChatTurnIntent.KeyRotate,
                WorkflowAuthoringIntentId => NyxIdChatTurnIntent.WorkflowAuthoring,
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
