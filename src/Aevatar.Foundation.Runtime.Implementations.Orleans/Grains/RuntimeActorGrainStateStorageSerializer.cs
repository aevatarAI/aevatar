using System.Text.Json;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Persistence;
using Google.Protobuf;
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
    private static ReadOnlySpan<byte> LegacyJsonReferenceTokenValue => "$id"u8;
    private static ReadOnlySpan<byte> Utf8ByteOrderMark => "\uFEFF"u8;

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
            IsLegacyJsonReferenceToken(bytes))
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

    private static bool IsLegacyJsonReferenceToken(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(Utf8ByteOrderMark))
            bytes = bytes[Utf8ByteOrderMark.Length..];

        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });

            return reader.Read() &&
                   reader.TokenType == JsonTokenType.String &&
                   reader.ValueTextEquals(LegacyJsonReferenceTokenValue) &&
                   !reader.Read();
        }
        catch (JsonException)
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
