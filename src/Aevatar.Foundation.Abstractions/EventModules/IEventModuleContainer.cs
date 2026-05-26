namespace Aevatar.Foundation.Abstractions.EventModules;

/// <summary>
/// Exposes the dynamic event modules currently attached to one agent.
/// </summary>
public interface IEventModuleContainer<TContext>
    where TContext : IEventContext
{
    IReadOnlyList<IEventModule<TContext>> GetModules();
}
