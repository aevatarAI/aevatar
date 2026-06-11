using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

/// <summary>
/// Decides whether a failed workflow run should be retried against the direct workflow.
/// </summary>
// Refactor (iter112/cluster-3): Old pattern: fallback policy treated legacy request mirrors as the command source. New principle: fallback decisions are derived from typed WorkflowChatSource only.
public sealed class WorkflowDirectFallbackPolicy
    : ICommandFallbackPolicy<WorkflowChatRunRequest>
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
        // Refactor (iter112/cluster-3): Old pattern: fallback policy inspected legacy WorkflowName/WorkflowYamls mirrors. New principle: fallback reads only the typed WorkflowChatSource.
        if (!_behaviorOptions.EnableDirectFallback)
            return false;
        if (ex is OperationCanceledException)
            return false;
        if (!IsWhitelistedException(ex))
            return false;
        if (request.Source.InlineBundle?.YamlDocuments is { Count: > 0 })
            return false;

        var workflowName = ResolveEffectiveWorkflowName(request);
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

    public bool TryCreateFallbackCommand(
        WorkflowChatRunRequest command,
        Exception exception,
        out WorkflowChatRunRequest fallbackCommand)
    {
        if (ShouldFallback(command, exception))
        {
            fallbackCommand = ToFallbackRequest(command);
            return true;
        }

        fallbackCommand = command;
        return false;
    }

    private bool IsWhitelistedException(Exception ex)
    {
        var exceptionType = ex.GetType();
        return _behaviorOptions.DirectFallbackExceptionWhitelist.Contains(exceptionType);
    }

    private string ResolveEffectiveWorkflowName(WorkflowChatRunRequest request)
    {
        // Refactor (iter112/cluster-3): Old pattern: effective workflow resolution read WorkflowName directly. New principle: workflow identity is read through WorkflowChatSource.
        ArgumentNullException.ThrowIfNull(request);

        var requestedWorkflowName = request.Source.Kind switch
        {
            WorkflowChatSourceKind.CatalogWorkflow =>
                WorkflowRunNameNormalizer.NormalizeWorkflowName(request.Source.CatalogName?.WorkflowName),
            WorkflowChatSourceKind.DefinitionActor =>
                WorkflowRunNameNormalizer.NormalizeWorkflowName(request.Source.DefinitionActorSource?.WorkflowName),
            WorkflowChatSourceKind.InlineYamlBundle =>
                WorkflowRunNameNormalizer.NormalizeWorkflowName(request.Source.InlineBundle?.EntryName),
            _ => string.Empty,
        };
        if (!string.IsNullOrWhiteSpace(requestedWorkflowName))
            return requestedWorkflowName;

        if (_behaviorOptions.UseAutoAsDefaultWhenWorkflowUnspecified)
            return WorkflowRunBehaviorOptions.AutoWorkflowName;

        var configuredDefault = WorkflowRunNameNormalizer.NormalizeWorkflowName(_behaviorOptions.DefaultWorkflowName);
        return string.IsNullOrWhiteSpace(configuredDefault)
            ? WorkflowRunBehaviorOptions.DirectWorkflowName
            : configuredDefault;
    }

    public WorkflowChatRunRequest ToFallbackRequest(WorkflowChatRunRequest request)
    {
        // Refactor (iter112/cluster-3): Old pattern: fallback rewrote mirror fields individually. New principle: fallback replaces the typed source atomically.
        return request with
        {
            Source = WorkflowChatSource.CatalogWorkflow(WorkflowRunBehaviorOptions.DirectWorkflowName),
        };
    }
}
