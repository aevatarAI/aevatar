using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Orchestration;

namespace Aevatar.Scripting.Projection.Projectors;

public sealed class ScriptEvolutionSessionCompletedEventProjector
    : IProjectionProjector<ScriptEvolutionSessionProjectionContext, IReadOnlyList<string>>
{
    private readonly IProjectionSessionEventHub<ScriptEvolutionSessionCompletedEvent> _sessionEventHub;

    public ScriptEvolutionSessionCompletedEventProjector(
        IProjectionSessionEventHub<ScriptEvolutionSessionCompletedEvent> sessionEventHub)
    {
        _sessionEventHub = sessionEventHub ?? throw new ArgumentNullException(nameof(sessionEventHub));
    }

    public ValueTask InitializeAsync(
        ScriptEvolutionSessionProjectionContext context,
        CancellationToken ct = default)
    {
        _ = context;
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask ProjectAsync(
        ScriptEvolutionSessionProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);
        ct.ThrowIfCancellationRequested();

        var payload = envelope.Payload;
        if (payload == null || !payload.Is(ScriptEvolutionSessionCompletedEvent.Descriptor))
            return;

        var completed = payload.Unpack<ScriptEvolutionSessionCompletedEvent>();
        var proposalId = string.IsNullOrWhiteSpace(completed.ProposalId)
            ? context.ProposalId
            : completed.ProposalId;
        if (string.IsNullOrWhiteSpace(proposalId))
            return;

        await _sessionEventHub.PublishAsync(
            context.RootActorId,
            proposalId,
            completed,
            ct);
    }

    public ValueTask CompleteAsync(
        ScriptEvolutionSessionProjectionContext context,
        IReadOnlyList<string> topology,
        CancellationToken ct = default)
    {
        _ = context;
        _ = topology;
        _ = ct;
        return ValueTask.CompletedTask;
    }
}
