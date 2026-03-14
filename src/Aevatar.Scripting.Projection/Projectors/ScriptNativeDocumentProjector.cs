using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Artifacts;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Materialization;
using Aevatar.Scripting.Projection.Materialization;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Serialization;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Projectors;

public sealed class ScriptNativeDocumentProjector
    : IProjectionProjector<ScriptExecutionProjectionContext, IReadOnlyList<string>>
{
    private readonly IProjectionStoreDispatcher<ScriptNativeDocumentReadModel, string> _nativeStoreDispatcher;
    private readonly IScriptDefinitionSnapshotPort _definitionSnapshotPort;
    private readonly IScriptBehaviorArtifactResolver _artifactResolver;
    private readonly IScriptReadModelMaterializationCompiler _materializationCompiler;
    private readonly IScriptNativeDocumentMaterializer _materializer;
    private readonly IProtobufMessageCodec _codec;

    public ScriptNativeDocumentProjector(
        IProjectionStoreDispatcher<ScriptNativeDocumentReadModel, string> nativeStoreDispatcher,
        IScriptDefinitionSnapshotPort definitionSnapshotPort,
        IScriptBehaviorArtifactResolver artifactResolver,
        IScriptReadModelMaterializationCompiler materializationCompiler,
        IScriptNativeDocumentMaterializer materializer,
        IProtobufMessageCodec codec)
    {
        _nativeStoreDispatcher = nativeStoreDispatcher ?? throw new ArgumentNullException(nameof(nativeStoreDispatcher));
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
        var normalized = ProjectionEnvelopeNormalizer.Normalize(envelope);
        if (normalized?.Payload?.Is(ScriptDomainFactCommitted.Descriptor) != true)
            return;

        var fact = normalized.Payload.Unpack<ScriptDomainFactCommitted>();
        var snapshot = await _definitionSnapshotPort.GetRequiredAsync(
            fact.DefinitionActorId,
            fact.Revision,
            ct);
        var scriptPackage = snapshot.ScriptPackage?.Clone() ?? ScriptPackageModel.CreateSingleSourcePackage(snapshot.SourceText);
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

        var semanticDocument = context.CurrentSemanticReadModelDocument
            ?? throw new InvalidOperationException(
                $"Semantic script read model document was not present in projection context for actor `{context.RootActorId}`.");
        if (semanticDocument.StateVersion != fact.StateVersion)
        {
            throw new InvalidOperationException(
                $"Semantic script read model version mismatch for actor `{context.RootActorId}`. " +
                $"Expected state_version={fact.StateVersion}, actual={semanticDocument.StateVersion}.");
        }

        var semanticReadModel = _codec.Unpack(semanticDocument.ReadModelPayload, artifact.Descriptor.ReadModelClrType);
        var nativeDocument = _materializer.Materialize(
            context.RootActorId,
            string.IsNullOrWhiteSpace(fact.ScriptId) ? snapshot.ScriptId : fact.ScriptId,
            string.IsNullOrWhiteSpace(fact.DefinitionActorId) ? semanticDocument.DefinitionActorId : fact.DefinitionActorId,
            string.IsNullOrWhiteSpace(fact.Revision) ? snapshot.Revision : fact.Revision,
            fact,
            semanticReadModel,
            plan);
        await _nativeStoreDispatcher.UpsertAsync(nativeDocument, ct);
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
