using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Projection.Projectors;

public sealed class ScriptDefinitionSnapshotProjector
    : ICurrentStateProjectionMaterializer<ScriptAuthorityProjectionContext>
{
    // Refactor (iter42/cluster-044-scripting-source-package-json-shadow):
    //   Old pattern: Scripting persists and republishes source_text as a compatibility shadow of ScriptPackageSpec; multi-file packages can be encoded as JSON text and reparsed from persisted source.
    //   New principle: ScriptPackageSpec is the sole internal source-package contract for commands/state/events/readmodels; source_text is only an external one-file adapter field at Host/Application boundary.
    private readonly IProjectionWriteDispatcher<ScriptDefinitionSnapshotDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public ScriptDefinitionSnapshotProjector(
        IProjectionWriteDispatcher<ScriptDefinitionSnapshotDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
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
                ScopeId = state.ScopeId ?? string.Empty,
                StateVersion = stateEvent.Version,
                LastEventId = stateEvent.EventId ?? string.Empty,
                CreatedAt = updatedAt,
                UpdatedAt = updatedAt,
            },
            ct);
    }

}
