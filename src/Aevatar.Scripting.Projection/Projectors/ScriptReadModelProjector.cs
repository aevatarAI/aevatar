using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Serialization;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Projection.Projectors;

public sealed class ScriptReadModelProjector
    : IProjectionProjector<ScriptExecutionProjectionContext, IReadOnlyList<string>>
{
    private readonly IProjectionDocumentReader<ScriptReadModelDocument, string> _documentReader;
    private readonly IProjectionWriteDispatcher<ScriptReadModelDocument, string> _writeDispatcher;
    private readonly IProjectionClock _clock;
    private readonly IScriptDefinitionSnapshotPort _definitionSnapshotPort;
    private readonly IScriptBehaviorArtifactResolver _artifactResolver;
    private readonly IProtobufMessageCodec _codec;

    public ScriptReadModelProjector(
        IProjectionDocumentReader<ScriptReadModelDocument, string> documentReader,
        IProjectionWriteDispatcher<ScriptReadModelDocument, string> writeDispatcher,
        IProjectionClock clock,
        IScriptDefinitionSnapshotPort definitionSnapshotPort,
        IScriptBehaviorArtifactResolver artifactResolver,
        IProtobufMessageCodec codec)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _definitionSnapshotPort = definitionSnapshotPort ?? throw new ArgumentNullException(nameof(definitionSnapshotPort));
        _artifactResolver = artifactResolver ?? throw new ArgumentNullException(nameof(artifactResolver));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
    }

    public ValueTask InitializeAsync(
        ScriptExecutionProjectionContext context,
        CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var document = new ScriptReadModelDocument
        {
            Id = context.RootActorId,
            UpdatedAt = now,
        };
        context.CurrentSemanticReadModelDocument = document.DeepClone();
        return new ValueTask(_writeDispatcher.UpsertAsync(document, ct));
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
        var scriptPackage = ScriptPackageModel.ResolveDeclaredPackage(
            snapshot.ScriptPackage,
            snapshot.SourceText);
        var artifact = _artifactResolver.Resolve(new ScriptBehaviorArtifactRequest(
            snapshot.ScriptId,
            snapshot.Revision,
            scriptPackage,
            snapshot.SourceHash));
        var behavior = artifact.CreateBehavior();
        var now = ProjectionEnvelopeTimestampResolver.Resolve(normalized, _clock.UtcNow);

        try
        {
            var eventTypeUrl = fact.DomainEventPayload?.TypeUrl ?? string.Empty;
            if (!artifact.Descriptor.DomainEvents.TryGetValue(eventTypeUrl, out var domainEventRegistration))
            {
                throw new InvalidOperationException(
                    $"Script read model projector rejected undeclared domain event type `{eventTypeUrl}`.");
            }
            var eventSemantics = artifact.Descriptor.RuntimeSemantics.GetRequiredMessageSemantics(eventTypeUrl, ScriptMessageKind.DomainEvent);
            if (eventSemantics.Kind != ScriptMessageKind.DomainEvent)
            {
                throw new InvalidOperationException(
                    $"Script read model projector rejected `{eventTypeUrl}` because runtime kind is `{eventSemantics.Kind}`.");
            }

            if (!eventSemantics.Projectable)
                return;

            var document = (await _documentReader.GetAsync(context.RootActorId, ct))?.DeepClone()
                           ?? new ScriptReadModelDocument
                           {
                               Id = context.RootActorId,
                           };
            document.Id = context.RootActorId;
            document.ScriptId = string.IsNullOrWhiteSpace(fact.ScriptId) ? snapshot.ScriptId : fact.ScriptId;
            document.DefinitionActorId = string.IsNullOrWhiteSpace(fact.DefinitionActorId)
                ? document.DefinitionActorId
                : fact.DefinitionActorId;
            document.Revision = string.IsNullOrWhiteSpace(fact.Revision) ? snapshot.Revision : fact.Revision;
            document.ReadModelTypeUrl = string.IsNullOrWhiteSpace(fact.ReadModelTypeUrl)
                ? artifact.Contract.ReadModelTypeUrl ?? string.Empty
                : fact.ReadModelTypeUrl;
            var currentReadModel = _codec.Unpack(document.ReadModelPayload, artifact.Descriptor.ReadModelClrType);
            var domainEvent = _codec.Unpack(fact.DomainEventPayload, domainEventRegistration.MessageClrType)
                ?? throw new InvalidOperationException($"Failed to unpack domain event payload `{eventTypeUrl}`.");
            var reducedReadModel = behavior.ReduceReadModel(
                currentReadModel,
                domainEvent,
                new ScriptFactContext(
                    fact.ActorId ?? context.RootActorId,
                    fact.DefinitionActorId ?? document.DefinitionActorId,
                    string.IsNullOrWhiteSpace(fact.ScriptId) ? snapshot.ScriptId : fact.ScriptId,
                    string.IsNullOrWhiteSpace(fact.Revision) ? snapshot.Revision : fact.Revision,
                    fact.RunId ?? string.Empty,
                    fact.CommandId ?? string.Empty,
                    fact.CorrelationId ?? string.Empty,
                    fact.EventSequence,
                    fact.StateVersion,
                    fact.EventType ?? eventTypeUrl,
                    fact.OccurredAtUnixTimeMs));
            document.ReadModelPayload = _codec.Pack(reducedReadModel)?.Clone()
                ?? document.ReadModelPayload
                ?? Any.Pack(new Empty());
            document.StateVersion = fact.StateVersion;
            document.LastEventId = normalized.Id ?? string.Empty;
            document.UpdatedAt = now;
            await _writeDispatcher.UpsertAsync(document, ct);
            context.CurrentSemanticReadModelDocument = document.DeepClone();
        }
        finally
        {
            if (behavior is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else if (behavior is IDisposable disposable)
                disposable.Dispose();
        }
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
