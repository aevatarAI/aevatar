using Aevatar.AI.Abstractions;

namespace Aevatar.AI.Abstractions.ToolProviders;

public interface IWorkflowRunBackgroundDeliveryRegistrationPort
{
    Task<WorkflowRunBackgroundDeliveryReceipt> RegisterAsync(
        WorkflowRunBackgroundDeliveryRegistration registration,
        CancellationToken ct = default);
}

public sealed record WorkflowRunBackgroundDeliveryRegistration(
    string DeliveryId,
    string WorkflowActorId,
    string WorkflowRunId,
    string WorkflowCommandId,
    string WorkflowCorrelationId,
    string StreamTopic,
    string ChannelPlatform,
    string ReplyMessageId,
    string PlatformMessageId,
    ChannelWorkflowResultDeliveryCredential WorkflowResultDeliveryCredential,
    string RegistrationScopeId,
    string BotRegistrationId);
