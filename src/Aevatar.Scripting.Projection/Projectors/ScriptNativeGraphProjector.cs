using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Materialization;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Projectors;

public sealed class ScriptNativeGraphProjector
    : IProjectionMaterializer<ScriptExecutionMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<ScriptNativeGraphReadModel> _graphWriteDispatcher;
    private readonly IScriptNativeGraphMaterializer _materializer;

    public ScriptNativeGraphProjector(
        IProjectionWriteDispatcher<ScriptNativeGraphReadModel> graphWriteDispatcher,
        IScriptNativeGraphMaterializer materializer)
    {
        _graphWriteDispatcher = graphWriteDispatcher ?? throw new ArgumentNullException(nameof(graphWriteDispatcher));
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
        await _graphWriteDispatcher.UpsertAsync(graphReadModel, ct);
    }

}
