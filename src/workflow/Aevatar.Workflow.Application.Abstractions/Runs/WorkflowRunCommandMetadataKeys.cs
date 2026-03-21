namespace Aevatar.Workflow.Application.Abstractions.Runs;

public static class WorkflowRunCommandMetadataKeys
{
    public const string CommandId = "workflow.command_id";
    public const string SessionId = "workflow.session_id";
    public const string ChannelId = "workflow.channel_id";
    public const string ScopeId = "workflow.scope_id";
    public const string MessageId = "workflow.message_id";
    public const string CorrelationId = "workflow.correlation_id";
    public const string IdempotencyKey = "workflow.idempotency_key";
    public const string CallbackUrl = "workflow.callback_url";
}
