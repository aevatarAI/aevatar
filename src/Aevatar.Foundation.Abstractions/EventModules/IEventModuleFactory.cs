// ─────────────────────────────────────────────────────────────
// IEventModuleFactory - event module factory contract.
// Creates event module instances by name for dynamic loading/registration.
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Foundation.Abstractions.EventModules;

/// <summary>
/// Event module factory contract for creating IEventModule instances by name.
/// </summary>
public interface IEventModuleFactory
{
    /// <summary>Tries to create a module by name. Returns true on success.</summary>
    bool TryCreate(string name, out IEventModule? module);
}
