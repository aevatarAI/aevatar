namespace Aevatar.GAgents.Scheduled;

public static class ScheduledWorkflowAgentDefaults
{
    public const string AgentType = "scheduled_workflow";
    public const string ActorIdPrefix = "scheduled-workflow";
    public const string DefaultWorkflowName = "direct";
    public const string DefaultTimezone = "UTC";

    public static string GenerateActorId() => $"{ActorIdPrefix}-{Guid.NewGuid():N}";
}
