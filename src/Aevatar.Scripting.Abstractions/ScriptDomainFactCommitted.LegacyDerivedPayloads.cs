using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Abstractions;

public sealed partial class ScriptDomainFactCommitted
{
    // Refactor (issue1289): keep removed derived payload fields readable only as legacy unknown-field data.
    // Refactor (iter76/cluster-076-scripting-domain-fact-derived-readmodel-payloads):
    //   Old pattern: ScriptDomainFactCommitted persisted derived readmodel/native_document/native_graph payloads inside the domain event
    //   New principle: domain event keeps only committed facts; projection materializer derives readmodel/native_document/(optional)native_graph from fact + state_root
    private const int LegacyReadModelPayloadFieldNumber = 15;
    private const int LegacyNativeDocumentFieldNumber = 16;
    private const int LegacyNativeGraphFieldNumber = 17;

    public Any? TryGetLegacyReadModelPayload() =>
        TryParseLegacyLengthDelimited(
            LegacyReadModelPayloadFieldNumber,
            Any.Parser,
            out var payload)
            ? payload
            : null;

    public ScriptNativeDocumentProjection? TryGetLegacyNativeDocument() =>
        TryParseLegacyLengthDelimited(
            LegacyNativeDocumentFieldNumber,
            ScriptNativeDocumentProjection.Parser,
            out var nativeDocument)
            ? nativeDocument
            : null;

    public ScriptNativeGraphProjection? TryGetLegacyNativeGraph() =>
        TryParseLegacyLengthDelimited(
            LegacyNativeGraphFieldNumber,
            ScriptNativeGraphProjection.Parser,
            out var nativeGraph)
            ? nativeGraph
            : null;

    private bool TryParseLegacyLengthDelimited<TMessage>(
        int fieldNumber,
        MessageParser<TMessage> parser,
        out TMessage? message)
        where TMessage : class, IMessage<TMessage>
    {
        ArgumentNullException.ThrowIfNull(parser);

        message = null;
        var input = new CodedInputStream(((IMessage)this).ToByteArray());
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (tag == 0)
                break;

            if (WireFormat.GetTagFieldNumber(tag) == fieldNumber &&
                WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
            {
                message = parser.ParseFrom(input.ReadBytes());
                return message != null;
            }

            input.SkipLastField();
        }

        return false;
    }
}
