namespace Aevatar.GAgentService.Abstractions;

public sealed record ScopeWorkflowTextInvocationRequest(
    string ScopeId,
    string WorkflowId,
    string Prompt,
    string? RevisionId = null,
    string? CommandId = null,
    string? CorrelationId = null);

public sealed class ScopeWorkflowInvocationLookupException : InvalidOperationException
{
    public ScopeWorkflowInvocationLookupException(
        string scopeId,
        string workflowId,
        ScopeWorkflowLookupResult lookup)
        : base($"Workflow '{workflowId}' cannot be invoked for scope '{scopeId}': {lookup.Reason}.")
    {
        ScopeId = scopeId;
        WorkflowId = workflowId;
        Lookup = lookup;
    }

    public string ScopeId { get; }
    public string WorkflowId { get; }
    public ScopeWorkflowLookupResult Lookup { get; }
}
