using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Application.Reporting;

internal sealed class NoopWorkflowExecutionReportArtifactSink : IWorkflowExecutionReportArtifactSink
{
    public Task PersistAsync(WorkflowExecutionReport report, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
