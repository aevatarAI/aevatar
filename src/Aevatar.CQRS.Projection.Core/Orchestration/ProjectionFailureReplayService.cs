namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed class ProjectionFailureReplayService : IProjectionFailureReplayService
{
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;

    public ProjectionFailureReplayService(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
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
            return false;

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
