using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Abstractions.Evolution;
using Aevatar.Scripting.Application;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class ScriptEvolutionCommandTargetBinder
    : ICommandTargetBinder<ScriptEvolutionProposal, ScriptEvolutionCommandTarget, ScriptEvolutionStartError>
{
    private readonly IScriptEvolutionProjectionPort _projectionPort;

    public ScriptEvolutionCommandTargetBinder(IScriptEvolutionProjectionPort projectionPort)
    {
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public async Task<CommandTargetBindingResult<ScriptEvolutionStartError>> BindAsync(
        ScriptEvolutionProposal command,
        ScriptEvolutionCommandTarget target,
        CommandContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        var channel = new EventChannel<ScriptEvolutionSessionCompletedEvent>(capacity: 256);
        var sink = new ScriptEvolutionScopedEventSink(target.ProposalId, channel);

        try
        {
            var projectionLease = await _projectionPort.EnsureAndAttachAsync(
                token => _projectionPort.EnsureActorProjectionAsync(
                    target.SessionActorId,
                    target.ProposalId,
                    token),
                sink,
                ct);

            if (projectionLease == null)
            {
                await sink.DisposeAsync();
                return CommandTargetBindingResult<ScriptEvolutionStartError>.Failure(
                    ScriptEvolutionStartError.ProjectionDisabled);
            }

            target.BindLiveObservation(projectionLease, sink);
            return CommandTargetBindingResult<ScriptEvolutionStartError>.Success();
        }
        catch
        {
            await sink.DisposeAsync();
            throw;
        }
    }
}
