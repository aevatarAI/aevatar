using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Abstractions.Evolution;
using Aevatar.Scripting.Application;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class ScriptEvolutionObservationLifecycle
    : ICommandObservationLifecycle<ScriptEvolutionProposal, ScriptEvolutionCommandTarget, ScriptEvolutionAcceptedReceipt, ScriptEvolutionStartError>
{
    private readonly IScriptEvolutionProjectionPort _projectionPort;

    public ScriptEvolutionObservationLifecycle(IScriptEvolutionProjectionPort projectionPort)
    {
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public async Task<CommandObservationBindingResult<ScriptEvolutionStartError>> BindAsync(
        ScriptEvolutionProposal command,
        CommandDispatchExecution<ScriptEvolutionCommandTarget, ScriptEvolutionAcceptedReceipt> execution,
        CancellationToken ct = default)
    {
        // Refactor (iter25/cluster-002-observation-lifecycle-core):
        //   Old pattern: script binder activated readmodel and live projections during command preparation.
        //   New principle: interaction observation lifecycle starts read-side observation before dispatch without affecting dispatch-only command admission.
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(execution);

        var target = execution.Target;
        var channel = new EventChannel<ScriptEvolutionSessionCompletedEvent>(capacity: 256);
        var sink = new ScriptEvolutionScopedEventSink(target.ProposalId, channel);

        try
        {
            if (!await target.ActivateReadModelAsync(ct))
            {
                await sink.DisposeAsync();
                return CommandObservationBindingResult<ScriptEvolutionStartError>.Failure(
                    ScriptEvolutionStartError.ProjectionDisabled);
            }

            var attachment = await _projectionPort.EnsureAndAttachLeaseAsync(
                token => _projectionPort.EnsureActorProjectionAsync(
                    target.SessionActorId,
                    target.ProposalId,
                    token),
                sink,
                ct);

            if (attachment == null)
            {
                await sink.DisposeAsync();
                return CommandObservationBindingResult<ScriptEvolutionStartError>.Failure(
                    ScriptEvolutionStartError.ProjectionDisabled);
            }

            target.BindLiveObservation(attachment.ProjectionLease, attachment.LiveSinkLease, sink);
            return CommandObservationBindingResult<ScriptEvolutionStartError>.Success();
        }
        catch
        {
            await sink.DisposeAsync();
            throw;
        }
    }
}
