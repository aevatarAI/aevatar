using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Core.Artifacts;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Materialization;
using Aevatar.Scripting.Projection.Materialization;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Serialization;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Projectors;

public sealed class ScriptNativeGraphProjector
    : IProjectionProjector<ScriptExecutionProjectionContext, IReadOnlyList<string>>
{
    private readonly IProjectionStoreDispatcher<ScriptNativeGraphReadModel, string> _graphStoreDispatcher;
    private readonly IScriptDefinitionSnapshotPort _definitionSnapshotPort;
    private readonly IScriptBehaviorArtifactResolver _artifactResolver;
    private readonly IScriptReadModelMaterializationCompiler _materializationCompiler;
    private readonly IScriptNativeGraphMaterializer _materializer;
    private readonly IProtobufMessageCodec _codec;

    public ScriptNativeGraphProjector(
        IProjectionStoreDispatcher<ScriptNativeGraphReadModel, string> graphStoreDispatcher,
        IScriptDefinitionSnapshotPort definitionSnapshotPort,
        IScriptBehaviorArtifactResolver artifactResolver,
        IScriptReadModelMaterializationCompiler materializationCompiler,
        IScriptNativeGraphMaterializer materializer,
        IProtobufMessageCodec codec)
    {
        _graphStoreDispatcher = graphStoreDispatcher ?? throw new ArgumentNullException(nameof(graphStoreDispatcher));
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
        var eventTypeUrl = fact.DomainEventPayload?.TypeUrl ?? string.Empty;
        var eventSemantics = artifact.Descriptor.RuntimeSemantics.GetRequiredMessageSemantics(eventTypeUrl, ScriptMessageKind.DomainEvent);
        if (eventSemantics.Kind != ScriptMessageKind.DomainEvent || !eventSemantics.Projectable)
            return;
        if (!string.IsNullOrWhiteSpace(eventSemantics.ReadModelScope) &&
            !string.Equals(eventSemantics.ReadModelScope, artifact.Descriptor.ReadModelDescriptor.FullName, StringComparison.Ordinal) &&
            !string.Equals(eventSemantics.ReadModelScope, fact.ReadModelTypeUrl, StringComparison.Ordinal))
        {
            return;
        }
        var plan = _materializationCompiler.GetOrCompile(
            artifact,
            snapshot.ReadModelSchemaHash,
            snapshot.ReadModelSchemaVersion);
        if (!plan.SupportsGraph)
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
        var graphReadModel = _materializer.Materialize(
            context.RootActorId,
            string.IsNullOrWhiteSpace(fact.ScriptId) ? snapshot.ScriptId : fact.ScriptId,
            string.IsNullOrWhiteSpace(fact.DefinitionActorId) ? semanticDocument.DefinitionActorId : fact.DefinitionActorId,
            string.IsNullOrWhiteSpace(fact.Revision) ? snapshot.Revision : fact.Revision,
            fact,
            semanticReadModel,
            plan);
        await _graphStoreDispatcher.UpsertAsync(graphReadModel, ct);
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
