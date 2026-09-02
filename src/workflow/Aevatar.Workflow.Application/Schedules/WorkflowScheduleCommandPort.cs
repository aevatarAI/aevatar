using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.Workflow.Application.Abstractions.Schedules;

namespace Aevatar.Workflow.Application.Schedules;

public sealed class WorkflowScheduleCommandPort : IWorkflowScheduleCommandPort
{
    private readonly IScheduledDispatchApplicationService _scheduledDispatches;

    public WorkflowScheduleCommandPort(IScheduledDispatchApplicationService scheduledDispatches)
    {
        _scheduledDispatches = scheduledDispatches ?? throw new ArgumentNullException(nameof(scheduledDispatches));
    }

    public async Task<WorkflowScheduleMutationReceipt> EnsureAsync(
        WorkflowScheduleConfiguration configuration,
        CancellationToken ct = default)
    {
        var receipt = await _scheduledDispatches.EnsureAsync(
            WorkflowScheduleConfigurationMapper.ToScheduledDispatchConfiguration(configuration),
            WorkflowScheduleConfigurationMapper.ToScheduledDispatchMutationContext(configuration),
            ct);
        return new WorkflowScheduleMutationReceipt(
            receipt.ScheduleId,
            receipt.ScheduleActorId,
            receipt.Accepted,
            receipt.CommandId,
            receipt.CorrelationId,
            receipt.AckedAt,
            receipt.AckStage);
    }
}
