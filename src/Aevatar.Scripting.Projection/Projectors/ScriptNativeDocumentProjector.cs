using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Materialization;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Projectors;

public sealed class ScriptNativeDocumentProjector
    : IProjectionMaterializer<ScriptExecutionMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<ScriptNativeDocumentReadModel> _nativeWriteDispatcher;
    private readonly IScriptNativeDocumentMaterializer _materializer;

    public ScriptNativeDocumentProjector(
        IProjectionWriteDispatcher<ScriptNativeDocumentReadModel> nativeWriteDispatcher,
        IScriptNativeDocumentMaterializer materializer)
    {
        _nativeWriteDispatcher = nativeWriteDispatcher ?? throw new ArgumentNullException(nameof(nativeWriteDispatcher));
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
        if (fact.NativeDocument == null)
            return;

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(
            envelope,
            DateTimeOffset.FromUnixTimeMilliseconds(fact.OccurredAtUnixTimeMs));
        var nativeDocument = _materializer.Materialize(
            context.RootActorId,
            fact.ScriptId ?? string.Empty,
            fact.DefinitionActorId ?? string.Empty,
            fact.Revision ?? string.Empty,
            fact,
            sourceEventId,
            updatedAt,
            fact.NativeDocument);
        await _nativeWriteDispatcher.UpsertAsync(nativeDocument, ct);
    }

}
