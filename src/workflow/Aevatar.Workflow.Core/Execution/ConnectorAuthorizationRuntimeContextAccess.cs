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
    public static void SetAuthorization(
        IWorkflowExecutionStateHost stateHost,
        string? authorization)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        stateHost.RuntimeContext.Connector.Authorization =
            string.IsNullOrWhiteSpace(authorization) ? null : authorization.Trim();
    }

    public static void RemoveAuthorization(IWorkflowExecutionStateHost stateHost)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        stateHost.RuntimeContext.Connector.Authorization = null;
    }

    public static bool TryGetAuthorization(
        IWorkflowExecutionContext ctx,
        out string authorization)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx is IWorkflowExecutionRuntimeContextAccessor runtimeAccessor &&
            !string.IsNullOrWhiteSpace(runtimeAccessor.RuntimeContext.Connector.Authorization))
        {
            authorization = runtimeAccessor.RuntimeContext.Connector.Authorization.Trim();
            return true;
        }

        authorization = string.Empty;
        return false;
    }
}
