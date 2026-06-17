using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Infrastructure.Runs;

public sealed class WorkflowConnectedServiceFileSubmitOptions
{
    public const string SectionName = "WorkflowConnectedServiceFileSubmit";

    public List<WorkflowConnectedServiceFileSubmitTarget> Targets { get; } = [];
}
