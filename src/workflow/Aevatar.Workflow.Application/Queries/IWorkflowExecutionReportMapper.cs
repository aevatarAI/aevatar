using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Application.Queries;

public interface IWorkflowExecutionReportMapper
{
    WorkflowRunSummary ToSummary(WorkflowExecutionReport report);

    WorkflowRunReport ToReport(WorkflowExecutionReport report);
}
