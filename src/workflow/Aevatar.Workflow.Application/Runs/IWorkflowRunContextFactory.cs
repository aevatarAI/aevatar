using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

public interface IWorkflowRunContextFactory
{
    Task<WorkflowRunContextCreateResult> CreateAsync(
        WorkflowChatRunRequest request,
        CancellationToken ct = default);
}
