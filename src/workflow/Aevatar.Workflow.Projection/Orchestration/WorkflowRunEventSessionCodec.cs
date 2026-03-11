using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection.Transport;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Projection.Orchestration;

/// <summary>
/// Session event codec for workflow live run output events.
/// </summary>
public sealed class WorkflowRunEventSessionCodec : IProjectionSessionEventCodec<WorkflowRunEvent>
    , ILegacyProjectionSessionEventCodec<WorkflowRunEvent>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Channel => "workflow-run";

    public string GetEventType(WorkflowRunEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return evt.Type;
    }

    public ByteString Serialize(WorkflowRunEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return Any.Pack(WorkflowRunSessionEventEnvelopeMapper.ToEnvelope(evt)).ToByteString();
    }

    public string? SerializeLegacy(WorkflowRunEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return null;
    }

    public WorkflowRunEvent? Deserialize(string eventType, ByteString payload)
    {
        if (string.IsNullOrWhiteSpace(eventType) || payload == null || payload.IsEmpty)
        {
            return null;
        }

        if (TryDeserializeEnvelope(eventType, payload, out var evt))
        {
            return evt;
        }

        if (TryDeserializeLegacyPayload(eventType, payload, out evt))
        {
            return evt;
        }

        return null;
    }

    public WorkflowRunEvent? DeserializeLegacy(string eventType, string payload)
    {
        if (string.IsNullOrWhiteSpace(eventType) || string.IsNullOrWhiteSpace(payload))
            return null;

        return TryDeserializeLegacyJson(eventType, payload, out var evt)
            ? evt
            : null;
    }

    private static bool TryDeserializeEnvelope(string eventType, ByteString payload, out WorkflowRunEvent? evt)
    {
        evt = null;
        try
        {
            var envelope = Any.Parser.ParseFrom(payload);
            if (!envelope.Is(WorkflowRunSessionEventEnvelope.Descriptor))
                return false;

            var decoded = WorkflowRunSessionEventEnvelopeMapper.FromEnvelope(
                envelope.Unpack<WorkflowRunSessionEventEnvelope>());
            if (!string.Equals(decoded?.Type, eventType, StringComparison.Ordinal))
                return false;

            evt = decoded;
            return true;
        }
        catch (InvalidProtocolBufferException)
        {
            return false;
        }
    }

    private static bool TryDeserializeLegacyPayload(string eventType, ByteString payload, out WorkflowRunEvent? evt)
    {
        evt = null;
        if (TryReadLegacyJsonFromStringValue(payload, out var json) ||
            TryReadLegacyJsonFromRawPayload(payload, out json))
        {
            return TryDeserializeLegacyJson(eventType, json, out evt);
        }

        return false;
    }

    private static bool TryReadLegacyJsonFromStringValue(ByteString payload, out string json)
    {
        json = string.Empty;
        try
        {
            var envelope = Any.Parser.ParseFrom(payload);
            if (!envelope.Is(StringValue.Descriptor))
                return false;

            json = envelope.Unpack<StringValue>().Value?.Trim() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(json);
        }
        catch (InvalidProtocolBufferException)
        {
            return false;
        }
    }

    private static bool TryReadLegacyJsonFromRawPayload(ByteString payload, out string json)
    {
        json = string.Empty;
        if (payload.IsEmpty)
            return false;

        var rawBytes = payload.ToByteArray();
        if (rawBytes.Length == 0)
            return false;

        var candidate = Encoding.UTF8.GetString(rawBytes).Trim();
        if (candidate.Length == 0 || candidate[0] != '{')
            return false;

        try
        {
            using var document = JsonDocument.Parse(candidate);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            json = candidate;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryDeserializeLegacyJson(string eventType, string json, out WorkflowRunEvent? evt)
    {
        evt = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (TryGetStringProperty(root, "type", out var jsonEventType) &&
                !string.IsNullOrWhiteSpace(jsonEventType) &&
                !string.Equals(jsonEventType, eventType, StringComparison.Ordinal))
            {
                return false;
            }

            var timestamp = TryGetInt64Property(root, "timestamp", out var parsedTimestamp)
                ? parsedTimestamp
                : (long?)null;

            evt = eventType switch
            {
                WorkflowRunEventTypes.RunStarted when TryGetRequiredStringProperty(root, "threadId", out var threadId) =>
                    new WorkflowRunStartedEvent
                    {
                        Timestamp = timestamp,
                        ThreadId = threadId,
                    },
                WorkflowRunEventTypes.RunFinished when TryGetRequiredStringProperty(root, "threadId", out var finishedThreadId) =>
                    new WorkflowRunFinishedEvent
                    {
                        Timestamp = timestamp,
                        ThreadId = finishedThreadId,
                        Result = TryGetProperty(root, "result", out var resultElement)
                            ? DeserializeLegacyJsonValue(resultElement)
                            : null,
                    },
                WorkflowRunEventTypes.RunError when TryGetRequiredStringProperty(root, "message", out var message) =>
                    new WorkflowRunErrorEvent
                    {
                        Timestamp = timestamp,
                        Message = message,
                        Code = TryGetStringProperty(root, "code", out var code) ? code : null,
                    },
                WorkflowRunEventTypes.StepStarted when TryGetRequiredStringProperty(root, "stepName", out var startedStepName) =>
                    new WorkflowStepStartedEvent
                    {
                        Timestamp = timestamp,
                        StepName = startedStepName,
                    },
                WorkflowRunEventTypes.StepFinished when TryGetRequiredStringProperty(root, "stepName", out var finishedStepName) =>
                    new WorkflowStepFinishedEvent
                    {
                        Timestamp = timestamp,
                        StepName = finishedStepName,
                    },
                WorkflowRunEventTypes.TextMessageStart
                    when TryGetRequiredStringProperty(root, "messageId", out var startMessageId) &&
                         TryGetRequiredStringProperty(root, "role", out var role) =>
                    new WorkflowTextMessageStartEvent
                    {
                        Timestamp = timestamp,
                        MessageId = startMessageId,
                        Role = role,
                    },
                WorkflowRunEventTypes.TextMessageContent
                    when TryGetRequiredStringProperty(root, "messageId", out var contentMessageId) &&
                         TryGetRequiredStringProperty(root, "delta", out var delta) =>
                    new WorkflowTextMessageContentEvent
                    {
                        Timestamp = timestamp,
                        MessageId = contentMessageId,
                        Delta = delta,
                    },
                WorkflowRunEventTypes.TextMessageEnd when TryGetRequiredStringProperty(root, "messageId", out var endMessageId) =>
                    new WorkflowTextMessageEndEvent
                    {
                        Timestamp = timestamp,
                        MessageId = endMessageId,
                    },
                WorkflowRunEventTypes.StateSnapshot =>
                    new WorkflowStateSnapshotEvent
                    {
                        Timestamp = timestamp,
                        Snapshot = TryGetProperty(root, "snapshot", out var snapshotElement)
                            ? DeserializeLegacyJsonValue(snapshotElement) ??
                              new Dictionary<string, object?>(StringComparer.Ordinal)
                            : new Dictionary<string, object?>(StringComparer.Ordinal),
                    },
                WorkflowRunEventTypes.ToolCallStart
                    when TryGetRequiredStringProperty(root, "toolCallId", out var toolCallId) &&
                         TryGetRequiredStringProperty(root, "toolName", out var toolName) =>
                    new WorkflowToolCallStartEvent
                    {
                        Timestamp = timestamp,
                        ToolCallId = toolCallId,
                        ToolName = toolName,
                    },
                WorkflowRunEventTypes.ToolCallEnd when TryGetRequiredStringProperty(root, "toolCallId", out var toolCallEndId) =>
                    new WorkflowToolCallEndEvent
                    {
                        Timestamp = timestamp,
                        ToolCallId = toolCallEndId,
                        Result = TryGetStringProperty(root, "result", out var result) ? result : null,
                    },
                WorkflowRunEventTypes.Custom when TryGetRequiredStringProperty(root, "name", out var name) =>
                    new WorkflowCustomEvent
                    {
                        Timestamp = timestamp,
                        Name = name,
                        Value = TryGetProperty(root, "value", out var valueElement)
                            ? DeserializeLegacyJsonValue(valueElement)
                            : null,
                    },
                _ => null,
            };

            return evt != null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement property)
    {
        property = default;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var candidate in element.EnumerateObject())
        {
            if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetRequiredStringProperty(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        return TryGetStringProperty(element, propertyName, out value) && !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetStringProperty(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!TryGetProperty(element, propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return false;

        value = property.GetString()?.Trim() ?? string.Empty;
        return true;
    }

    private static bool TryGetInt64Property(JsonElement element, string propertyName, out long value)
    {
        value = default;
        if (!TryGetProperty(element, propertyName, out var property))
            return false;

        if (property.ValueKind == JsonValueKind.Number)
            return property.TryGetInt64(out value);

        return property.ValueKind == JsonValueKind.String &&
               long.TryParse(property.GetString(), out value);
    }

    private static object? DeserializeLegacyJsonValue(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.True:
            case JsonValueKind.False:
                return element.GetBoolean();
            case JsonValueKind.Number:
                if (element.TryGetInt32(out var intValue))
                    return intValue;
                if (element.TryGetInt64(out var longValue))
                    return longValue;
                return element.GetDouble();
            case JsonValueKind.Object:
                var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                    dictionary[property.Name] = DeserializeLegacyJsonValue(property.Value);
                return dictionary;
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray())
                    list.Add(DeserializeLegacyJsonValue(item));
                return list;
            default:
                return null;
        }
    }
}
