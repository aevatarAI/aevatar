using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Core.Schedules;

public sealed class WorkflowScheduleWakeupGAgent : GAgentBase
{
    [EventHandler]
    public Task HandleWorkflowScheduleDue(WorkflowScheduleDueEvent request) =>
        Services
            .GetRequiredService<IWorkflowScheduleDueEventHandlerPort>()
            .HandleDueAsync(request, CancellationToken.None);
}
