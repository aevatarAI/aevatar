using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Core.Artifacts;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Serialization;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Projection.Projectors;

public sealed class ScriptReadModelProjector
    : IProjectionProjector<ScriptExecutionProjectionContext, IReadOnlyList<string>>
{
    private readonly IProjectionStoreDispatcher<ScriptReadModelDocument, string> _storeDispatcher;
    private readonly IProjectionClock _clock;
    private readonly IScriptDefinitionSnapshotPort _definitionSnapshotPort;
    private readonly IScriptBehaviorArtifactResolver _artifactResolver;
    private readonly IProtobufMessageCodec _codec;

    public ScriptReadModelProjector(
        IProjectionStoreDispatcher<ScriptReadModelDocument, string> storeDispatcher,
        IProjectionClock clock,
        IScriptDefinitionSnapshotPort definitionSnapshotPort,
        IScriptBehaviorArtifactResolver artifactResolver,
        IProtobufMessageCodec codec)
    {
        _storeDispatcher = storeDispatcher ?? throw new ArgumentNullException(nameof(storeDispatcher));
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
        return new ValueTask(_storeDispatcher.UpsertAsync(new ScriptReadModelDocument
        {
            Id = context.RootActorId,
            UpdatedAt = now,
        }, ct));
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
        var artifact = _artifactResolver.Resolve(new ScriptBehaviorArtifactRequest(
            snapshot.ScriptId,
            snapshot.Revision,
            snapshot.SourceText,
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

            await _storeDispatcher.MutateAsync(context.RootActorId, document =>
            {
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
            }, ct);
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
