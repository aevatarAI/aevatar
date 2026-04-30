namespace Aevatar.Foundation.Abstractions.TypeSystem;

/// <summary>
/// Opaque handle returned by <see cref="IAgentKindRegistry.Resolve(string)"/>.
/// Carries everything <c>RuntimeActorGrain</c> needs to instantiate and bind
/// the agent without exposing the implementation CLR type at the contract
/// surface — the door stays open for scripted / workflow / out-of-process
/// implementations behind the same kind.
/// </summary>
/// <remarks>
/// <see cref="Factory"/> takes the activation-time <see cref="IServiceProvider"/>
/// rather than capturing one at registry-construction time, so grain-scoped
/// dependencies resolve in the grain's own container instead of the silo
/// root container. Capturing a singleton-scoped provider here would make
/// every agent's constructor-injected scoped dependency captive.
/// </remarks>
public sealed record AgentImplementation(
    Func<IServiceProvider, IAgent> Factory,
    Type StateContractType,
    AgentImplementationMetadata Metadata);
