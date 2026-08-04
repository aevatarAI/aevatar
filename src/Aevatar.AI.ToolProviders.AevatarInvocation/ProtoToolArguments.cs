using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;

namespace Aevatar.AI.ToolProviders.AevatarInvocation;

internal sealed record ToolArgumentParseResult<T>(
    T? Value,
    InvocationToolError? Error)
    where T : class, IMessage<T>;

internal static class ProtoToolArguments
{
    private static readonly JsonParser IgnoreUnknownFieldsParser = new(
        JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    public static InvocationToolError? RejectForbiddenRootField(
        string argumentsJson,
        string field,
        string replacement)
    {
        var normalized = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;
        try
        {
            if (JsonNode.Parse(normalized) is not JsonObject obj || !obj.ContainsKey(field))
                return null;

            return new InvocationToolError
            {
                Code = "invalid_arguments",
                Message = $"{field} is not accepted. Use {replacement}.",
                Field = field,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static InvocationToolError? RejectForbiddenRootFields(
        string argumentsJson,
        IReadOnlyCollection<string> fields,
        string replacement)
    {
        var normalized = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;
        try
        {
            if (JsonNode.Parse(normalized) is not JsonObject obj)
                return null;

            foreach (var field in fields)
            {
                if (!obj.ContainsKey(field))
                    continue;

                return new InvocationToolError
                {
                    Code = "invalid_arguments",
                    Message = $"{field} is not accepted. Use {replacement}.",
                    Field = field,
                };
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ToolArgumentParseResult<T> Parse<T>(string argumentsJson)
        where T : class, IMessage<T>, new()
    {
        var normalized = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;
        try
        {
            var node = JsonNode.Parse(normalized) ?? new JsonObject();
            NormalizeToolEnums(node);
            normalized = node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            var value = IgnoreUnknownFieldsParser.Parse<T>(normalized);
            return new ToolArgumentParseResult<T>(value, null);
        }
        catch (JsonException ex)
        {
            return Invalid<T>($"Arguments must be a JSON object: {ex.Message}");
        }
        catch (InvalidProtocolBufferException ex)
        {
            return Invalid<T>(ex.Message);
        }
        catch (Exception ex)
        {
            return Invalid<T>(ex.Message);
        }
    }

    public static InvocationToolError? Require(
        string? value,
        string field,
        string message)
    {
        return string.IsNullOrWhiteSpace(value)
            ? new InvocationToolError
            {
                Code = "invalid_arguments",
                Message = message,
                Field = field,
            }
            : null;
    }

    public static InvocationToolError? RequirePayload(InvocationPayload? payload, string field)
    {
        if (payload == null)
        {
            return new InvocationToolError
            {
                Code = "invalid_arguments",
                Message = $"{field} is required.",
                Field = field,
            };
        }

        if (string.IsNullOrWhiteSpace(payload.Prompt) && payload.InputParts.Count == 0)
        {
            return new InvocationToolError
            {
                Code = "invalid_arguments",
                Message = $"{field}.prompt or {field}.input_parts is required.",
                Field = field,
            };
        }

        return null;
    }

    public static InvocationToolError? RequireMessage(IMessage? value, string field) =>
        value == null
            ? new InvocationToolError
            {
                Code = "invalid_arguments",
                Message = $"{field} is required.",
                Field = field,
            }
            : null;

    private static ToolArgumentParseResult<T> Invalid<T>(string message)
        where T : class, IMessage<T>, new() =>
        new(null, new InvocationToolError
        {
            Code = "invalid_arguments",
            Message = message,
        });

    private static void NormalizeToolEnums(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                NormalizeEnumProperty(obj, "wait", NormalizeWaitEnum);
                NormalizeEnumProperty(obj, "kind", NormalizeContentPartKind);
                foreach (var (_, child) in obj)
                    NormalizeToolEnums(child);
                break;
            case JsonArray array:
                foreach (var child in array)
                    NormalizeToolEnums(child);
                break;
        }
    }

    private static void NormalizeEnumProperty(
        JsonObject obj,
        string propertyName,
        Func<string, string> normalize)
    {
        if (obj[propertyName] is not JsonValue value)
            return;

        var raw = value.GetValue<string?>()?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return;

        obj[propertyName] = normalize(raw);
    }

    private static string NormalizeWaitEnum(string raw) =>
        raw.ToLowerInvariant() switch
        {
            "ack" => "INVOCATION_WAIT_MODE_ACK",
            "stream" => "INVOCATION_WAIT_MODE_STREAM",
            "complete" => "INVOCATION_WAIT_MODE_COMPLETE",
            "invocation_wait_mode_ack" => "INVOCATION_WAIT_MODE_ACK",
            "invocation_wait_mode_stream" => "INVOCATION_WAIT_MODE_STREAM",
            "invocation_wait_mode_complete" => "INVOCATION_WAIT_MODE_COMPLETE",
            _ => raw,
        };

    private static string NormalizeContentPartKind(string raw) =>
        raw.ToLowerInvariant() switch
        {
            "text" => "INVOCATION_CONTENT_PART_KIND_TEXT",
            "image" => "INVOCATION_CONTENT_PART_KIND_IMAGE",
            "audio" => "INVOCATION_CONTENT_PART_KIND_AUDIO",
            "video" => "INVOCATION_CONTENT_PART_KIND_VIDEO",
            "file" => "INVOCATION_CONTENT_PART_KIND_FILE",
            "invocation_content_part_kind_text" => "INVOCATION_CONTENT_PART_KIND_TEXT",
            "invocation_content_part_kind_image" => "INVOCATION_CONTENT_PART_KIND_IMAGE",
            "invocation_content_part_kind_audio" => "INVOCATION_CONTENT_PART_KIND_AUDIO",
            "invocation_content_part_kind_video" => "INVOCATION_CONTENT_PART_KIND_VIDEO",
            "invocation_content_part_kind_file" => "INVOCATION_CONTENT_PART_KIND_FILE",
            _ => raw,
        };
}
