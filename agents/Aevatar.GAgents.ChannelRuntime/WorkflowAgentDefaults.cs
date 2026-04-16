namespace Aevatar.GAgents.ChannelRuntime;

internal static class WorkflowAgentDefaults
{
    public const string AgentType = "workflow_agent";
    public const string ActorIdPrefix = "workflow-agent";
    public const string TemplateName = "social_media";
    public const string ProviderName = "nyxid";
    public const string DefaultTimezone = "UTC";
    public const string StatusRunning = "running";
    public const string StatusError = "error";
    public const string StatusDisabled = "disabled";
    public const string TriggerCallbackId = "workflow-agent-next-fire";

    public static string GenerateActorId() => $"{ActorIdPrefix}-{Guid.NewGuid():N}";
}
