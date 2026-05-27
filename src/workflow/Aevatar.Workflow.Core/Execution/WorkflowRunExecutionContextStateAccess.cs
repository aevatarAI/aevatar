using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Execution;

// Refactor (iter115/cluster-3):
//   Old pattern: request-scoped LLM and connector control facts lived in
//                WorkflowExecutionRuntimeContext and disappeared on replay.
//   New principle: durable control/security facts are typed actor state under
//                  WorkflowRunState.ExecutionContext.
internal static class WorkflowRunExecutionContextStateAccess
{
    public static WorkflowRunExecutionContextState Get(IWorkflowExecutionStateHost stateHost)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        return stateHost.ExecutionContextSnapshot;
    }

    public static WorkflowRunExecutionContextState Get(IWorkflowExecutionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (ctx is IWorkflowExecutionStateHost stateHost)
            return stateHost.ExecutionContextSnapshot;
        if (ctx is IWorkflowExecutionStateHostAccessor stateHostAccessor)
            return stateHostAccessor.StateHost.ExecutionContextSnapshot;

        return new WorkflowRunExecutionContextState();
    }

    public static Task ClearAsync(
        IWorkflowExecutionStateHost stateHost,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        return stateHost.ClearExecutionContextAsync(ct);
    }

    public static Task ApplyRequestMetadataAsync(
        IWorkflowExecutionStateHost stateHost,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        var delta = BuildRequestMetadataDelta(metadata);
        return stateHost.UpdateExecutionContextAsync(delta, ct);
    }

    public static WorkflowRunExecutionContextDelta BuildRequestMetadataDelta(
        IReadOnlyDictionary<string, string>? metadata)
    {
        var delta = new WorkflowRunExecutionContextDelta
        {
            ClearConnector = true,
        };
        if (metadata == null || metadata.Count == 0)
            return delta;

        foreach (var pair in metadata)
        {
            var key = Normalize(pair.Key);
            var value = Normalize(pair.Value);
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;

            if (string.Equals(key, ConnectorRequest.HttpAuthorizationMetadataKey, StringComparison.Ordinal))
            {
                delta.Connector = new WorkflowRunConnectorExecutionContextDelta
                {
                    HttpAuthorization = value,
                };
                continue;
            }
        }

        return delta;
    }

    public static bool TryGetConnectorAuthorization(
        IWorkflowExecutionContext ctx,
        out string authorization)
    {
        var connector = Get(ctx).Connector;
        if (!string.IsNullOrWhiteSpace(connector?.HttpAuthorization))
        {
            authorization = connector.HttpAuthorization.Trim();
            return true;
        }

        authorization = string.Empty;
        return false;
    }

    public static bool TryGetLlm(
        IWorkflowExecutionContext ctx,
        out WorkflowLlmExecutionContextState llm)
    {
        llm = Get(ctx).Llm ?? new WorkflowLlmExecutionContextState();
        return !string.IsNullOrWhiteSpace(llm.ModelOverride) ||
               llm.HasMaxToolRoundsOverride ||
               !string.IsNullOrWhiteSpace(llm.UserMemoryPrompt);
    }

    public static WorkflowRunExecutionContextState RedactedClone(WorkflowRunExecutionContextState? source)
    {
        var clone = source?.Clone() ?? new WorkflowRunExecutionContextState();
        if (!string.IsNullOrWhiteSpace(clone.Connector?.HttpAuthorization))
            clone.Connector.HttpAuthorization = string.Empty;
        return clone;
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
