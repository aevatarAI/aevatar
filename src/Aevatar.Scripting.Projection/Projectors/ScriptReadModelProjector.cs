using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Serialization;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Projection.Projectors;

public sealed class ScriptReadModelProjector
    : IProjectionProjector<ScriptExecutionProjectionContext, IReadOnlyList<string>>
{
    private readonly IProjectionWriteDispatcher<ScriptReadModelDocument> _writeDispatcher;
    private readonly IScriptDefinitionSnapshotPort _definitionSnapshotPort;
    private readonly IScriptBehaviorArtifactResolver _artifactResolver;
    private readonly IProtobufMessageCodec _codec;
    private readonly IProjectionClock _clock;

    public ScriptReadModelProjector(
        IProjectionWriteDispatcher<ScriptReadModelDocument> writeDispatcher,
        IScriptDefinitionSnapshotPort definitionSnapshotPort,
        IScriptBehaviorArtifactResolver artifactResolver,
        IProtobufMessageCodec codec,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _definitionSnapshotPort = definitionSnapshotPort ?? throw new ArgumentNullException(nameof(definitionSnapshotPort));
        _artifactResolver = artifactResolver ?? throw new ArgumentNullException(nameof(artifactResolver));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ValueTask InitializeAsync(
        ScriptExecutionProjectionContext context,
        CancellationToken ct = default)
    {
        _ = context;
        _ = ct;
        return ValueTask.CompletedTask;
    }

    public async ValueTask ProjectAsync(
        ScriptExecutionProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        if (!CommittedStateEventEnvelope.TryUnpackState<ScriptBehaviorState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent?.EventData?.Is(ScriptDomainFactCommitted.Descriptor) != true ||
            state == null)
        {
            return;
        }

        var fact = stateEvent.EventData.Unpack<ScriptDomainFactCommitted>();
        var snapshot = await _definitionSnapshotPort.GetRequiredAsync(
            state.DefinitionActorId,
            state.Revision,
            ct);
        var scriptPackage = ScriptPackageModel.ResolveDeclaredPackage(
            snapshot.ScriptPackage,
            snapshot.SourceText);
        var artifact = _artifactResolver.Resolve(new ScriptBehaviorArtifactRequest(
            snapshot.ScriptId,
            snapshot.Revision,
            scriptPackage,
            snapshot.SourceHash));
        var semanticReadModel = await ScriptCommittedStateProjectionSupport.BuildSemanticReadModelAsync(
            context.RootActorId,
            state,
            fact,
            artifact,
            _codec);
        var document = new ScriptReadModelDocument
        {
            Id = context.RootActorId,
            ScriptId = state.ScriptId ?? string.Empty,
            DefinitionActorId = state.DefinitionActorId ?? string.Empty,
            Revision = state.Revision ?? string.Empty,
            ReadModelTypeUrl = string.IsNullOrWhiteSpace(state.ReadModelTypeUrl)
                ? fact.ReadModelTypeUrl ?? string.Empty
                : state.ReadModelTypeUrl,
            ReadModelPayload = _codec.Pack(semanticReadModel)?.Clone() ?? Any.Pack(new Empty()),
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow),
        };

        await _writeDispatcher.UpsertAsync(document, ct);
    }

    public ValueTask CompleteAsync(
        ScriptExecutionProjectionContext context,
        IReadOnlyList<string> topology,
        CancellationToken ct = default)
    {
        _ = context;
        _ = topology;
        _ = ct;
        return ValueTask.CompletedTask;
    }
}
