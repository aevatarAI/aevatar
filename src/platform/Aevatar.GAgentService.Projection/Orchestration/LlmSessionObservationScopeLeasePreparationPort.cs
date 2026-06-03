using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class LlmSessionObservationScopeLeasePreparationPort
    : ILlmSessionObservationScopeLeasePreparationPort
{
    private readonly IProjectionScopeActivationService<LlmSessionObservationRuntimeLease> _sessionActivationService;
    private readonly IProjectionScopeReleaseService<LlmSessionObservationRuntimeLease> _sessionReleaseService;

    public LlmSessionObservationScopeLeasePreparationPort(
        IProjectionScopeActivationService<LlmSessionObservationRuntimeLease> sessionActivationService,
        IProjectionScopeReleaseService<LlmSessionObservationRuntimeLease> sessionReleaseService)
    {
        _sessionActivationService = sessionActivationService ?? throw new ArgumentNullException(nameof(sessionActivationService));
        _sessionReleaseService = sessionReleaseService ?? throw new ArgumentNullException(nameof(sessionReleaseService));
    }

    public async Task<LlmSessionObservationScopeLeasePreparation?> PrepareAsync(
        string actorId,
        string responseId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(responseId))
            return null;

        LlmSessionObservationRuntimeLease? sessionLease = null;
        try
        {
            var normalizedActorId = actorId.Trim();
            var normalizedResponseId = responseId.Trim();
            sessionLease = await _sessionActivationService.EnsureAsync(
                new ProjectionScopeStartRequest
                {
                    RootActorId = normalizedActorId,
                    ProjectionKind = ServiceProjectionKinds.LlmSessionObservation,
                    Mode = ProjectionRuntimeMode.SessionObservation,
                    SessionId = normalizedResponseId,
                },
                ct).ConfigureAwait(false);

            return new LlmSessionObservationScopeLeasePreparation(
                normalizedActorId,
                normalizedResponseId);
        }
        catch
        {
            if (sessionLease != null)
                await _sessionReleaseService.ReleaseIfIdleAsync(sessionLease, CancellationToken.None).ConfigureAwait(false);

            return null;
        }
    }

    public Task ReleaseAsync(
        LlmSessionObservationScopeLeasePreparation preparation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ct.ThrowIfCancellationRequested();

        var sessionLease = new LlmSessionObservationRuntimeLease(new LlmSessionObservationProjectionContext
        {
            RootActorId = preparation.ActorId,
            ProjectionKind = ServiceProjectionKinds.LlmSessionObservation,
            SessionId = preparation.ResponseId,
        });

        return _sessionReleaseService.ReleaseIfIdleAsync(sessionLease, ct);
    }
}
