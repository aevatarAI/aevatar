using Aevatar.AI.Abstractions;
using Google.Protobuf;

namespace Aevatar.GAgents.NyxidChat;

internal static class NyxIdChatRecoverySecretPayloadCodec
{
    internal const string FormatMarker = "aevatar.nyxid-chat.exact-service-recovery";
    internal const uint CurrentSchemaVersion = 1;

    internal sealed record Decoded(
        AgentToolCredentialsPayload Credentials,
        NyxIdChatOperationDispatchCommand? ExactServiceCommand,
        bool IsWrapped);

    public static string Encode(
        AgentToolCredentialsPayload credentials,
        NyxIdChatOperationDispatchCommand exactServiceCommand) =>
        Convert.ToBase64String(new NyxIdChatRecoverySecretPayload
        {
            Format = FormatMarker,
            SchemaVersion = CurrentSchemaVersion,
            Credentials = credentials.Clone(),
            ExactServiceCommand = exactServiceCommand.Clone(),
        }.ToByteArray());

    public static string Encode(Decoded decoded, AgentToolCredentialsPayload credentials) =>
        decoded.IsWrapped && decoded.ExactServiceCommand is not null
            ? Encode(credentials, decoded.ExactServiceCommand)
            : Convert.ToBase64String(credentials.ToByteArray());

    public static bool TryDecode(string? encoded, out Decoded decoded)
    {
        decoded = null!;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded ?? string.Empty);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            var wrapped = NyxIdChatRecoverySecretPayload.Parser.ParseFrom(bytes);
            var hasWrapperShape =
                !string.IsNullOrEmpty(wrapped.Format) ||
                wrapped.SchemaVersion != 0 ||
                wrapped.Credentials is not null ||
                wrapped.ExactServiceCommand is not null;
            if (hasWrapperShape)
            {
                if (!string.Equals(wrapped.Format, FormatMarker, StringComparison.Ordinal) ||
                    wrapped.SchemaVersion != CurrentSchemaVersion ||
                    wrapped.Credentials is null ||
                    wrapped.ExactServiceCommand is null ||
                    !HasCredential(wrapped.Credentials))
                {
                    return false;
                }

                decoded = new Decoded(
                    wrapped.Credentials.Clone(),
                    wrapped.ExactServiceCommand.Clone(),
                    IsWrapped: true);
                return true;
            }
        }
        catch (InvalidProtocolBufferException)
        {
            // Fall through to the pre-wrapper credential payload.
        }

        try
        {
            var credentials = AgentToolCredentialsPayload.Parser.ParseFrom(bytes);
            if (!HasCredential(credentials))
                return false;
            decoded = new Decoded(credentials, ExactServiceCommand: null, IsWrapped: false);
            return true;
        }
        catch (InvalidProtocolBufferException)
        {
            return false;
        }
    }

    private static bool HasCredential(AgentToolCredentialsPayload credentials) =>
        !string.IsNullOrWhiteSpace(credentials.NyxIdAccessToken) ||
        !string.IsNullOrWhiteSpace(credentials.NyxIdOrgToken) ||
        !string.IsNullOrWhiteSpace(credentials.SenderNyxIdAccessToken) ||
        !string.IsNullOrWhiteSpace(credentials.SourceReadableNyxIdAccessToken);
}
