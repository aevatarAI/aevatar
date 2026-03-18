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
        ct.ThrowIfCancellationRequested();

        var actorId = ProjectionScopeActorId.Build(scopeKey);
        if (!await _runtime.ExistsAsync(actorId).ConfigureAwait(false))
            return false;

        var envelope = ProjectionScopeCommandEnvelopeFactory.Create(
            new ReplayProjectionFailuresCommand
            {
                MaxItems = Math.Max(1, maxItems),
            },
            actorId);
        envelope.Route = EnvelopeRouteSemantics.CreateDirect("projection.scope.admin.replay", actorId);
        await _dispatchPort.DispatchAsync(actorId, envelope, ct).ConfigureAwait(false);
        return true;
    }
}
