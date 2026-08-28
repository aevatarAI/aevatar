using Aevatar.Foundation.Abstractions.Streaming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed class ProjectionFailureReplayService : IProjectionFailureReplayService
{
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IStreamForwardingBindingAuthority _bindingAuthority;
    private readonly ILogger<ProjectionFailureReplayService> _logger;

    public ProjectionFailureReplayService(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        IStreamForwardingBindingAuthority bindingAuthority,
        ILogger<ProjectionFailureReplayService>? logger = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _bindingAuthority = bindingAuthority ?? throw new ArgumentNullException(nameof(bindingAuthority));
        _logger = logger ?? NullLogger<ProjectionFailureReplayService>.Instance;
    }

    public async Task<bool> ReplayAsync(
        ProjectionRuntimeScopeKey scopeKey,
        int maxItems = 100,
        CancellationToken ct = default)
    {
        return await DispatchAsync(
            scopeKey,
            maxItems,
            automaticRecovery: false,
            observedScopeStateVersion: 0,
            ct).ConfigureAwait(false);
    }

    public async Task<bool> ReplayAutomaticallyAsync(
        ProjectionRuntimeScopeKey scopeKey,
        long observedScopeStateVersion,
        int maxItems = 100,
        CancellationToken ct = default)
    {
        return await DispatchAsync(
            scopeKey,
            maxItems,
            automaticRecovery: true,
            Math.Max(1, observedScopeStateVersion),
            ct).ConfigureAwait(false);
    }

    private async Task<bool> DispatchAsync(
        ProjectionRuntimeScopeKey scopeKey,
        int maxItems,
        bool automaticRecovery,
        long observedScopeStateVersion,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var actorId = ProjectionScopeActorId.Build(scopeKey);
        if (!await _runtime.ExistsAsync(actorId).ConfigureAwait(false))
        {
            var binding = await _bindingAuthority
                .GetAsync(scopeKey.RootActorId, actorId, ct)
                .ConfigureAwait(false);
            if (!ProjectionScopeObservationRelayBinding.TryGetRecoveryTargetActorKind(
                    binding,
                    scopeKey.RootActorId,
                    actorId,
                    out var targetActorKind))
            {
                return false;
            }

            // The durable relay is actor-owned activation evidence and carries the
            // exact registered kind. Re-establishing by that typed fact repairs a
            // state row that the runtime deliberately reports as uninitialized;
            // the original stream delivery remains pending and is then redelivered.
            _logger.LogWarning(
                "Projection failure replay is re-establishing an uninitialized scope actor from durable relay evidence. actorId={ActorId} rootActorId={RootActorId} projectionKind={ProjectionKind} targetActorKind={TargetActorKind}",
                actorId,
                scopeKey.RootActorId,
                scopeKey.ProjectionKind,
                targetActorKind);
            _ = await _runtime
                .CreateByKindAsync(targetActorKind, actorId, ct)
                .ConfigureAwait(false);
        }

        var envelope = ProjectionScopeCommandEnvelopeFactory.Create(
            new ReplayProjectionFailuresCommand
            {
                MaxItems = Math.Max(1, maxItems),
                AutomaticRecovery = automaticRecovery,
                ObservedScopeStateVersion = observedScopeStateVersion,
            },
            actorId);
        envelope.Route = EnvelopeRouteSemantics.CreateDirect(
            automaticRecovery ? "projection.scope.automatic-recovery" : "projection.scope.admin.replay",
            actorId);
        await _dispatchPort.DispatchAsync(actorId, envelope, ct).ConfigureAwait(false);
        return true;
    }
}
