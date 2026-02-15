using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Application.Reporting;

public interface IWorkflowExecutionReportArtifactSink
{
    Task PersistAsync(WorkflowExecutionReport report, CancellationToken ct = default);
}
