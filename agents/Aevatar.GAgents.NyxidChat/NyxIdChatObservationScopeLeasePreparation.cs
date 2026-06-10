using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.GAgents.NyxidChat;

internal sealed class NyxIdChatObservationScopeLeasePreparation<TCommand>
    : ICommandObservationScopeLeasePreparation<TCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError>
{
    private readonly IProjectionScopeActivationService<NyxIdChatSessionRuntimeLease> _activationService;
    private readonly IProjectionScopeReleaseService<NyxIdChatSessionRuntimeLease> _releaseService;
    private readonly Func<TCommand, string> _sessionIdResolver;

    public NyxIdChatObservationScopeLeasePreparation(
        IProjectionScopeActivationService<NyxIdChatSessionRuntimeLease> activationService,
        IProjectionScopeReleaseService<NyxIdChatSessionRuntimeLease> releaseService,
        Func<TCommand, string> sessionIdResolver)
    {
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
        _releaseService = releaseService ?? throw new ArgumentNullException(nameof(releaseService));
        _sessionIdResolver = sessionIdResolver ?? throw new ArgumentNullException(nameof(sessionIdResolver));
    }

    public async Task<CommandObservationScopeLeasePreparationResult<NyxIdChatStartError>> PrepareAsync(
        TCommand command,
        CommandDispatchExecution<NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt> execution,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(execution);

        var actorId = execution.Target.ActorId;
        var sessionId = _sessionIdResolver(command);
        if (string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(sessionId))
        {
            return CommandObservationScopeLeasePreparationResult<NyxIdChatStartError>.Failure(
                NyxIdChatStartError.ProjectionUnavailable);
        }

        try
        {
            var normalizedActorId = actorId.Trim();
            var normalizedSessionId = sessionId.Trim();
            var lease = await _activationService.EnsureAsync(
                new ProjectionScopeStartRequest
                {
                    RootActorId = normalizedActorId,
                    ProjectionKind = NyxIdChatProjectionKinds.ChatSession,
                    Mode = ProjectionRuntimeMode.SessionObservation,
                    SessionId = normalizedSessionId,
                },
                ct).ConfigureAwait(false);

            return CommandObservationScopeLeasePreparationResult<NyxIdChatStartError>.Success(
                new PreparedObservationScopeHandle(_releaseService, lease));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return CommandObservationScopeLeasePreparationResult<NyxIdChatStartError>.Failure(
                NyxIdChatStartError.ProjectionUnavailable);
        }
    }

    private sealed class PreparedObservationScopeHandle(
        IProjectionScopeReleaseService<NyxIdChatSessionRuntimeLease> releaseService,
        NyxIdChatSessionRuntimeLease lease)
        : ICommandObservationScopeLeasePreparationHandle
    {
        public Task ReleaseAsync(CancellationToken ct = default) =>
            releaseService.ReleaseIfIdleAsync(lease, ct);
    }
}
