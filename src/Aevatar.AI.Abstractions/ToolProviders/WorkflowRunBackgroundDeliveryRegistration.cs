using Aevatar.AI.Abstractions;

namespace Aevatar.AI.Abstractions.ToolProviders;

public static class WorkflowRunBackgroundDeliveryMetadataKeys
{
    public const string DurableReplyCredentialRef = "channel.nyx_reply_credential_ref";
}

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
    string DurableReplyCredentialRef,
    string RegistrationScopeId);
