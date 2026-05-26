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
        // Refactor (iter41/cluster-041-command-observation-projection-activation):
        //   Old pattern: command observation binders ensure/activate projection/readmodel sessions before dispatch.
        //   New principle: observation binders attach only to existing projection-owned sessions;
        //   activation happens in projection-owned startup/background/committed-state lifecycle.
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(execution);

        var target = execution.Target;
        var channel = new EventChannel<ScriptEvolutionSessionCompletedEvent>(capacity: 256);
        var sink = new ScriptEvolutionScopedEventSink(target.ProposalId, channel);

        try
        {
            var attachment = await _projectionPort.AttachExistingActorProjectionAsync(
                target.SessionActorId,
                target.ProposalId,
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
