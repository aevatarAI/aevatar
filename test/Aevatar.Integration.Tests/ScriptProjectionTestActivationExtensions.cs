using Aevatar.CQRS.Projection.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Aevatar.Scripting.Abstractions.Evolution;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Orchestration;

namespace Aevatar.Integration.Tests;

internal static class ScriptProjectionTestActivationExtensions
{
    public static async Task<IScriptExecutionProjectionLease?> EnsureScriptExecutionProjectionAsync(
        this IServiceProvider services,
        string actorId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        var activationService = services.GetRequiredService<IProjectionScopeActivationService<ScriptExecutionRuntimeLease>>();
        return await activationService.EnsureAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = ScriptProjectionKinds.ExecutionSession,
                Mode = ProjectionRuntimeMode.SessionObservation,
                SessionId = actorId,
            },
            ct);
    }

    public static async Task<IScriptEvolutionProjectionLease?> EnsureScriptEvolutionProjectionAsync(
        this IServiceProvider services,
        string sessionActorId,
        string proposalId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        var activationService = services.GetRequiredService<IProjectionScopeActivationService<ScriptEvolutionRuntimeLease>>();
        return await activationService.EnsureAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = sessionActorId,
                ProjectionKind = ScriptProjectionKinds.EvolutionSession,
                Mode = ProjectionRuntimeMode.SessionObservation,
                SessionId = proposalId,
            },
            ct);
    }
}
