using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.Workflow.Application.Abstractions.Reporting;

public interface IWorkflowRunReportExportPort
{
    Task ExportAsync(WorkflowRunReport report, CancellationToken ct = default);
}
