using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Workflow.Abstractions.Execution;

namespace Aevatar.Workflow.Core.Execution;

// Refactor (iter16/cluster-031):
//   Old pattern: connector authorization lived under the generic execution item
//                key `http.authorization` and was recovered through string-key bag lookup.
//   New principle: connector authorization is a typed field on the actor-owned
//                  WorkflowExecutionRuntimeContext connector section.
internal static class ConnectorAuthorizationRuntimeContextAccess
{
    // Refactor (iter16/cluster-031):
    //   Old pattern: authorization writes used SetExecutionItem with the
    //                `http.authorization` string key.
    //   New principle: authorization writes update the typed connector section
    //                  of WorkflowExecutionRuntimeContext.
    public static Task SetAuthorizationAsync(
        IWorkflowExecutionStateHost stateHost,
        string? authorization,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        if (string.IsNullOrWhiteSpace(authorization))
        {
            return stateHost.UpdateExecutionContextAsync(
                new WorkflowRunExecutionContextDelta
                {
                    ClearConnector = true,
                },
                ct);
        }

        return stateHost.UpdateExecutionContextAsync(
            new WorkflowRunExecutionContextDelta
            {
                ClearConnector = true,
                Connector = new WorkflowRunConnectorExecutionContextDelta
                {
                    HttpAuthorization = authorization.Trim(),
                },
            },
            ct);
    }

    // Refactor (iter16/cluster-031):
    //   Old pattern: clearing authorization removed a value from the generic
    //                execution item bag by string key.
    //   New principle: clearing authorization nulls the typed connector field.
    public static Task RemoveAuthorizationAsync(
        IWorkflowExecutionStateHost stateHost,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        return stateHost.UpdateExecutionContextAsync(
            new WorkflowRunExecutionContextDelta
            {
                ClearConnector = true,
            },
            ct);
    }

    // Refactor (iter16/cluster-031):
    //   Old pattern: connector modules recovered authorization through generic
    //                item lookup on IWorkflowExecutionItemsContext.
    //   New principle: connector modules read authorization through the typed
    //                  runtime context accessor contract.
    public static bool TryGetAuthorization(
        IWorkflowExecutionContext ctx,
        out string authorization)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        return WorkflowRunExecutionContextStateAccess.TryGetConnectorAuthorization(ctx, out authorization);
    }
}
