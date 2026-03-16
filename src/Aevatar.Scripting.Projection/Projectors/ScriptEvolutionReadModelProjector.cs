using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Projectors;

public sealed class ScriptEvolutionReadModelProjector
    : IProjectionProjector<ScriptEvolutionSessionProjectionContext, IReadOnlyList<string>>
{
    private readonly IProjectionWriteDispatcher<ScriptEvolutionReadModel> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public ScriptEvolutionReadModelProjector(
        IProjectionWriteDispatcher<ScriptEvolutionReadModel> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
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
        if (!CommittedStateEventEnvelope.TryUnpackState<ScriptEvolutionSessionState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent == null ||
            state == null)
        {
            return;
        }

        var readModelId = ResolveReadModelId(state, context.ProposalId);
        if (string.IsNullOrWhiteSpace(readModelId))
            return;

        var now = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        var readModel = BuildReadModel(
            readModelId,
            context.RootActorId,
            state,
            stateEvent,
            now);
        await _writeDispatcher.UpsertAsync(readModel, ct);
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

    private static ScriptEvolutionReadModel BuildReadModel(
        string readModelId,
        string actorId,
        ScriptEvolutionSessionState state,
        StateEvent stateEvent,
        DateTimeOffset updatedAt)
    {
        var readModel = new ScriptEvolutionReadModel
        {
            Id = readModelId,
            ActorId = actorId ?? string.Empty,
            ProposalId = ResolveReadModelId(state, readModelId),
            ScriptId = state.ScriptId ?? string.Empty,
            BaseRevision = state.BaseRevision ?? string.Empty,
            CandidateRevision = state.CandidateRevision ?? string.Empty,
            ValidationStatus = ResolveValidationStatus(state),
            PromotionStatus = ResolvePromotionStatus(state),
            RollbackStatus = ResolveRollbackStatus(state),
            FailureReason = state.FailureReason ?? string.Empty,
            DefinitionActorId = state.DefinitionActorId ?? string.Empty,
            CatalogActorId = state.CatalogActorId ?? string.Empty,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = updatedAt,
        };
        foreach (var diagnostic in state.Diagnostics)
            readModel.Diagnostics.Add(diagnostic);
        return readModel;
    }

    private static string ResolveReadModelId(
        ScriptEvolutionSessionState state,
        string fallbackProposalId)
    {
        var proposalId = state.ProposalId ?? string.Empty;
        return string.IsNullOrWhiteSpace(proposalId)
            ? fallbackProposalId ?? string.Empty
            : proposalId;
    }

    private static string ResolveValidationStatus(ScriptEvolutionSessionState state)
    {
        if (state.ValidationSucceeded)
            return ScriptEvolutionStatuses.Validated;

        return string.Equals(state.Status, ScriptEvolutionStatuses.ValidationFailed, StringComparison.Ordinal)
            ? ScriptEvolutionStatuses.ValidationFailed
            : ScriptEvolutionStatuses.Pending;
    }

    private static string ResolvePromotionStatus(ScriptEvolutionSessionState state)
    {
        return state.Status switch
        {
            ScriptEvolutionStatuses.Promoted => ScriptEvolutionStatuses.Promoted,
            ScriptEvolutionStatuses.Rejected => ScriptEvolutionStatuses.Rejected,
            ScriptEvolutionStatuses.PromotionFailed => ScriptEvolutionStatuses.PromotionFailed,
            ScriptEvolutionStatuses.RollbackRequested => ScriptEvolutionStatuses.RollbackRequested,
            ScriptEvolutionStatuses.RolledBack => ScriptEvolutionStatuses.RolledBack,
            _ => ScriptEvolutionStatuses.Pending,
        };
    }

    private static string ResolveRollbackStatus(ScriptEvolutionSessionState state)
    {
        return state.Status switch
        {
            ScriptEvolutionStatuses.RollbackRequested => ScriptEvolutionStatuses.RollbackRequested,
            ScriptEvolutionStatuses.RolledBack => ScriptEvolutionStatuses.RolledBack,
            _ => string.Empty,
        };
    }
}
