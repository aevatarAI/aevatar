using Aevatar.AI.ToolProviders.Binding.Models;

namespace Aevatar.AI.ToolProviders.Binding.Ports;

/// <summary>
/// Adapter for unbinding services from a scope.
/// Infrastructure layer must provide the implementation.
/// </summary>
public interface IScopeBindingUnbindAdapter
{
    Task<ScopeBindingUnbindResult> UnbindAsync(string scopeId, string serviceId, CancellationToken ct = default);
}
