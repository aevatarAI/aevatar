namespace Aevatar.Workflow.Application.Abstractions.Schedules;

public static class WorkflowScheduledDispatchAdapterConventions
{
    public const string TargetActorId = "workflow.schedule.adapter";
}

public interface IWorkflowScheduledDispatchPreparationService
{
    Task<ScheduledDispatchPreparation> PrepareAsync(
        WorkflowScheduleConfiguration configuration,
        string commandId,
        string correlationId,
        CancellationToken ct = default);
}
