using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Reporting;

namespace Aevatar.Workflow.Application.Reporting;

internal sealed class NoopWorkflowExecutionReportArtifactSink : IWorkflowExecutionReportArtifactSink
{
    public Task PersistAsync(WorkflowRunReport report, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
