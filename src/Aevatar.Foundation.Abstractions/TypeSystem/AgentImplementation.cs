namespace Aevatar.Foundation.Abstractions.TypeSystem;

/// <summary>
/// Opaque handle returned by <see cref="IAgentKindRegistry.Resolve(string)"/>.
/// Carries everything <c>RuntimeActorGrain</c> needs to instantiate and bind
/// the agent without exposing the implementation CLR type at the contract
/// surface — the door stays open for scripted / workflow / out-of-process
/// implementations behind the same kind.
/// </summary>
public sealed record AgentImplementation(
    Func<IAgent> Factory,
    Type StateContractType,
    AgentImplementationMetadata Metadata);
