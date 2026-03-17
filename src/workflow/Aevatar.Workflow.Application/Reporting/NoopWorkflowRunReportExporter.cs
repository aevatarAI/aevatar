using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Reporting;

namespace Aevatar.Workflow.Application.Reporting;

internal sealed class NoopWorkflowRunReportExporter : IWorkflowRunReportExportPort
{
    public Task ExportAsync(WorkflowRunReport report, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
