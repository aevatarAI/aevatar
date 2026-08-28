using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Persistence;
using Google.Protobuf;
using Newtonsoft.Json;
using Orleans.Storage;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

/// <summary>
/// Adds a narrow recovery boundary around the existing Orleans JSON wire.
/// Valid rows intentionally retain that wire during a rolling deployment so
/// older silos can still read rows written by newer silos.
/// </summary>
internal sealed class RuntimeActorGrainStateStorageSerializer(
    IGrainStorageSerializer rollingCompatibleJsonSerializer) : IGrainStorageSerializer
{
    private const string LegacyJsonReferenceTokenValue = "$id";

    public BinaryData Serialize<T>(T input)
    {
        ArgumentNullException.ThrowIfNull(input);
        EnsureSupportedStateType<T>();

        if (input is RuntimeActorGrainState
            {
                Identity: null,
                StorageRecovery: { SourcePayload.IsEmpty: false } recovery,
            })
        {
            // A failed reconstruction attempt must not replace the original
            // evidence with an empty actor row that an older silo could accept.
            return BinaryData.FromBytes(recovery.SourcePayload.ToByteArray());
        }

        var serialized = rollingCompatibleJsonSerializer.Serialize(input);
        EnsureJsonObject(serialized, typeof(T));
        return serialized;
    }

    public T Deserialize<T>(BinaryData input)
    {
        ArgumentNullException.ThrowIfNull(input);
        EnsureSupportedStateType<T>();

        var bytes = input.ToArray();
        if (typeof(T) == typeof(RuntimeActorGrainState) &&
            IsLegacyJsonReferenceToken(input))
        {
            var recoveryState = new RuntimeActorGrainState
            {
                StorageRecovery = new RuntimeActorStateStorageRecovery
                {
                    Reason = RuntimeActorStateStorageRecoveryReason.LegacyJsonReferenceToken,
                    SourcePayload = ByteString.CopyFrom(bytes),
                },
            };
            return (T)(object)recoveryState;
        }

        return rollingCompatibleJsonSerializer.Deserialize<T>(input);
    }

    private static bool IsLegacyJsonReferenceToken(BinaryData input)
    {
        // Orleans' JSON storage serializer crosses the same BinaryData.ToString()
        // boundary and uses Newtonsoft. Reusing that reader family matters for
        // legacy rows with reader-ignored NUL or non-breaking-space padding.
        // Its typed conversion fails on the first root value before it validates
        // any trailing corrupt content, so only that root token is authoritative
        // for recognizing this otherwise unreadable legacy row.
        var text = input.ToString();
        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text[1..];

        try
        {
            using var textReader = new StringReader(text);
            using var reader = new JsonTextReader(textReader)
            {
                DateParseHandling = DateParseHandling.None,
            };

            return reader.Read() &&
                   reader.TokenType == JsonToken.String &&
                   string.Equals(
                       reader.Value as string,
                       LegacyJsonReferenceTokenValue,
                       StringComparison.Ordinal);
        }
        catch (JsonReaderException)
        {
            return false;
        }
    }

    private static void EnsureJsonObject(BinaryData serialized, Type stateType)
    {
        var bytes = serialized.ToArray().AsSpan();
        while (!bytes.IsEmpty && char.IsWhiteSpace((char)bytes[0]))
            bytes = bytes[1..];
        while (!bytes.IsEmpty && char.IsWhiteSpace((char)bytes[^1]))
            bytes = bytes[..^1];

        if (!bytes.IsEmpty && bytes[0] == (byte)'{' && bytes[^1] == (byte)'}')
            return;

        throw new InvalidOperationException(
            $"Rolling-compatible runtime actor state serialization for '{stateType.FullName}' " +
            "did not produce a JSON object; the durable write was rejected.");
    }

    private static void EnsureSupportedStateType<T>()
    {
        if (typeof(T) == typeof(RuntimeActorGrainState) ||
            typeof(T) == typeof(RuntimeActorCommittedStatePublicationGrainState))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{nameof(RuntimeActorGrainStateStorageSerializer)} only supports " +
            $"{nameof(RuntimeActorGrainState)} and " +
            $"{nameof(RuntimeActorCommittedStatePublicationGrainState)}, not '{typeof(T).FullName}'.");
    }
}
