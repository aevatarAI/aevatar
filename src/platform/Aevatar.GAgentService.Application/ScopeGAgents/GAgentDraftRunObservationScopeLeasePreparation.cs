using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;

namespace Aevatar.GAgentService.Application.ScopeGAgents;

internal sealed class GAgentDraftRunObservationScopeLeasePreparation
    : ICommandObservationScopeLeasePreparation<GAgentDraftRunCommand, GAgentDraftRunCommandTarget, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError>
{
    private readonly IGAgentDraftRunObservationScopeLeasePreparationPort _port;

    public GAgentDraftRunObservationScopeLeasePreparation(
        IGAgentDraftRunObservationScopeLeasePreparationPort port)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
    }

    public async Task<CommandObservationScopeLeasePreparationResult<GAgentDraftRunStartError>> PrepareAsync(
        GAgentDraftRunCommand command,
        CommandDispatchExecution<GAgentDraftRunCommandTarget, GAgentDraftRunAcceptedReceipt> execution,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(execution);

        var preparation = await _port.PrepareAsync(
            execution.Target.ActorId,
            execution.Context.CommandId,
            execution.Context.CorrelationId,
            ct).ConfigureAwait(false);
        if (preparation == null)
            return CommandObservationScopeLeasePreparationResult<GAgentDraftRunStartError>.Failure(
                GAgentDraftRunStartError.ProjectionUnavailable);

        return CommandObservationScopeLeasePreparationResult<GAgentDraftRunStartError>.Success(
            new PreparedObservationScopeHandle(_port, preparation));
    }

    private sealed class PreparedObservationScopeHandle(
        IGAgentDraftRunObservationScopeLeasePreparationPort port,
        Aevatar.GAgentService.Abstractions.ScopeGAgents.GAgentDraftRunObservationScopeLeasePreparation preparation)
        : ICommandObservationScopeLeasePreparationHandle
    {
        public Task ReleaseAsync(CancellationToken ct = default) =>
            port.ReleaseAsync(preparation, ct);
    }
}
