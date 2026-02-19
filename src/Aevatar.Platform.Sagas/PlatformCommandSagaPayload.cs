using Aevatar.Platform.Application.Abstractions.Commands;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Platform.Sagas;

internal static class PlatformCommandSagaPayload
{
    private const string FieldType = "type";
    private const string EventType = "platform.command.status";
    private const string FieldCommandId = "command_id";
    private const string FieldSubsystem = "subsystem";
    private const string FieldCommand = "command";
    private const string FieldMethod = "method";
    private const string FieldTargetEndpoint = "target_endpoint";
    private const string FieldState = "state";
    private const string FieldSucceeded = "succeeded";
    private const string FieldResponseStatusCode = "response_status_code";
    private const string FieldResponseContentType = "response_content_type";
    private const string FieldResponseBody = "response_body";
    private const string FieldError = "error";
    private const string FieldAcceptedAt = "accepted_at";
    private const string FieldUpdatedAt = "updated_at";

    public static Struct Build(PlatformCommandStatus status)
    {
        var payload = new Struct();
        payload.Fields[FieldType] = Value.ForString(EventType);
        payload.Fields[FieldCommandId] = Value.ForString(status.CommandId);
        payload.Fields[FieldSubsystem] = Value.ForString(status.Subsystem);
        payload.Fields[FieldCommand] = Value.ForString(status.Command);
        payload.Fields[FieldMethod] = Value.ForString(status.Method);
        payload.Fields[FieldTargetEndpoint] = Value.ForString(status.TargetEndpoint);
        payload.Fields[FieldState] = Value.ForString(status.State);
        payload.Fields[FieldSucceeded] = Value.ForBool(status.Succeeded);
        payload.Fields[FieldResponseStatusCode] = status.ResponseStatusCode.HasValue
            ? Value.ForNumber(status.ResponseStatusCode.Value)
            : Value.ForNull();
        payload.Fields[FieldResponseContentType] = Value.ForString(status.ResponseContentType ?? string.Empty);
        payload.Fields[FieldResponseBody] = Value.ForString(status.ResponseBody ?? string.Empty);
        payload.Fields[FieldError] = Value.ForString(status.Error ?? string.Empty);
        payload.Fields[FieldAcceptedAt] = Value.ForString(status.AcceptedAt.ToString("O"));
        payload.Fields[FieldUpdatedAt] = Value.ForString(status.UpdatedAt.ToString("O"));
        return payload;
    }

    public static bool TryParse(EventEnvelope envelope, out PlatformCommandSagaSignal? signal)
    {
        signal = null;
        if (envelope.Payload == null || !envelope.Payload.Is(Struct.Descriptor))
            return false;

        var payload = envelope.Payload.Unpack<Struct>();
        if (!TryReadString(payload, FieldType, out var type) ||
            !string.Equals(type, EventType, StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryReadString(payload, FieldCommandId, out var commandId))
            return false;

        _ = TryReadString(payload, FieldSubsystem, out var subsystem);
        _ = TryReadString(payload, FieldCommand, out var command);
        _ = TryReadString(payload, FieldMethod, out var method);
        _ = TryReadString(payload, FieldTargetEndpoint, out var targetEndpoint);
        _ = TryReadString(payload, FieldState, out var state);
        _ = TryReadBool(payload, FieldSucceeded, out var succeeded);
        _ = TryReadInt(payload, FieldResponseStatusCode, out var responseStatusCode);
        _ = TryReadString(payload, FieldResponseContentType, out var responseContentType);
        _ = TryReadString(payload, FieldResponseBody, out var responseBody);
        _ = TryReadString(payload, FieldError, out var error);
        _ = TryReadDateTimeOffset(payload, FieldAcceptedAt, out var acceptedAt);
        _ = TryReadDateTimeOffset(payload, FieldUpdatedAt, out var updatedAt);

        signal = new PlatformCommandSagaSignal(
            CommandId: commandId,
            Subsystem: subsystem,
            Command: command,
            Method: method,
            TargetEndpoint: targetEndpoint,
            State: state,
            Succeeded: succeeded,
            ResponseStatusCode: responseStatusCode,
            ResponseContentType: responseContentType,
            ResponseBody: responseBody,
            Error: error,
            AcceptedAt: acceptedAt,
            UpdatedAt: updatedAt);
        return true;
    }

    private static bool TryReadString(Struct payload, string key, out string value)
    {
        value = string.Empty;
        if (!payload.Fields.TryGetValue(key, out var raw))
            return false;

        value = raw.StringValue ?? string.Empty;
        return true;
    }

    private static bool TryReadBool(Struct payload, string key, out bool value)
    {
        value = false;
        if (!payload.Fields.TryGetValue(key, out var raw))
            return false;

        value = raw.KindCase switch
        {
            Value.KindOneofCase.BoolValue => raw.BoolValue,
            Value.KindOneofCase.StringValue when bool.TryParse(raw.StringValue, out var parsed) => parsed,
            _ => false,
        };
        return true;
    }

    private static bool TryReadInt(Struct payload, string key, out int? value)
    {
        value = null;
        if (!payload.Fields.TryGetValue(key, out var raw))
            return false;

        switch (raw.KindCase)
        {
            case Value.KindOneofCase.NumberValue:
                value = (int)raw.NumberValue;
                return true;
            case Value.KindOneofCase.StringValue when int.TryParse(raw.StringValue, out var parsed):
                value = parsed;
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadDateTimeOffset(Struct payload, string key, out DateTimeOffset value)
    {
        value = default;
        if (!payload.Fields.TryGetValue(key, out var raw))
            return false;

        return DateTimeOffset.TryParse(raw.StringValue, out value);
    }
}

internal sealed record PlatformCommandSagaSignal(
    string CommandId,
    string Subsystem,
    string Command,
    string Method,
    string TargetEndpoint,
    string State,
    bool Succeeded,
    int? ResponseStatusCode,
    string ResponseContentType,
    string ResponseBody,
    string Error,
    DateTimeOffset AcceptedAt,
    DateTimeOffset UpdatedAt);
