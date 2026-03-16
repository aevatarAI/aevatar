using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;
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

        if (!CommittedStateEventEnvelope.TryUnpackState<ScriptDefinitionState>(
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
        await _writeDispatcher.UpsertAsync(
            new ScriptDefinitionSnapshotDocument
            {
                Id = context.RootActorId,
                ScriptId = state.ScriptId ?? string.Empty,
                DefinitionActorId = context.RootActorId,
                Revision = state.Revision ?? string.Empty,
                SourceText = state.SourceText ?? string.Empty,
                SourceHash = state.SourceHash ?? string.Empty,
                StateTypeUrl = state.StateTypeUrl ?? string.Empty,
                ReadModelTypeUrl = state.ReadModelTypeUrl ?? string.Empty,
                ReadModelSchemaVersion = state.ReadModelSchemaVersion ?? string.Empty,
                ReadModelSchemaHash = state.ReadModelSchemaHash ?? string.Empty,
                ScriptPackage = state.ScriptPackage?.Clone() ?? new ScriptPackageSpec(),
                ProtocolDescriptorSetBase64 = (state.ProtocolDescriptorSet ?? ByteString.Empty).ToBase64(),
                StateDescriptorFullName = state.StateDescriptorFullName ?? string.Empty,
                ReadModelDescriptorFullName = state.ReadModelDescriptorFullName ?? string.Empty,
                RuntimeSemantics = state.RuntimeSemantics?.Clone() ?? new ScriptRuntimeSemanticsSpec(),
                StateVersion = stateEvent.Version,
                LastEventId = stateEvent.EventId ?? string.Empty,
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
