using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed class UserAgentCatalogProjector
    : ICurrentStateProjectionMaterializer<UserAgentCatalogMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<AgentRegistryDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public UserAgentCatalogProjector(
        IProjectionWriteDispatcher<AgentRegistryDocument> writeDispatcher,
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

        if (!CommittedStateEventEnvelope.TryUnpackState<AgentRegistryState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent == null ||
            state == null)
        {
            return;
        }

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        foreach (var entry in state.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.AgentId))
                continue;

            var document = new AgentRegistryDocument
            {
                Id = entry.AgentId,
                Platform = entry.Platform ?? string.Empty,
                ConversationId = entry.ConversationId ?? string.Empty,
                NyxProviderSlug = entry.NyxProviderSlug ?? string.Empty,
                NyxApiKey = entry.NyxApiKey ?? string.Empty,
                OwnerNyxUserId = entry.OwnerNyxUserId ?? string.Empty,
                AgentType = entry.AgentType ?? string.Empty,
                TemplateName = entry.TemplateName ?? string.Empty,
                ScopeId = entry.ScopeId ?? string.Empty,
                ApiKeyId = entry.ApiKeyId ?? string.Empty,
                ScheduleCron = entry.ScheduleCron ?? string.Empty,
                ScheduleTimezone = entry.ScheduleTimezone ?? string.Empty,
                Status = entry.Status ?? string.Empty,
                LastRunAtUtc = entry.LastRunAt,
                NextRunAtUtc = entry.NextRunAt,
                ErrorCount = entry.ErrorCount,
                LastError = entry.LastError ?? string.Empty,
                Tombstoned = entry.Tombstoned,
                StateVersion = stateEvent.Version,
                LastEventId = stateEvent.EventId ?? string.Empty,
                ActorId = context.RootActorId,
                UpdatedAt = updatedAt,
                CreatedAt = entry.CreatedAt != null ? entry.CreatedAt.ToDateTimeOffset() : updatedAt,
            };

            await _writeDispatcher.UpsertAsync(document, ct);
        }
    }
}
