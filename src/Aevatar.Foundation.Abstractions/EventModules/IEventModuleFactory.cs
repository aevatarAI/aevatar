// ─────────────────────────────────────────────────────────────
// IEventModuleFactory<TContext> - event module factory contract.
// Creates event module instances by name for dynamic loading/registration.
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;

namespace Aevatar.Foundation.Abstractions.EventModules;

/// <summary>
/// Generic event module factory contract for creating IEventModule<TContext> instances by name.
/// </summary>
public interface IEventModuleFactory<TContext>
    where TContext : IEventContext
{
    /// <summary>Tries to create a module by name. Returns true on success.</summary>
    bool TryCreate(string name, out IEventModule<TContext>? module);
}
