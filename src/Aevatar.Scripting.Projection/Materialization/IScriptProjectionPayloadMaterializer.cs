using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;

namespace Aevatar.Scripting.Projection.Materialization;

public interface IScriptProjectionPayloadMaterializer
{
    ValueTask<ScriptProjectionPayload> MaterializeAsync(
        ScriptProjectionMaterializationInput input,
        CancellationToken ct = default);
}

public sealed record ScriptProjectionMaterializationInput(
    ScriptDomainFactCommitted Fact,
    EventEnvelope Envelope,
    string RootActorId,
    string SourceEventId,
    DateTimeOffset UpdatedAt);

public sealed record ScriptProjectionPayload(
    Google.Protobuf.WellKnownTypes.Any? ReadModelPayload,
    ScriptNativeDocumentProjection? NativeDocument,
    ScriptNativeGraphProjection? NativeGraph,
    bool UsedLegacyReadModelPayload,
    bool UsedLegacyNativeDocument,
    bool UsedLegacyNativeGraph);
