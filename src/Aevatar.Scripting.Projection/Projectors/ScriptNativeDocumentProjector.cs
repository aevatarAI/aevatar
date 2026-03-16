using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Materialization;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Serialization;
using Aevatar.Scripting.Projection.Materialization;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Projectors;

public sealed class ScriptNativeDocumentProjector
    : IProjectionProjector<ScriptExecutionProjectionContext, IReadOnlyList<string>>
{
    private readonly IProjectionWriteDispatcher<ScriptNativeDocumentReadModel> _nativeWriteDispatcher;
    private readonly IScriptDefinitionSnapshotPort _definitionSnapshotPort;
    private readonly IScriptBehaviorArtifactResolver _artifactResolver;
    private readonly IScriptReadModelMaterializationCompiler _materializationCompiler;
    private readonly IScriptNativeDocumentMaterializer _materializer;
    private readonly IProtobufMessageCodec _codec;

    public ScriptNativeDocumentProjector(
        IProjectionWriteDispatcher<ScriptNativeDocumentReadModel> nativeWriteDispatcher,
        IScriptDefinitionSnapshotPort definitionSnapshotPort,
        IScriptBehaviorArtifactResolver artifactResolver,
        IScriptReadModelMaterializationCompiler materializationCompiler,
        IScriptNativeDocumentMaterializer materializer,
        IProtobufMessageCodec codec)
    {
        _nativeWriteDispatcher = nativeWriteDispatcher ?? throw new ArgumentNullException(nameof(nativeWriteDispatcher));
        _definitionSnapshotPort = definitionSnapshotPort ?? throw new ArgumentNullException(nameof(definitionSnapshotPort));
        _artifactResolver = artifactResolver ?? throw new ArgumentNullException(nameof(artifactResolver));
        _materializationCompiler = materializationCompiler ?? throw new ArgumentNullException(nameof(materializationCompiler));
        _materializer = materializer ?? throw new ArgumentNullException(nameof(materializer));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
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
        var plan = _materializationCompiler.GetOrCompile(
            artifact,
            snapshot.ReadModelSchemaHash,
            snapshot.ReadModelSchemaVersion);
        if (!plan.SupportsDocument)
            return;

        var semanticReadModel = await ScriptCommittedStateProjectionSupport.BuildSemanticReadModelAsync(
            context.RootActorId,
            state,
            fact,
            artifact,
            _codec);
        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(
            envelope,
            DateTimeOffset.FromUnixTimeMilliseconds(fact.OccurredAtUnixTimeMs));
        var nativeDocument = _materializer.Materialize(
            context.RootActorId,
            state.ScriptId,
            state.DefinitionActorId,
            state.Revision,
            fact,
            string.IsNullOrWhiteSpace(stateEvent.EventId) ? envelope.Id ?? string.Empty : stateEvent.EventId,
            updatedAt,
            semanticReadModel,
            plan);
        await _nativeWriteDispatcher.UpsertAsync(nativeDocument, ct);
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
