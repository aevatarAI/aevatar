using System.Text.Json;
using Google.Protobuf.Collections;

namespace Aevatar.AI.ToolProviders.AevatarInvocation;

internal static class AevatarInvocationJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Error(InvocationToolError error) =>
        JsonSerializer.Serialize(new
        {
            error = new
            {
                code = error.Code,
                message = error.Message,
                field = string.IsNullOrWhiteSpace(error.Field) ? null : error.Field,
            },
        }, Options);

    public static string Serialize(InvocationToolResult result) =>
        result.Error == null
            ? JsonSerializer.Serialize(new
            {
                run_id = EmptyToNull(result.RunId),
                status = EmptyToNull(result.Status),
                stream_topic = EmptyToNull(result.StreamTopic),
                result = TryParseJson(result.ResultJson),
                actor_id = EmptyToNull(result.ActorId),
                command_id = EmptyToNull(result.CommandId),
                correlation_id = EmptyToNull(result.CorrelationId),
                service_id = EmptyToNull(result.ServiceId),
                endpoint_id = EmptyToNull(result.EndpointId),
                wait = ToWaitText(result.Wait),
            }, Options)
            : Error(result.Error);

    public static string Serialize(ObserveRunResult result) =>
        result.Error == null
            ? JsonSerializer.Serialize(new
            {
                run_id = EmptyToNull(result.RunId),
                status = EmptyToNull(result.Status),
                recent_events = result.RecentEvents.Select(static evt => new
                {
                    event_type = EmptyToNull(evt.EventType),
                    message = EmptyToNull(evt.Message),
                    step_id = EmptyToNull(evt.StepId),
                    timestamp_utc = EmptyToNull(evt.TimestampUtc),
                }).ToArray(),
                partial_output = EmptyToNull(result.PartialOutput),
                actor_id = EmptyToNull(result.ActorId),
                command_id = EmptyToNull(result.CommandId),
                state_version = result.StateVersion,
            }, Options)
            : Error(result.Error);

    public static Dictionary<string, string> ToDictionary(MapField<string, string> source)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in source)
        {
            var key = pair.Key?.Trim() ?? string.Empty;
            if (key.Length == 0)
                continue;
            result[key] = pair.Value ?? string.Empty;
        }

        return result;
    }

    public static string ToJson<T>(T value) =>
        JsonSerializer.Serialize(value, Options);

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string ToWaitText(InvocationWaitMode wait) =>
        wait switch
        {
            InvocationWaitMode.Ack => "ack",
            InvocationWaitMode.Complete => "complete",
            _ => "stream",
        };

    private static object? TryParseJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(value);
        }
        catch
        {
            return value;
        }
    }
}
