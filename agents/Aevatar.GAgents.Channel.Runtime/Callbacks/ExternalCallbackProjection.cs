using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Channel.Runtime;

public sealed partial class ExternalCallbackCurrentStateDocument : IProjectionReadModel<ExternalCallbackCurrentStateDocument>
{
    public string ActorId => Id;
    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtc?.ToDateTimeOffset() ?? default;
        set => UpdatedAtUtc = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}

public sealed class ExternalCallbackMaterializationContext : IProjectionMaterializationContext
{
    public required string RootActorId { get; init; }
    public required string ProjectionKind { get; init; }
}

public sealed class ExternalCallbackMaterializationLease(ExternalCallbackMaterializationContext context)
    : ProjectionRuntimeLeaseBase(context.RootActorId), IProjectionContextRuntimeLease<ExternalCallbackMaterializationContext>
{
    public ExternalCallbackMaterializationContext Context { get; } = context;
}

public sealed class ExternalCallbackCurrentStateProjector(
    IProjectionWriteDispatcher<ExternalCallbackCurrentStateDocument> writer,
    IProjectionClock clock) : ICurrentStateProjectionMaterializer<ExternalCallbackMaterializationContext>
{
    public async ValueTask ProjectAsync(ExternalCallbackMaterializationContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        if (!CommittedStateEventEnvelope.TryUnpackState<ExternalCallbackState>(envelope, out _, out var evt, out var state) ||
            evt is null || state?.Snapshot is not { } snapshot)
            return;
        if (snapshot.OperationActorId != context.RootActorId)
            throw new InvalidOperationException("Callback snapshot authority does not match its actor.");
        var write = await writer.UpsertAsync(new ExternalCallbackCurrentStateDocument
        {
            Id = context.RootActorId, StateVersion = evt.Version, LastEventId = evt.EventId,
            UpdatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, clock.UtcNow),
            CallbackId = snapshot.CallbackId, ExternalRequestId = snapshot.ExternalRequestId, Snapshot = snapshot.Clone(),
        }, ct);
        if (write.IsRejected)
            throw new InvalidOperationException($"External callback projection rejected source version {evt.Version}: {write.Disposition}.");
    }
}

public sealed class ExternalCallbackDocumentMetadataProvider : IProjectionDocumentMetadataProvider<ExternalCallbackCurrentStateDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "channel-external-callback-current-state",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal) { ["dynamic"] = true },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
