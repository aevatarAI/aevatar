using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Abstractions.Responses;

public static class ResponsesJsonValues
{
    private static readonly JsonParser Parser = new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));
    private static readonly JsonFormatter Formatter = new(new JsonFormatter.Settings(formatDefaultValues: false));

    public static Value ParseBoundaryPayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return EmptyObject();

        try
        {
            return Parser.Parse<Value>(payload);
        }
        catch (InvalidJsonException)
        {
            return Value.ForString(payload.Trim());
        }
    }

    public static string ToBoundaryJson(Value? value)
    {
        if (value == null || value.KindCase == Value.KindOneofCase.None)
            return string.Empty;

        using var document = JsonDocument.Parse(Formatter.Format(value));
        return JsonSerializer.Serialize(document.RootElement);
    }

    public static Value ErrorObject(string code, string callId)
    {
        var value = EmptyObject();
        value.StructValue.Fields["error"] = Value.ForString(code);
        value.StructValue.Fields["call_id"] = Value.ForString(callId);
        return value;
    }

    private static Value EmptyObject() =>
        new()
        {
            StructValue = new Struct(),
        };
}
