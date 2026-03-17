using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Materialization;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Projectors;

public sealed class ScriptNativeGraphProjector
    : ICurrentStateProjectionMaterializer<ScriptExecutionMaterializationContext>
{
    private readonly IProjectionGraphWriter<ScriptNativeGraphReadModel> _graphWriter;
    private readonly IScriptNativeGraphMaterializer _materializer;

    public ScriptNativeGraphProjector(
        IProjectionGraphWriter<ScriptNativeGraphReadModel> graphWriter,
        IScriptNativeGraphMaterializer materializer)
    {
        _graphWriter = graphWriter ?? throw new ArgumentNullException(nameof(graphWriter));
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
        if (fact.NativeGraph == null)
            return;

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(
            envelope,
            DateTimeOffset.FromUnixTimeMilliseconds(fact.OccurredAtUnixTimeMs));
        var graphReadModel = _materializer.Materialize(
            context.RootActorId,
            fact.ScriptId ?? string.Empty,
            fact.DefinitionActorId ?? string.Empty,
            fact.Revision ?? string.Empty,
            fact,
            sourceEventId,
            updatedAt,
            fact.NativeGraph);
        await _graphWriter.UpsertAsync(graphReadModel, ct);
    }

}
