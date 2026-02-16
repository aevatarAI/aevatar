using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.Workflow.Application.Abstractions.Reporting;

public interface IWorkflowExecutionReportArtifactSink
{
    Task PersistAsync(WorkflowRunReport report, CancellationToken ct = default);
}
