namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Supplies an exact, module-owned Agent Kind for re-establishing a projection scope whose
/// runtime identity is unavailable and whose authoritative durable relay is absent. A resolver
/// is a static capability contract; it must never override conflicting distributed evidence.
/// </summary>
public interface IProjectionScopeRecoveryAgentKindResolver
{
    bool TryResolve(
        ProjectionRuntimeScopeKey scopeKey,
        out string agentKind);
}
