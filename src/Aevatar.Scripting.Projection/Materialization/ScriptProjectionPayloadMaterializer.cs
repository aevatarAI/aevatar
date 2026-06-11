using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Core.Materialization;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Serialization;
using Google.Protobuf;

namespace Aevatar.Scripting.Projection.Materialization;

public sealed class ScriptProjectionPayloadMaterializer : IScriptProjectionPayloadMaterializer
{
    // Refactor (issue1289): derive projection payloads from committed fact plus state_root, with legacy fallback only.
    // Refactor (iter76/cluster-076-scripting-domain-fact-derived-readmodel-payloads):
    //   Old pattern: ScriptDomainFactCommitted persisted derived readmodel/native_document/native_graph payloads inside the domain event
    //   New principle: domain event keeps only committed facts; projection materializer derives readmodel/native_document/(optional)native_graph from fact + state_root
    private readonly IScriptBehaviorArtifactResolver _artifactResolver;
    private readonly IScriptReadModelMaterializationCompiler _materializationCompiler;
    private readonly IScriptNativeProjectionBuilder _nativeProjectionBuilder;
    private readonly IProtobufMessageCodec _codec;

    public ScriptProjectionPayloadMaterializer(
        IScriptBehaviorArtifactResolver artifactResolver,
        IScriptReadModelMaterializationCompiler materializationCompiler,
        IScriptNativeProjectionBuilder nativeProjectionBuilder,
        IProtobufMessageCodec codec)
    {
        _artifactResolver = artifactResolver ?? throw new ArgumentNullException(nameof(artifactResolver));
        _materializationCompiler = materializationCompiler ?? throw new ArgumentNullException(nameof(materializationCompiler));
        _nativeProjectionBuilder = nativeProjectionBuilder ?? throw new ArgumentNullException(nameof(nativeProjectionBuilder));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
    }

    public async ValueTask<ScriptProjectionPayload> MaterializeAsync(
        ScriptProjectionMaterializationInput input,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Fact);
        ArgumentNullException.ThrowIfNull(input.Envelope);

        try
        {
            var semanticReadModel = await DeriveReadModelAsync(input, ct);
            ct.ThrowIfCancellationRequested();
            var readModelPayload = _codec.Pack(semanticReadModel)?.Clone();
            var state = UnpackCommittedScriptState(input.Envelope);
            var artifact = ResolveArtifact(input.Fact, state);
            var plan = _materializationCompiler.Compile(
                artifact,
                state.ReadModelSchemaHash ?? string.Empty,
                state.ReadModelSchemaVersion ?? string.Empty);
            var actorId = ResolveActorId(input.Fact, input.RootActorId);
            var nativeDocument = _nativeProjectionBuilder.BuildDocument(
                semanticReadModel,
                plan);
            var nativeGraph = _nativeProjectionBuilder.BuildGraph(
                actorId,
                ResolveScriptId(input.Fact, state),
                ResolveDefinitionActorId(input.Fact, state),
                ResolveRevision(input.Fact, state),
                semanticReadModel,
                plan);

            return new ScriptProjectionPayload(
                readModelPayload,
                nativeDocument,
                nativeGraph,
                UsedLegacyReadModelPayload: false,
                UsedLegacyNativeDocument: false,
                UsedLegacyNativeGraph: false);
        }
        catch (Exception) when (HasLegacyFallback(input.Fact))
        {
            return new ScriptProjectionPayload(
                input.Fact.TryGetLegacyReadModelPayload()?.Clone(),
                input.Fact.TryGetLegacyNativeDocument()?.Clone(),
                input.Fact.TryGetLegacyNativeGraph()?.Clone(),
                UsedLegacyReadModelPayload: input.Fact.TryGetLegacyReadModelPayload() != null,
                UsedLegacyNativeDocument: input.Fact.TryGetLegacyNativeDocument() != null,
                UsedLegacyNativeGraph: input.Fact.TryGetLegacyNativeGraph() != null);
        }
    }

    private async ValueTask<IMessage?> DeriveReadModelAsync(
        ScriptProjectionMaterializationInput input,
        CancellationToken ct)
    {
        var state = UnpackCommittedScriptState(input.Envelope);
        var artifact = ResolveArtifact(input.Fact, state);
        var currentState = _codec.Unpack(state.StateRoot, artifact.Descriptor.StateClrType);
        var behavior = artifact.CreateBehavior();
        try
        {
            ct.ThrowIfCancellationRequested();
            return behavior.BuildReadModel(
                currentState,
                CreateFactContext(input.Fact, input.RootActorId));
        }
        finally
        {
            if (behavior is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else if (behavior is IDisposable disposable)
                disposable.Dispose();
        }
    }

    private ScriptBehaviorArtifact ResolveArtifact(
        ScriptDomainFactCommitted fact,
        ScriptBehaviorState state)
    {
        var package = state.ScriptPackage?.Clone() ?? new ScriptPackageSpec();
        if (package.CsharpSources.Count == 0)
            throw new InvalidOperationException("Committed scripting state_root does not contain a script package.");

        return _artifactResolver.Resolve(new ScriptBehaviorArtifactRequest(
            ResolveScriptId(fact, state),
            ResolveRevision(fact, state),
            package,
            state.SourceHash ?? string.Empty));
    }

    private static ScriptBehaviorState UnpackCommittedScriptState(EventEnvelope envelope)
    {
        if (!CommittedStateEventEnvelope.TryUnpack(envelope, out var published) ||
            published?.StateRoot?.Is(ScriptBehaviorState.Descriptor) != true)
        {
            throw new InvalidOperationException("Scripting projection requires committed ScriptBehaviorState state_root.");
        }

        return published.StateRoot.Unpack<ScriptBehaviorState>();
    }

    private static ScriptFactContext CreateFactContext(
        ScriptDomainFactCommitted fact,
        string rootActorId)
    {
        return new ScriptFactContext(
            ResolveActorId(fact, rootActorId),
            fact.DefinitionActorId ?? string.Empty,
            fact.ScriptId ?? string.Empty,
            fact.Revision ?? string.Empty,
            fact.RunId ?? string.Empty,
            fact.CommandId ?? string.Empty,
            fact.CorrelationId ?? string.Empty,
            fact.EventSequence,
            fact.StateVersion,
            fact.EventType ?? string.Empty,
            fact.OccurredAtUnixTimeMs);
    }

    private static bool HasLegacyFallback(ScriptDomainFactCommitted fact) =>
        fact.TryGetLegacyReadModelPayload() != null ||
        fact.TryGetLegacyNativeDocument() != null ||
        fact.TryGetLegacyNativeGraph() != null;

    private static string ResolveActorId(ScriptDomainFactCommitted fact, string rootActorId) =>
        string.IsNullOrWhiteSpace(fact.ActorId) ? rootActorId : fact.ActorId;

    private static string ResolveDefinitionActorId(ScriptDomainFactCommitted fact, ScriptBehaviorState state) =>
        string.IsNullOrWhiteSpace(fact.DefinitionActorId) ? state.DefinitionActorId ?? string.Empty : fact.DefinitionActorId;

    private static string ResolveScriptId(ScriptDomainFactCommitted fact, ScriptBehaviorState state) =>
        string.IsNullOrWhiteSpace(fact.ScriptId) ? state.ScriptId ?? string.Empty : fact.ScriptId;

    private static string ResolveRevision(ScriptDomainFactCommitted fact, ScriptBehaviorState state) =>
        string.IsNullOrWhiteSpace(fact.Revision) ? state.Revision ?? string.Empty : fact.Revision;
}
