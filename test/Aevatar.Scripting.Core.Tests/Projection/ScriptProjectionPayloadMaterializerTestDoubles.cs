using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Materialization;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Projection;

internal sealed class StubScriptProjectionPayloadMaterializer : IScriptProjectionPayloadMaterializer
{
    private readonly Func<ScriptProjectionMaterializationInput, ScriptProjectionPayload> _factory;

    public StubScriptProjectionPayloadMaterializer(Func<ScriptProjectionMaterializationInput, ScriptProjectionPayload> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public ValueTask<ScriptProjectionPayload> MaterializeAsync(
        ScriptProjectionMaterializationInput input,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_factory(input));
    }

    public static StubScriptProjectionPayloadMaterializer WithReadModel(IMessage readModel) =>
        new(_ => new ScriptProjectionPayload(
            Any.Pack(readModel),
            NativeDocument: null,
            NativeGraph: null,
            UsedLegacyReadModelPayload: false,
            UsedLegacyNativeDocument: false,
            UsedLegacyNativeGraph: false));

    public static StubScriptProjectionPayloadMaterializer WithNativeDocument(ScriptNativeDocumentProjection? nativeDocument) =>
        new(_ => new ScriptProjectionPayload(
            ReadModelPayload: null,
            nativeDocument,
            NativeGraph: null,
            UsedLegacyReadModelPayload: false,
            UsedLegacyNativeDocument: false,
            UsedLegacyNativeGraph: false));

    public static StubScriptProjectionPayloadMaterializer WithNativeGraph(ScriptNativeGraphProjection? nativeGraph) =>
        new(_ => new ScriptProjectionPayload(
            ReadModelPayload: null,
            NativeDocument: null,
            nativeGraph,
            UsedLegacyReadModelPayload: false,
            UsedLegacyNativeDocument: false,
            UsedLegacyNativeGraph: false));
}

internal static class ScriptLegacyFactPayloadTestHelper
{
    public static ScriptDomainFactCommitted WithLegacyPayloads(
        ScriptDomainFactCommitted fact,
        Any? readModelPayload = null,
        ScriptNativeDocumentProjection? nativeDocument = null,
        ScriptNativeGraphProjection? nativeGraph = null)
    {
        ArgumentNullException.ThrowIfNull(fact);

        using var stream = new MemoryStream();
        var bytes = ((IMessage)fact).ToByteArray();
        stream.Write(bytes, 0, bytes.Length);
        using (var output = new CodedOutputStream(stream, leaveOpen: true))
        {
            WriteMessage(output, 15, readModelPayload);
            WriteMessage(output, 16, nativeDocument);
            WriteMessage(output, 17, nativeGraph);
        }

        return ScriptDomainFactCommitted.Parser.ParseFrom(stream.ToArray());
    }

    private static void WriteMessage(CodedOutputStream output, int fieldNumber, IMessage? message)
    {
        if (message == null)
            return;

        output.WriteTag(fieldNumber, WireFormat.WireType.LengthDelimited);
        output.WriteMessage(message);
    }
}
