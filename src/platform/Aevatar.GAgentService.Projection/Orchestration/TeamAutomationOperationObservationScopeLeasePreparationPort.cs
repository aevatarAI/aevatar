using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class TeamAutomationOperationObservationScopeLeasePreparationPort
    : ITeamAutomationOperationObservationScopeLeasePreparationPort
{
    private readonly IProjectionScopeActivationService<TeamAutomationOperationObservationRuntimeLease>
        _activationService;
    private readonly IProjectionScopeReleaseService<TeamAutomationOperationObservationRuntimeLease>
        _releaseService;
    private readonly ILogger<TeamAutomationOperationObservationScopeLeasePreparationPort> _logger;

    public TeamAutomationOperationObservationScopeLeasePreparationPort(
        IProjectionScopeActivationService<TeamAutomationOperationObservationRuntimeLease> activationService,
        IProjectionScopeReleaseService<TeamAutomationOperationObservationRuntimeLease> releaseService,
        ILogger<TeamAutomationOperationObservationScopeLeasePreparationPort>? logger = null)
    {
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
        _releaseService = releaseService ?? throw new ArgumentNullException(nameof(releaseService));
        _logger = logger ?? NullLogger<TeamAutomationOperationObservationScopeLeasePreparationPort>.Instance;
    }

    public async Task<TeamAutomationOperationObservationScopeLeasePreparation?> PrepareAsync(
        string actorId,
        string operationId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(operationId))
            return null;

        TeamAutomationOperationObservationRuntimeLease? lease = null;
        try
        {
            var normalizedActorId = actorId.Trim();
            var normalizedOperationId = operationId.Trim();
            lease = await _activationService.EnsureAsync(
                new ProjectionScopeStartRequest
                {
                    RootActorId = normalizedActorId,
                    ProjectionKind = ServiceProjectionKinds.TeamAutomationOperationObservation,
                    Mode = ProjectionRuntimeMode.SessionObservation,
                    SessionId = normalizedOperationId,
                },
                ct).ConfigureAwait(false);

            return new TeamAutomationOperationObservationScopeLeasePreparation(
                normalizedActorId,
                normalizedOperationId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to prepare a team automation operation observation scope lease for actor {ActorId} and operation {OperationId}.",
                actorId,
                operationId);
            if (lease != null)
                await _releaseService.ReleaseIfIdleAsync(lease, CancellationToken.None).ConfigureAwait(false);

            return null;
        }
    }

    public Task ReleaseAsync(
        TeamAutomationOperationObservationScopeLeasePreparation preparation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ct.ThrowIfCancellationRequested();

        var lease = new TeamAutomationOperationObservationRuntimeLease(
            new TeamAutomationOperationObservationProjectionContext
            {
                RootActorId = preparation.ActorId,
                ProjectionKind = ServiceProjectionKinds.TeamAutomationOperationObservation,
                SessionId = preparation.OperationId,
            });
        return _releaseService.ReleaseIfIdleAsync(lease, ct);
    }
}
