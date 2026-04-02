using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Workflow.Abstractions.Execution;

namespace Aevatar.Workflow.Core.Execution;

internal static class ConnectorAuthorizationRuntimeItemsAccess
{
    private const string ConnectorAuthorizationItemKey = ConnectorRequest.HttpAuthorizationMetadataKey;

    public static void SetAuthorization(
        IWorkflowExecutionStateHost stateHost,
        string? authorization)
    {
        ArgumentNullException.ThrowIfNull(stateHost);

        if (string.IsNullOrWhiteSpace(authorization))
        {
            stateHost.RemoveExecutionItem(ConnectorAuthorizationItemKey);
            return;
        }

        stateHost.SetExecutionItem(ConnectorAuthorizationItemKey, authorization.Trim());
    }

    public static void RemoveAuthorization(IWorkflowExecutionStateHost stateHost)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        stateHost.RemoveExecutionItem(ConnectorAuthorizationItemKey);
    }

    public static bool TryGetAuthorization(
        IWorkflowExecutionContext ctx,
        out string authorization)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (WorkflowExecutionItemsAccess.TryGetItem(ctx, ConnectorAuthorizationItemKey, out string? existing) &&
            !string.IsNullOrWhiteSpace(existing))
        {
            authorization = existing.Trim();
            return true;
        }

        authorization = string.Empty;
        return false;
    }
}
