using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Projection.Projectors;

public sealed class ServiceInvocationCatalogProjector
    : IProjectionArtifactMaterializer<ServiceInvocationCatalogProjectionContext>
{
    private readonly IProjectionWriteDispatcher<ServiceInvocationCatalogReadModel> _storeDispatcher;
    private readonly IProjectionClock _clock;

    public ServiceInvocationCatalogProjector(
        IProjectionWriteDispatcher<ServiceInvocationCatalogReadModel> storeDispatcher,
        IProjectionClock clock)
    {
        _storeDispatcher = storeDispatcher ?? throw new ArgumentNullException(nameof(storeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        ServiceInvocationCatalogProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        if (!CommittedStateEventEnvelope.TryUnpackState<ServiceInvocationCatalogState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent == null ||
            state == null ||
            state.Identity == null)
        {
            return;
        }

        var serviceKey = ServiceKeys.Build(state.Identity);
        if (string.IsNullOrWhiteSpace(serviceKey))
            return;

        var readModel = new ServiceInvocationCatalogReadModel
        {
            Id = serviceKey,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            TenantId = state.Identity.TenantId ?? string.Empty,
            AppId = state.Identity.AppId ?? string.Empty,
            Namespace = state.Identity.Namespace ?? string.Empty,
            ServiceId = state.Identity.ServiceId ?? string.Empty,
            ServiceKey = serviceKey,
            ObservedAt = state.ObservedAt == null
                ? CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow)
                : state.ObservedAt.ToDateTimeOffset(),
            SourceCatalogVersion = state.SourceCatalogVersion,
            SourceServingVersion = state.SourceServingVersion,
            SourceRevisionVersion = state.SourceRevisionVersion,
            Entries = state.Entries
                .OrderBy(x => x.EndpointId ?? string.Empty, StringComparer.Ordinal)
                .Select(x => MapEntry(serviceKey, x))
                .ToList(),
        };
        await _storeDispatcher.UpsertAsync(readModel, ct);
    }

    private static ServiceInvocationReadinessEntryReadModel MapEntry(
        string serviceKey,
        ServiceInvocationCatalogEntryState entry) =>
        new()
        {
            ServiceKey = serviceKey,
            EndpointId = entry.EndpointId ?? string.Empty,
            ReadinessStatus = entry.ReadinessStatus,
            UnavailableReason = entry.UnavailableReason,
            SelectedRevisionId = entry.SelectedRevisionId ?? string.Empty,
            SelectedDeploymentId = entry.SelectedDeploymentId ?? string.Empty,
            SelectedActorId = entry.SelectedActorId ?? string.Empty,
        };
}
