namespace Aevatar.Foundation.Core.EventSourcing;

/// <summary>
/// Runtime binding hook for stateful agents to receive their event sourcing factory explicitly.
/// </summary>
public interface IEventSourcingFactoryBinding
{
    /// <summary>
    /// Binds the event sourcing behavior factory from runtime service provider.
    /// </summary>
    void BindEventSourcingFactory(IServiceProvider services);
}
