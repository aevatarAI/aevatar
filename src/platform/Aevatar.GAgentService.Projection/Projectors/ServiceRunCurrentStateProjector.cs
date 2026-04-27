using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Projectors;

public sealed class ServiceRunCurrentStateProjector
    : ICurrentStateProjectionMaterializer<ServiceRunCurrentStateProjectionContext>
{
    private readonly IProjectionWriteDispatcher<ServiceRunCurrentStateReadModel> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public ServiceRunCurrentStateProjector(
        IProjectionWriteDispatcher<ServiceRunCurrentStateReadModel> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        ServiceRunCurrentStateProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        if (!CommittedStateEventEnvelope.TryUnpackState<ServiceRunState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent == null ||
            state?.Record == null)
        {
            return;
        }

        var record = state.Record;
        if (string.IsNullOrWhiteSpace(record.RunId))
            return;

        var observedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        var document = new ServiceRunCurrentStateReadModel
        {
            Id = record.RunId,
            ActorId = context.RootActorId,
            ScopeId = record.ScopeId ?? string.Empty,
            ServiceId = record.ServiceId ?? string.Empty,
            ServiceKey = record.ServiceKey ?? string.Empty,
            RunId = record.RunId,
            CommandId = record.CommandId ?? string.Empty,
            CorrelationId = record.CorrelationId ?? string.Empty,
            EndpointId = record.EndpointId ?? string.Empty,
            ImplementationKind = (int)record.ImplementationKind,
            TargetActorId = record.TargetActorId ?? string.Empty,
            RevisionId = record.RevisionId ?? string.Empty,
            DeploymentId = record.DeploymentId ?? string.Empty,
            Status = (int)record.Status,
            TenantId = record.Identity?.TenantId ?? string.Empty,
            AppId = record.Identity?.AppId ?? string.Empty,
            Namespace = record.Identity?.Namespace ?? string.Empty,
            CreatedAt = record.CreatedAt?.ToDateTimeOffset() ?? observedAt,
            UpdatedAt = record.UpdatedAt?.ToDateTimeOffset() ?? observedAt,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
        };

        await _writeDispatcher.UpsertAsync(document, ct);
    }
}
