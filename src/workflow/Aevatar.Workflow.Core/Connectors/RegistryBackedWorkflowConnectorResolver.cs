using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Workflow.Abstractions.Execution;

namespace Aevatar.Workflow.Core.Connectors;

/// <summary>
/// Default runtime connector resolver backed by the host-level connector registry.
/// </summary>
public sealed class RegistryBackedWorkflowConnectorResolver : IWorkflowConnectorResolver
{
    private readonly IConnectorRegistry _registry;

    public RegistryBackedWorkflowConnectorResolver(IConnectorRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public ValueTask<IConnector?> ResolveAsync(
        IWorkflowExecutionContext context,
        string connectorName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorName);
        ct.ThrowIfCancellationRequested();

        _registry.TryGet(connectorName, out var connector);
        return ValueTask.FromResult(connector);
    }
}
