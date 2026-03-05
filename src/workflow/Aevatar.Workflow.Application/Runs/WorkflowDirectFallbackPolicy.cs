using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

/// <summary>
/// Decides whether a failed workflow run should be retried against the direct workflow.
/// </summary>
public sealed class WorkflowDirectFallbackPolicy
{
    private readonly WorkflowRunBehaviorOptions _behaviorOptions;

    public WorkflowDirectFallbackPolicy(WorkflowRunBehaviorOptions? behaviorOptions = null)
    {
        _behaviorOptions = behaviorOptions ?? new WorkflowRunBehaviorOptions();
    }

    /// <summary>
    /// Returns <see langword="true"/> when the request and exception satisfy direct fallback policy.
    /// </summary>
    public bool ShouldFallback(WorkflowChatRunRequest request, Exception ex)
    {
        if (!_behaviorOptions.EnableDirectFallback)
            return false;
        if (ex is OperationCanceledException)
            return false;
        if (!IsWhitelistedException(ex))
            return false;
        if (request.WorkflowYamls is { Count: > 0 })
            return false;

        var workflowName = WorkflowRunNameNormalizer.NormalizeWorkflowName(request.WorkflowName);
        if (string.IsNullOrWhiteSpace(workflowName))
            return false;

        var isDirectRequest = string.Equals(
            workflowName,
            WorkflowRunBehaviorOptions.DirectWorkflowName,
            StringComparison.OrdinalIgnoreCase);
        if (isDirectRequest)
            return false;

        return _behaviorOptions.DirectFallbackWorkflowWhitelist.Contains(workflowName);
    }

    private bool IsWhitelistedException(Exception ex)
    {
        var exceptionType = ex.GetType();
        return _behaviorOptions.DirectFallbackExceptionWhitelist.Contains(exceptionType);
    }

    public WorkflowChatRunRequest ToFallbackRequest(WorkflowChatRunRequest request) =>
        request with
        {
            WorkflowName = WorkflowRunBehaviorOptions.DirectWorkflowName,
            WorkflowYamls = null,
        };
}
