using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Core;

internal interface IWorkflowStepFamilyHandler
{
    IReadOnlyCollection<string> SupportedStepTypes { get; }

    Task HandleStepRequestAsync(StepRequestEvent request, CancellationToken ct);
}
