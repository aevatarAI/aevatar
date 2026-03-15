using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Materialization;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Serialization;
using Aevatar.Scripting.Projection.Materialization;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Projectors;

public sealed class ScriptNativeGraphProjector
    : IProjectionProjector<ScriptExecutionProjectionContext, IReadOnlyList<string>>
{
    private readonly IProjectionWriteDispatcher<ScriptNativeGraphReadModel> _graphWriteDispatcher;
    private readonly IScriptBehaviorArtifactResolver _artifactResolver;
    private readonly IScriptReadModelMaterializationCompiler _materializationCompiler;
    private readonly IScriptNativeGraphMaterializer _materializer;
    private readonly IProtobufMessageCodec _codec;

    public ScriptNativeGraphProjector(
        IProjectionWriteDispatcher<ScriptNativeGraphReadModel> graphWriteDispatcher,
        IScriptBehaviorArtifactResolver artifactResolver,
        IScriptReadModelMaterializationCompiler materializationCompiler,
        IScriptNativeGraphMaterializer materializer,
        IProtobufMessageCodec codec)
    {
        _graphWriteDispatcher = graphWriteDispatcher ?? throw new ArgumentNullException(nameof(graphWriteDispatcher));
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
        var scriptPackage = ScriptPackageModel.ResolveDeclaredPackage(
            state.ScriptPackage,
            state.SourceText);
        var artifact = _artifactResolver.Resolve(new ScriptBehaviorArtifactRequest(
            string.IsNullOrWhiteSpace(state.ScriptId) ? fact.ScriptId ?? string.Empty : state.ScriptId,
            string.IsNullOrWhiteSpace(state.Revision) ? fact.Revision ?? string.Empty : state.Revision,
            scriptPackage,
            state.SourceHash));
        var plan = _materializationCompiler.GetOrCompile(
            artifact,
            state.ReadModelSchemaHash,
            state.ReadModelSchemaVersion);
        if (!plan.SupportsGraph)
            return;

        var semanticReadModel = _codec.Unpack(fact.ReadModelPayload, artifact.Descriptor.ReadModelClrType);
        if (semanticReadModel == null)
            return;
        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(
            envelope,
            DateTimeOffset.FromUnixTimeMilliseconds(fact.OccurredAtUnixTimeMs));
        var graphReadModel = _materializer.Materialize(
            context.RootActorId,
            state.ScriptId,
            state.DefinitionActorId,
            state.Revision,
            fact,
            string.IsNullOrWhiteSpace(stateEvent.EventId) ? envelope.Id ?? string.Empty : stateEvent.EventId,
            updatedAt,
            semanticReadModel,
            plan);
        await _graphWriteDispatcher.UpsertAsync(graphReadModel, ct);
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
