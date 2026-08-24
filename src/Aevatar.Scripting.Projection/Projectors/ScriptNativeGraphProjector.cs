using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Materialization;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Projectors;

public sealed class ScriptNativeGraphProjector
    : ICurrentStateProjectionMaterializer<ScriptExecutionMaterializationContext>
{
    // Refactor (issue1289): native graphs consume materializer-derived payloads instead of event-embedded payloads.
    // Refactor (iter76/cluster-076-scripting-domain-fact-derived-readmodel-payloads):
    //   Old pattern: ScriptDomainFactCommitted persisted derived readmodel/native_document/native_graph payloads inside the domain event
    //   New principle: domain event keeps only committed facts; projection materializer derives readmodel/native_document/(optional)native_graph from fact + state_root
    private readonly IProjectionGraphWriter<ScriptNativeGraphReadModel> _graphWriter;
    private readonly IScriptProjectionPayloadMaterializer _payloadMaterializer;
    private readonly IScriptNativeGraphMaterializer _materializer;
    private readonly ProjectionGraphProviderStatus? _graphProviderStatus;

    public ScriptNativeGraphProjector(
        IProjectionGraphWriter<ScriptNativeGraphReadModel> graphWriter,
        IScriptProjectionPayloadMaterializer payloadMaterializer,
        IScriptNativeGraphMaterializer materializer,
        ProjectionGraphProviderStatus? graphProviderStatus = null)
    {
        _graphWriter = graphWriter ?? throw new ArgumentNullException(nameof(graphWriter));
        _payloadMaterializer = payloadMaterializer ?? throw new ArgumentNullException(nameof(payloadMaterializer));
        _materializer = materializer ?? throw new ArgumentNullException(nameof(materializer));
        _graphProviderStatus = graphProviderStatus;
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

        if (_graphProviderStatus is { Enabled: false })
            return;

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
        if (payload.NativeGraph == null)
            return;

        var graphReadModel = _materializer.Materialize(
            context.RootActorId,
            fact.ScriptId ?? string.Empty,
            fact.DefinitionActorId ?? string.Empty,
            fact.Revision ?? string.Empty,
            fact,
            sourceEventId,
            updatedAt,
            payload.NativeGraph);
        await _graphWriter.UpsertAsync(graphReadModel, context.ProjectionKind, ct);
    }

}
