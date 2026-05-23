using Aevatar.Scripting.Abstractions.Evolution;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Projection.Orchestration;

namespace Aevatar.Integration.Tests;

internal static class ScriptProjectionTestActivationExtensions
{
    public static Task<IScriptExecutionProjectionLease?> EnsureActorProjectionAsync(
        this IScriptExecutionProjectionPort projectionPort,
        string actorId,
        CancellationToken ct = default)
    {
        var concretePort = projectionPort as ScriptExecutionProjectionPort
            ?? throw new InvalidOperationException(
                $"Integration tests require `{typeof(ScriptExecutionProjectionPort).FullName}` to activate execution observation scopes.");

        return concretePort.EnsureActorProjectionAsync(actorId, ct);
    }

    public static Task<IScriptEvolutionProjectionLease?> EnsureActorProjectionAsync(
        this IScriptEvolutionProjectionPort projectionPort,
        string sessionActorId,
        string proposalId,
        CancellationToken ct = default)
    {
        var concretePort = projectionPort as ScriptEvolutionProjectionPort
            ?? throw new InvalidOperationException(
                $"Integration tests require `{typeof(ScriptEvolutionProjectionPort).FullName}` to activate evolution observation scopes.");

        return concretePort.EnsureActorProjectionAsync(sessionActorId, proposalId, ct);
    }
}
