using Aevatar.Workflow.Application.Abstractions.Schedules;
using Aevatar.Workflow.Core.Schedules;

namespace Aevatar.Workflow.Infrastructure.Schedules;

public sealed class WorkflowScheduleDueEventHandlerPort : IWorkflowScheduleDueEventHandlerPort
{
    private readonly IWorkflowScheduleApplicationService _schedules;

    public WorkflowScheduleDueEventHandlerPort(IWorkflowScheduleApplicationService schedules)
    {
        _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
    }

    public Task HandleDueAsync(
        WorkflowScheduleDueEvent due,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(due);
        var scheduleId = due.ScheduleId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(scheduleId))
            return Task.CompletedTask;

        var scheduledFireAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(due.ScheduledFireAtUnixTimeMs);
        return _schedules.RunNowAsync(
            new WorkflowScheduleFireRequest(
                scheduleId,
                scheduledFireAtUtc,
                Force: false,
                AdvanceSchedule: true),
            ct);
    }
}
