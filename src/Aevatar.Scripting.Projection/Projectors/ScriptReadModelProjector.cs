using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Materialization;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Projection.Projectors;

public sealed class ScriptReadModelProjector
    : ICurrentStateProjectionMaterializer<ScriptExecutionMaterializationContext>
{
    // Refactor (issue1289): readmodel documents consume materializer-derived payloads instead of event-embedded payloads.
    // Refactor (iter76/cluster-076-scripting-domain-fact-derived-readmodel-payloads):
    //   Old pattern: ScriptDomainFactCommitted persisted derived readmodel/native_document/native_graph payloads inside the domain event
    //   New principle: domain event keeps only committed facts; projection materializer derives readmodel/native_document/(optional)native_graph from fact + state_root
    private readonly IProjectionWriteDispatcher<ScriptReadModelDocument> _writeDispatcher;
    private readonly IScriptProjectionPayloadMaterializer _payloadMaterializer;
    private readonly IProjectionClock _clock;

    public ScriptReadModelProjector(
        IProjectionWriteDispatcher<ScriptReadModelDocument> writeDispatcher,
        IScriptProjectionPayloadMaterializer payloadMaterializer,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _payloadMaterializer = payloadMaterializer ?? throw new ArgumentNullException(nameof(payloadMaterializer));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        ScriptExecutionMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        if (!CommittedStateEventEnvelope.TryGetObservedPayload(
                envelope,
                out var observedPayload,
                out var sourceEventId,
                out _) ||
            observedPayload?.Is(ScriptDomainFactCommitted.Descriptor) != true)
        {
            return;
        }

        var fact = observedPayload.Unpack<ScriptDomainFactCommitted>();
        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        var payload = await _payloadMaterializer.MaterializeAsync(
            new ScriptProjectionMaterializationInput(
                fact,
                envelope,
                context.RootActorId,
                sourceEventId,
                updatedAt),
            ct);
        if (payload.ReadModelPayload == null)
            return;

        var actorId = string.IsNullOrWhiteSpace(fact.ActorId) ? context.RootActorId : fact.ActorId;
        var document = new ScriptReadModelDocument
        {
            Id = actorId,
            ScriptId = fact.ScriptId ?? string.Empty,
            DefinitionActorId = fact.DefinitionActorId ?? string.Empty,
            Revision = fact.Revision ?? string.Empty,
            ReadModelTypeUrl = fact.ReadModelTypeUrl ?? string.Empty,
            ReadModelPayload = payload.ReadModelPayload.Clone(),
            StateVersion = fact.StateVersion,
            LastEventId = sourceEventId,
            UpdatedAt = updatedAt,
            ScopeId = fact.ScopeId ?? string.Empty,
        };

        await _writeDispatcher.UpsertAsync(document, ct);
    }

}
