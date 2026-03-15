using Aevatar.Scripting.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Projection.Projectors;

public sealed class ScriptDefinitionSnapshotProjector
    : IProjectionProjector<ScriptAuthorityProjectionContext, IReadOnlyList<string>>
{
    private readonly IProjectionWriteDispatcher<ScriptDefinitionSnapshotDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public ScriptDefinitionSnapshotProjector(
        IProjectionWriteDispatcher<ScriptDefinitionSnapshotDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ValueTask InitializeAsync(ScriptAuthorityProjectionContext context, CancellationToken ct = default)
    {
        _ = context;
        _ = ct;
        return ValueTask.CompletedTask;
    }

    public async ValueTask ProjectAsync(
        ScriptAuthorityProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.Payload?.Is(ScriptDefinitionUpsertedEvent.Descriptor) != true)
            return;

        var evt = envelope.Payload.Unpack<ScriptDefinitionUpsertedEvent>();
        var updatedAt = EventEnvelopeTimestampResolver.Resolve(envelope, _clock.UtcNow);
        await _writeDispatcher.UpsertAsync(
            new ScriptDefinitionSnapshotDocument
            {
                Id = context.RootActorId,
                ScriptId = evt.ScriptId ?? string.Empty,
                DefinitionActorId = context.RootActorId,
                Revision = evt.ScriptRevision ?? string.Empty,
                SourceText = evt.SourceText ?? string.Empty,
                SourceHash = evt.SourceHash ?? string.Empty,
                StateTypeUrl = evt.StateTypeUrl ?? string.Empty,
                ReadModelTypeUrl = evt.ReadModelTypeUrl ?? string.Empty,
                ReadModelSchemaVersion = evt.ReadModelSchemaVersion ?? string.Empty,
                ReadModelSchemaHash = evt.ReadModelSchemaHash ?? string.Empty,
                ScriptPackage = evt.ScriptPackage?.Clone() ?? new ScriptPackageSpec(),
                ProtocolDescriptorSetBase64 = (evt.ProtocolDescriptorSet ?? ByteString.Empty).ToBase64(),
                StateDescriptorFullName = evt.StateDescriptorFullName ?? string.Empty,
                ReadModelDescriptorFullName = evt.ReadModelDescriptorFullName ?? string.Empty,
                RuntimeSemantics = evt.RuntimeSemantics?.Clone() ?? new ScriptRuntimeSemanticsSpec(),
                StateVersion = 1,
                LastEventId = evt.ScriptRevision ?? string.Empty,
                CreatedAt = updatedAt,
                UpdatedAt = updatedAt,
            },
            ct);
    }

    public ValueTask CompleteAsync(
        ScriptAuthorityProjectionContext context,
        IReadOnlyList<string> projectionResult,
        CancellationToken ct = default)
    {
        _ = context;
        _ = projectionResult;
        _ = ct;
        return ValueTask.CompletedTask;
    }
}
