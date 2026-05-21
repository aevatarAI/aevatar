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
        // Refactor (iter25/cluster-002-observation-lifecycle-core):
        //   Old pattern: DefaultCommandDispatchPipeline.PrepareAsync 内 attach projection/session binder(混 read-side 关注到 pre-dispatch command 准备)
        //   New principle: 新 CQRS Core ObservationLifecycle port/phase:streaming observation attachment 移到 post-accepted dispatch 之后或独立 lifecycle;PrepareAsync 不再持有 projection/session 关注
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        var channel = new EventChannel<ScriptEvolutionSessionCompletedEvent>(capacity: 256);
        var sink = new ScriptEvolutionScopedEventSink(target.ProposalId, channel);

        try
        {
            if (!await target.ActivateReadModelAsync(ct))
            {
                await sink.DisposeAsync();
                return CommandTargetBindingResult<ScriptEvolutionStartError>.Failure(
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
                return CommandTargetBindingResult<ScriptEvolutionStartError>.Failure(
                    ScriptEvolutionStartError.ProjectionDisabled);
            }

            target.BindLiveObservation(attachment.ProjectionLease, attachment.LiveSinkLease, sink);
            return CommandTargetBindingResult<ScriptEvolutionStartError>.Success();
        }
        catch
        {
            await sink.DisposeAsync();
            throw;
        }
    }
}
