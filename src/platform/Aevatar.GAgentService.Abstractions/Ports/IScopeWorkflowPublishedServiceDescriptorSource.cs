namespace Aevatar.GAgentService.Abstractions.Ports;

/// <summary>
/// Supplies explicit workflow-to-published-service mappings from an owning
/// read model. Implementations must not derive one identity from another.
/// </summary>
public interface IScopeWorkflowPublishedServiceDescriptorSource
{
    Task<IReadOnlyList<ScopeWorkflowPublishedServiceDescriptor>> ListAsync(
        string scopeId,
        int take,
        CancellationToken ct = default);

    Task<IReadOnlyList<ScopeWorkflowPublishedServiceDescriptor>> FindByWorkflowIdAsync(
        string scopeId,
        string workflowId,
        CancellationToken ct = default);
}
