namespace Aevatar.Workflow.Application.Abstractions.Schedules;

public interface IWorkflowScheduleCommandPort
{
    Task<WorkflowScheduleMutationReceipt> EnsureAsync(
        WorkflowScheduleConfiguration configuration,
        CancellationToken ct = default);
}
