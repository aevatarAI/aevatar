using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class GAgentDraftRunObservationScopeLeasePreparationPort
    : IGAgentDraftRunObservationScopeLeasePreparationPort
{
    private readonly IProjectionScopeActivationService<GAgentDraftRunRuntimeLease> _sessionActivationService;
    private readonly IProjectionScopeReleaseService<GAgentDraftRunRuntimeLease> _sessionReleaseService;
    private readonly IProjectionScopeActivationService<ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>> _terminalActivationService;
    private readonly IProjectionScopeReleaseService<ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>> _terminalReleaseService;

    public GAgentDraftRunObservationScopeLeasePreparationPort(
        IProjectionScopeActivationService<GAgentDraftRunRuntimeLease> sessionActivationService,
        IProjectionScopeReleaseService<GAgentDraftRunRuntimeLease> sessionReleaseService,
        IProjectionScopeActivationService<ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>> terminalActivationService,
        IProjectionScopeReleaseService<ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>> terminalReleaseService)
    {
        _sessionActivationService = sessionActivationService ?? throw new ArgumentNullException(nameof(sessionActivationService));
        _sessionReleaseService = sessionReleaseService ?? throw new ArgumentNullException(nameof(sessionReleaseService));
        _terminalActivationService = terminalActivationService ?? throw new ArgumentNullException(nameof(terminalActivationService));
        _terminalReleaseService = terminalReleaseService ?? throw new ArgumentNullException(nameof(terminalReleaseService));
    }

    public async Task<GAgentDraftRunObservationScopeLeasePreparation?> PrepareAsync(
        string actorId,
        string commandId,
        string correlationId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId) ||
            string.IsNullOrWhiteSpace(commandId) ||
            string.IsNullOrWhiteSpace(correlationId))
        {
            return null;
        }

        GAgentDraftRunRuntimeLease? sessionLease = null;
        ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>? terminalLease = null;
        try
        {
            var normalizedActorId = actorId.Trim();
            var normalizedCommandId = commandId.Trim();
            var normalizedCorrelationId = correlationId.Trim();
            sessionLease = await _sessionActivationService.EnsureAsync(
                new ProjectionScopeStartRequest
                {
                    RootActorId = normalizedActorId,
                    ProjectionKind = ServiceProjectionKinds.DraftRunSession,
                    Mode = ProjectionRuntimeMode.SessionObservation,
                    SessionId = normalizedCommandId,
                },
                ct).ConfigureAwait(false);
            terminalLease = await _terminalActivationService.EnsureAsync(
                new ProjectionScopeStartRequest
                {
                    RootActorId = normalizedActorId,
                    ProjectionKind = ServiceProjectionKinds.GAgentRunTerminalDraftRun,
                    Mode = ProjectionRuntimeMode.DurableMaterialization,
                    SessionId = normalizedCorrelationId,
                },
                ct).ConfigureAwait(false);

            return new GAgentDraftRunObservationScopeLeasePreparation(
                normalizedActorId,
                normalizedCommandId,
                normalizedCorrelationId);
        }
        catch
        {
            if (terminalLease != null)
                await _terminalReleaseService.ReleaseIfIdleAsync(terminalLease, CancellationToken.None).ConfigureAwait(false);

            if (sessionLease != null)
                await _sessionReleaseService.ReleaseIfIdleAsync(sessionLease, CancellationToken.None).ConfigureAwait(false);

            return null;
        }
    }

    public async Task ReleaseAsync(
        GAgentDraftRunObservationScopeLeasePreparation preparation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ct.ThrowIfCancellationRequested();

        var sessionLease = new GAgentDraftRunRuntimeLease(new GAgentDraftRunProjectionContext
        {
            RootActorId = preparation.ActorId,
            ProjectionKind = ServiceProjectionKinds.DraftRunSession,
            SessionId = preparation.CommandId,
        });
        var terminalLease = new ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>(
            preparation.ActorId,
            new GAgentRunTerminalProjectionContext
            {
                RootActorId = preparation.ActorId,
                ProjectionKind = ServiceProjectionKinds.GAgentRunTerminalDraftRun,
                CorrelationId = preparation.CorrelationId,
                InteractionKind = GAgentRunTerminalInteractionKind.DraftRun,
            });

        await _terminalReleaseService.ReleaseIfIdleAsync(terminalLease, ct).ConfigureAwait(false);
        await _sessionReleaseService.ReleaseIfIdleAsync(sessionLease, ct).ConfigureAwait(false);
    }
}
