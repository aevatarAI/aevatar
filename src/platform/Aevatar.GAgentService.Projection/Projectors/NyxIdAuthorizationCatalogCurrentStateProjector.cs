using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Core.Schedules.Authorization;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Projectors;

public sealed class NyxIdAuthorizationCatalogCurrentStateProjector
    : ICurrentStateProjectionMaterializer<NyxIdAuthorizationCatalogProjectionContext>
{
    private readonly IProjectionWriteDispatcher<NyxIdAuthorizationCatalogDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public NyxIdAuthorizationCatalogCurrentStateProjector(
        IProjectionWriteDispatcher<NyxIdAuthorizationCatalogDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        NyxIdAuthorizationCatalogProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        if (!CommittedStateEventEnvelope.TryUnpackState<NyxIdAuthorizationCatalogState>(
                envelope, out _, out var stateEvent, out var state) ||
            stateEvent == null ||
            state?.Owner == null)
        {
            return;
        }

        var document = new NyxIdAuthorizationCatalogDocument
        {
            Id = context.RootActorId,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow),
            Owner = state.Owner.Clone(),
            ContractVersion = state.ContractVersion,
            PolicyVersion = state.PolicyVersion,
            ContentDigest = state.ContentDigest,
            Invalidated = state.Invalidated,
            InvalidationReason = state.InvalidationReason,
            InvalidatedAt = state.InvalidatedAt?.ToDateTimeOffset(),
            LastRefreshFailedAt = state.LastRefreshFailedAt?.ToDateTimeOffset(),
            LastRefreshFailureCode = state.LastRefreshFailureCode,
            LifecycleFence = state.LifecycleFence,
            Activated = state.Activated,
            ActivatedAt = state.ActivatedAt?.ToDateTimeOffset(),
            Cleaned = state.Cleaned,
            CleanedAt = state.CleanedAt?.ToDateTimeOffset(),
            CleanupReason = state.CleanupReason,
        };
        if (state.GatewayLlmTarget != null)
            document.GatewayLlmTarget = state.GatewayLlmTarget.Clone();
        if (state.ObservedAt != null)
            document.ObservedAt = state.ObservedAt.ToDateTimeOffset();
        if (state.FreshUntil != null)
            document.FreshUntil = state.FreshUntil.ToDateTimeOffset();
        if (state.EvaluatedAt != null)
            document.EvaluatedAt = state.EvaluatedAt.ToDateTimeOffset();
        document.Services.Add(state.Services.Select(static service => service.Clone()));

        var result = await _writeDispatcher.UpsertAsync(document, ct);
        if (result.IsRejected)
        {
            throw new InvalidOperationException(
                $"NyxID authorization catalog projection rejected state version {stateEvent.Version}: {result.Disposition}.");
        }
    }
}
