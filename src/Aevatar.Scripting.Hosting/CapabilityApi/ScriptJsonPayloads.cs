using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Hosting.CapabilityApi;

internal static class ScriptJsonPayloads
{
    public static Any PackStruct(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Any.Pack(new Struct());

        var parsed = JsonParser.Default.Parse<Struct>(json);
        return Any.Pack(parsed);
    }

    public static string ToJson(Any? payload)
    {
        if (payload == null)
            return "{}";

        if (payload.Is(Struct.Descriptor))
            return JsonFormatter.Default.Format(payload.Unpack<Struct>());
        if (payload.Is(Value.Descriptor))
            return JsonFormatter.Default.Format(payload.Unpack<Value>());
        if (payload.Is(ListValue.Descriptor))
            return JsonFormatter.Default.Format(payload.Unpack<ListValue>());
        if (payload.Is(StringValue.Descriptor))
            return JsonFormatter.Default.Format(payload.Unpack<StringValue>());
        if (payload.Is(BoolValue.Descriptor))
            return JsonFormatter.Default.Format(payload.Unpack<BoolValue>());
        if (payload.Is(Int32Value.Descriptor))
            return JsonFormatter.Default.Format(payload.Unpack<Int32Value>());
        if (payload.Is(Int64Value.Descriptor))
            return JsonFormatter.Default.Format(payload.Unpack<Int64Value>());
        if (payload.Is(UInt32Value.Descriptor))
            return JsonFormatter.Default.Format(payload.Unpack<UInt32Value>());
        if (payload.Is(UInt64Value.Descriptor))
            return JsonFormatter.Default.Format(payload.Unpack<UInt64Value>());
        if (payload.Is(FloatValue.Descriptor))
            return JsonFormatter.Default.Format(payload.Unpack<FloatValue>());
        if (payload.Is(DoubleValue.Descriptor))
            return JsonFormatter.Default.Format(payload.Unpack<DoubleValue>());
        if (payload.Is(BytesValue.Descriptor))
            return JsonFormatter.Default.Format(payload.Unpack<BytesValue>());
        if (payload.Is(Empty.Descriptor))
            return JsonFormatter.Default.Format(payload.Unpack<Empty>());

        return JsonFormatter.Default.Format(payload);
    }
}
