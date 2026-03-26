using Aevatar.Foundation.Abstractions.Connectors;

namespace Aevatar.Workflow.Abstractions.Execution;

/// <summary>
/// Resolves a workflow-visible connector at runtime.
/// </summary>
public interface IWorkflowConnectorResolver
{
    ValueTask<IConnector?> ResolveAsync(
        IWorkflowExecutionContext context,
        string connectorName,
        CancellationToken ct = default);
}
