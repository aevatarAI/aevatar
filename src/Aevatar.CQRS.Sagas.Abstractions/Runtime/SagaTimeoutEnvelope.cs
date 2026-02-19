using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Sagas.Abstractions.Runtime;

public static class SagaTimeoutEnvelope
{
    public const string PublisherId = "cqrs.sagas.timeout";
    private const string FieldType = "type";
    private const string FieldSagaName = "saga_name";
    private const string FieldTimeoutName = "timeout_name";
    private const string FieldActorId = "actor_id";
    private const string FieldMetadata = "metadata";
    private const string TimeoutTypeValue = "saga.timeout";

    public static EventEnvelope Create(
        string sagaName,
        string correlationId,
        string timeoutName,
        string actorId,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sagaName);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeoutName);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        var payload = new Struct();
        payload.Fields[FieldType] = Value.ForString(TimeoutTypeValue);
        payload.Fields[FieldSagaName] = Value.ForString(sagaName);
        payload.Fields[FieldTimeoutName] = Value.ForString(timeoutName);
        payload.Fields[FieldActorId] = Value.ForString(actorId);

        if (metadata is { Count: > 0 })
        {
            var metadataStruct = new Struct();
            foreach (var (key, value) in metadata)
            {
                if (string.IsNullOrWhiteSpace(key))
                    continue;
                metadataStruct.Fields[key] = Value.ForString(value ?? string.Empty);
            }

            if (metadataStruct.Fields.Count > 0)
                payload.Fields[FieldMetadata] = Value.ForStruct(metadataStruct);
        }

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            PublisherId = PublisherId,
            Direction = EventDirection.Self,
            CorrelationId = correlationId,
            TargetActorId = actorId,
        };
    }

    public static bool IsTimeout(EventEnvelope envelope) =>
        TryParse(envelope, out _);

    public static bool TryParse(EventEnvelope envelope, out SagaTimeoutSignal? signal)
    {
        signal = null;
        if (envelope.Payload == null || !envelope.Payload.Is(Struct.Descriptor))
            return false;

        var payload = envelope.Payload.Unpack<Struct>();
        if (!TryRead(payload, FieldType, out var type) ||
            !string.Equals(type, TimeoutTypeValue, StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryRead(payload, FieldSagaName, out var sagaName) ||
            !TryRead(payload, FieldTimeoutName, out var timeoutName) ||
            !TryRead(payload, FieldActorId, out var actorId))
        {
            return false;
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (payload.Fields.TryGetValue(FieldMetadata, out var metadataValue) &&
            metadataValue.KindCase == Value.KindOneofCase.StructValue &&
            metadataValue.StructValue != null)
        {
            foreach (var (key, value) in metadataValue.StructValue.Fields)
            {
                metadata[key] = value.StringValue ?? string.Empty;
            }
        }

        signal = new SagaTimeoutSignal(
            sagaName,
            timeoutName,
            actorId,
            metadata);
        return true;
    }

    private static bool TryRead(Struct payload, string key, out string value)
    {
        value = string.Empty;
        if (!payload.Fields.TryGetValue(key, out var raw))
            return false;

        value = raw.StringValue ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }
}

public sealed record SagaTimeoutSignal(
    string SagaName,
    string TimeoutName,
    string ActorId,
    IReadOnlyDictionary<string, string> Metadata);
