namespace Aevatar.Workflow.Application.Abstractions.Schedules;

public interface IWorkflowScheduledDispatchPreparationService
{
    Task<ScheduledDispatchPreparation> PrepareAsync(
        WorkflowScheduleConfiguration configuration,
        string commandId,
        string correlationId,
        CancellationToken ct = default);
}
