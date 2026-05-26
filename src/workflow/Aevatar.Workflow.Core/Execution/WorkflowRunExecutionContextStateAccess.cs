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
        return stateHost.ExecutionContextState;
    }

    public static WorkflowRunExecutionContextState Get(IWorkflowExecutionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (ctx is IWorkflowExecutionStateHost stateHost)
            return stateHost.ExecutionContextState;
        if (ctx is IWorkflowExecutionStateHostAccessor stateHostAccessor)
            return stateHostAccessor.StateHost.ExecutionContextState;

        return new WorkflowRunExecutionContextState();
    }

    public static void Clear(IWorkflowExecutionStateHost stateHost)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        stateHost.ExecutionContextState.Llm = null;
        stateHost.ExecutionContextState.Connector = null;
    }

    public static void ApplyRequestMetadata(
        IWorkflowExecutionStateHost stateHost,
        IReadOnlyDictionary<string, string>? metadata)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        stateHost.ExecutionContextState.Connector = null;

        if (metadata == null || metadata.Count == 0)
            return;

        foreach (var pair in metadata)
        {
            var key = Normalize(pair.Key);
            var value = Normalize(pair.Value);
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;

            if (string.Equals(key, ConnectorRequest.HttpAuthorizationMetadataKey, StringComparison.Ordinal))
            {
                EnsureConnector(stateHost.ExecutionContextState).HttpAuthorization = value;
                continue;
            }
        }
    }

    public static void ApplyToolContext(
        IWorkflowExecutionStateHost stateHost,
        AgentToolExecutionContext? toolContext)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        stateHost.ExecutionContextState.Llm = null;

        if (toolContext == null)
            return;

        var llm = EnsureLlm(stateHost.ExecutionContextState);
        llm.NyxidAccessToken = Normalize(toolContext.Credentials.NyxIdAccessToken);
        llm.ModelOverride = Normalize(toolContext.Routing.ModelOverride);
        llm.NyxidRoutePreference = Normalize(toolContext.Routing.NyxIdRoutePreference);

        if (string.IsNullOrWhiteSpace(llm.NyxidAccessToken) &&
            string.IsNullOrWhiteSpace(llm.ModelOverride) &&
            string.IsNullOrWhiteSpace(llm.NyxidRoutePreference))
        {
            stateHost.ExecutionContextState.Llm = null;
        }
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
        return !string.IsNullOrWhiteSpace(llm.NyxidAccessToken) ||
               !string.IsNullOrWhiteSpace(llm.ModelOverride) ||
               !string.IsNullOrWhiteSpace(llm.NyxidRoutePreference);
    }

    public static WorkflowLlmExecutionContextState EnsureLlm(WorkflowRunExecutionContextState state)
    {
        state.Llm ??= new WorkflowLlmExecutionContextState();
        return state.Llm;
    }

    public static WorkflowConnectorExecutionContextState EnsureConnector(WorkflowRunExecutionContextState state)
    {
        state.Connector ??= new WorkflowConnectorExecutionContextState();
        return state.Connector;
    }

    public static WorkflowRunExecutionContextState RedactedClone(WorkflowRunExecutionContextState? source)
    {
        var clone = source?.Clone() ?? new WorkflowRunExecutionContextState();
        if (!string.IsNullOrWhiteSpace(clone.Llm?.NyxidAccessToken))
            clone.Llm.NyxidAccessToken = string.Empty;
        if (!string.IsNullOrWhiteSpace(clone.Connector?.HttpAuthorization))
            clone.Connector.HttpAuthorization = string.Empty;
        return clone;
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
