using Aevatar.Workflow.Application.Abstractions.Schedules;

namespace Aevatar.Workflow.Core.Schedules;

public interface IWorkflowScheduleDueEventHandlerPort
{
    Task HandleDueAsync(
        WorkflowScheduleDueEvent due,
        CancellationToken ct = default);
}

public sealed class NoopWorkflowScheduleDueEventHandlerPort : IWorkflowScheduleDueEventHandlerPort
{
    public Task HandleDueAsync(
        WorkflowScheduleDueEvent due,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(due);
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
