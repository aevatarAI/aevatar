namespace Aevatar.Foundation.Abstractions.TypeSystem;

/// <summary>
/// Phase 1 transitional fallback for un-decorated <see cref="IAgent"/>
/// classes whose persisted <c>RuntimeActorGrainState.AgentTypeName</c>
/// points at a CLR full name not yet registered with
/// <see cref="IAgentKindRegistry"/> via <see cref="GAgentAttribute"/>.
/// </summary>
/// <remarks>
/// <para>
/// Encapsulates the legacy <c>Type.GetType</c> + AppDomain reflection scan
/// behind a port so <c>RuntimeActorGrain</c> stays free of reflection.
/// Activations resolved through this path do <em>not</em> lazy-tag
/// <c>RuntimeActorIdentity.Kind</c> — without a stable kind, there is
/// nothing safe to write back.
/// </para>
/// <para>
/// Phase 3 (hard-deprecation) removes the default registration of this
/// resolver from DI; un-decorated classes will fail to activate, forcing
/// every live <see cref="IAgent"/> implementation to declare a
/// <see cref="GAgentAttribute"/>.
/// </para>
/// </remarks>
public interface ILegacyAgentClrTypeResolver
{
    bool TryResolve(string clrTypeName, out AgentImplementation implementation);
}
