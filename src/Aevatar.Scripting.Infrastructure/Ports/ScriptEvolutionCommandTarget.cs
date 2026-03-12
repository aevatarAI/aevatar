using System.Runtime.ExceptionServices;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Abstractions.Evolution;
using Aevatar.Scripting.Application;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class ScriptEvolutionCommandTarget
    : IActorCommandDispatchTarget,
      ICommandEventTarget<ScriptEvolutionSessionCompletedEvent>,
      ICommandInteractionCleanupTarget<ScriptEvolutionAcceptedReceipt, ScriptEvolutionInteractionCompletion>,
      ICommandDispatchCleanupAware
{
    private readonly IScriptEvolutionProjectionPort _projectionPort;

    public ScriptEvolutionCommandTarget(
        IActor actor,
        string managerActorId,
        string proposalId,
        IScriptEvolutionProjectionPort projectionPort)
    {
        Actor = actor ?? throw new ArgumentNullException(nameof(actor));
        ManagerActorId = string.IsNullOrWhiteSpace(managerActorId)
            ? throw new ArgumentException("Manager actor id is required.", nameof(managerActorId))
            : managerActorId;
        ProposalId = string.IsNullOrWhiteSpace(proposalId)
            ? throw new ArgumentException("Proposal id is required.", nameof(proposalId))
            : proposalId;
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public IActor Actor { get; }
    public string ManagerActorId { get; }
    public string ProposalId { get; }
    public string TargetId => Actor.Id;
    public string SessionActorId => Actor.Id;
    public IScriptEvolutionProjectionLease? ProjectionLease { get; private set; }
    public IEventSink<ScriptEvolutionSessionCompletedEvent>? LiveSink { get; private set; }

    public void BindLiveObservation(
        IScriptEvolutionProjectionLease lease,
        IEventSink<ScriptEvolutionSessionCompletedEvent> sink)
    {
        ProjectionLease = lease ?? throw new ArgumentNullException(nameof(lease));
        LiveSink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public IEventSink<ScriptEvolutionSessionCompletedEvent> RequireLiveSink() =>
        LiveSink ?? throw new InvalidOperationException("Script evolution live sink is not bound.");

    public Task CleanupAfterDispatchFailureAsync(CancellationToken ct = default) =>
        ReleaseAsync(ct);

    public Task ReleaseAfterInteractionAsync(
        ScriptEvolutionAcceptedReceipt receipt,
        CommandInteractionCleanupContext<ScriptEvolutionInteractionCompletion> cleanup,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(cleanup);
        return ReleaseAsync(ct);
    }

    public async Task ReleaseAsync(CancellationToken ct = default)
    {
        Exception? firstException = null;

        if (ProjectionLease != null && LiveSink != null)
        {
            try
            {
                await _projectionPort.DetachReleaseAndDisposeAsync(
                    ProjectionLease,
                    LiveSink,
                    onDetachedAsync: null,
                    ct);
            }
            catch (Exception ex)
            {
                firstException ??= ex;
            }

            ProjectionLease = null;
            LiveSink = null;
        }
        else
        {
            if (LiveSink != null)
            {
                try
                {
                    await LiveSink.DisposeAsync();
                }
                catch (Exception ex)
                {
                    firstException ??= ex;
                }

                LiveSink = null;
            }

            if (ProjectionLease != null)
            {
                try
                {
                    await _projectionPort.ReleaseActorProjectionAsync(ProjectionLease, ct);
                }
                catch (Exception ex)
                {
                    firstException ??= ex;
                }

                ProjectionLease = null;
            }
        }

        if (firstException != null)
            ExceptionDispatchInfo.Capture(firstException).Throw();
    }
}
