namespace Aevatar.Workflow.Abstractions.Credentials;

public interface IWorkflowCallerAccessTokenProvider
{
    Task<string> IssueAsync(
        WorkflowCallerNyxIdAuthority authority,
        CancellationToken ct = default);
}
