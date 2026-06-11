using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Materialization;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Projectors;

public sealed class ScriptNativeDocumentProjector
    : ICurrentStateProjectionMaterializer<ScriptExecutionMaterializationContext>
{
    // Refactor (issue1289): native documents consume materializer-derived payloads instead of event-embedded payloads.
    // Refactor (iter76/cluster-076-scripting-domain-fact-derived-readmodel-payloads):
    //   Old pattern: ScriptDomainFactCommitted persisted derived readmodel/native_document/native_graph payloads inside the domain event
    //   New principle: domain event keeps only committed facts; projection materializer derives readmodel/native_document/(optional)native_graph from fact + state_root
    private readonly IProjectionWriteDispatcher<ScriptNativeDocumentReadModel> _nativeWriteDispatcher;
    private readonly IScriptProjectionPayloadMaterializer _payloadMaterializer;
    private readonly IScriptNativeDocumentMaterializer _materializer;

    public ScriptNativeDocumentProjector(
        IProjectionWriteDispatcher<ScriptNativeDocumentReadModel> nativeWriteDispatcher,
        IScriptProjectionPayloadMaterializer payloadMaterializer,
        IScriptNativeDocumentMaterializer materializer)
    {
        _nativeWriteDispatcher = nativeWriteDispatcher ?? throw new ArgumentNullException(nameof(nativeWriteDispatcher));
        _payloadMaterializer = payloadMaterializer ?? throw new ArgumentNullException(nameof(payloadMaterializer));
        _materializer = materializer ?? throw new ArgumentNullException(nameof(materializer));
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
        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(
            envelope,
            DateTimeOffset.FromUnixTimeMilliseconds(fact.OccurredAtUnixTimeMs));
        var payload = await _payloadMaterializer.MaterializeAsync(
            new ScriptProjectionMaterializationInput(
                fact,
                envelope,
                context.RootActorId,
                sourceEventId,
                updatedAt),
            ct);
        if (payload.NativeDocument == null)
            return;

        var nativeDocument = _materializer.Materialize(
            context.RootActorId,
            fact.ScriptId ?? string.Empty,
            fact.DefinitionActorId ?? string.Empty,
            fact.Revision ?? string.Empty,
            fact,
            sourceEventId,
            updatedAt,
            payload.NativeDocument);
        await _nativeWriteDispatcher.UpsertAsync(nativeDocument, ct);
    }

}
