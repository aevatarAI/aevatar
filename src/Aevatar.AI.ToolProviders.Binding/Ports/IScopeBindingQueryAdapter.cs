using Aevatar.AI.ToolProviders.Binding.Models;

namespace Aevatar.AI.ToolProviders.Binding.Ports;

/// <summary>
/// Read-only adapter for querying scope bindings.
/// Infrastructure layer must provide the implementation.
/// </summary>
public interface IScopeBindingQueryAdapter
{
    Task<IReadOnlyList<ScopeBindingEntry>> ListAsync(string scopeId, CancellationToken ct = default);
    Task<ScopeBindingHealthStatus?> GetStatusAsync(string scopeId, string serviceId, CancellationToken ct = default);
}
