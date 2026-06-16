using Aevatar.AI.Abstractions;

namespace Aevatar.AI.Abstractions.ToolProviders;

public static class WorkflowRunBackgroundDeliveryMetadataKeys
{
    public const string BotAgentKeyId = "channel.nyx_agent_api_key_id";
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
    string BotAgentKeyId,
    string RegistrationScopeId);
