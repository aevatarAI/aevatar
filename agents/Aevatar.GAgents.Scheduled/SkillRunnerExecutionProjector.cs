using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.GAgents.Scheduled;

// Refactor (iter94/cluster-094a):
//   Old: runner execution fields were tempting to fold into the catalog readmodel/query port.
//   New: SkillRunner committed state owns and materializes execution readmodel rows independently.
public sealed class SkillRunnerExecutionProjector
    : ICurrentStateProjectionMaterializer<UserAgentCatalogMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<SkillRunnerExecutionDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public SkillRunnerExecutionProjector(
        IProjectionWriteDispatcher<SkillRunnerExecutionDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        UserAgentCatalogMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpackState<SkillRunnerState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent is null ||
            state is null ||
            !TryResolveRunnerActorId(context, envelope, out var agentId))
        {
            return;
        }

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        await _writeDispatcher.UpsertAsync(
            Materialize(agentId, state, stateEvent, updatedAt),
            ct);
    }

    private static SkillRunnerExecutionDocument Materialize(
        string agentId,
        SkillRunnerState state,
        StateEvent stateEvent,
        DateTimeOffset updatedAt)
    {
        var document = new SkillRunnerExecutionDocument
        {
            Id = agentId,
            ActorId = agentId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = updatedAt,
            CreatedAt = updatedAt,
            TemplateName = state.TemplateName ?? string.Empty,
            ScopeId = state.ScopeId ?? string.Empty,
            ScheduleCron = state.ScheduleCron ?? string.Empty,
            ScheduleTimezone = state.ScheduleTimezone ?? string.Empty,
            Status = ResolveRunnerStatus(state),
            LastRunAtUtc = state.LastRunAt,
            NextRunAtUtc = state.NextRunAt,
            ErrorCount = state.ErrorCount,
            LastError = state.LastError ?? string.Empty,
            ScheduleMode = state.ScheduleMode,
            RunAtUtc = state.OneShotRunAt,
            RetiredAtUtc = state.RetiredAt,
            RetirementReason = state.RetirementReason ?? string.Empty,
            LastSuccessfulDelivery = state.LastSuccessfulDelivery?.Clone(),
        };
        document.RecentDeliveries.AddRange(state.RecentDeliveries.Select(static entry => entry.Clone()));
        return document;
    }

    private static bool TryResolveRunnerActorId(
        UserAgentCatalogMaterializationContext context,
        EventEnvelope envelope,
        out string agentId)
    {
        agentId = string.Empty;
        var rootActorId = context.RootActorId?.Trim();
        if (!string.IsNullOrWhiteSpace(rootActorId) &&
            !string.Equals(rootActorId, UserAgentCatalogGAgent.WellKnownId, StringComparison.Ordinal))
        {
            agentId = rootActorId;
            return true;
        }

        var publisherActorId = envelope.Route?.PublisherActorId?.Trim();
        if (string.IsNullOrWhiteSpace(publisherActorId) ||
            string.Equals(publisherActorId, UserAgentCatalogGAgent.WellKnownId, StringComparison.Ordinal))
        {
            return false;
        }

        agentId = publisherActorId;
        return true;
    }

    private static string ResolveRunnerStatus(SkillRunnerState state)
    {
        if (state.ScheduleMode == SkillRunnerScheduleMode.OneShot && state.RetiredAt != null)
            return state.ErrorCount > 0
                ? SkillRunnerDefaults.StatusError
                : SkillRunnerDefaults.StatusCompleted;

        if (!state.Enabled)
            return SkillRunnerDefaults.StatusDisabled;

        return state.ErrorCount > 0
            ? SkillRunnerDefaults.StatusError
            : SkillRunnerDefaults.StatusRunning;
    }
}
