using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Workflow.Abstractions.Execution;

namespace Aevatar.Workflow.Core.Execution;

internal static class ConnectorAuthorizationRuntimeItemsAccess
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
