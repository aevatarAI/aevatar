using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Application.Abstractions.Credentials;

public interface IWorkflowCallerAccessTokenProvider
{
    Task<string> IssueAsync(
        WorkflowCallerNyxIdAuthority authority,
        CancellationToken ct = default);
}
