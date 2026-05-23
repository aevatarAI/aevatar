using Aevatar.Foundation.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.Orchestration;

namespace Aevatar.Demos.Inspector.ReadModels;

internal sealed class InspectorStudioActorBootstrap : IStudioActorBootstrap
{
    private readonly IActorRuntime _runtime;
    private readonly IProjectionScopeActivationService<StudioMaterializationRuntimeLease> _activationService;

    public InspectorStudioActorBootstrap(
        IActorRuntime runtime,
        IProjectionScopeActivationService<StudioMaterializationRuntimeLease> activationService)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
    }

    public async Task<IActor> EnsureAsync<TAgent>(string actorId, CancellationToken ct = default)
        where TAgent : IAgent, IProjectedActor
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        var actor = await _runtime.GetAsync(actorId)
                    ?? await _runtime.CreateAsync<TAgent>(actorId, ct);

        await _activationService.EnsureAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = TAgent.ProjectionKind,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
            ct);

        return actor;
    }
}
