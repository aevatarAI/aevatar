using System.Security.Cryptography;
using Google.Protobuf;

namespace Aevatar.GAgents.NyxidChat;

internal static class NyxIdChatActionPostconditionEvidence
{
    internal const int Sha256Length = SHA256.HashSizeInBytes;

    internal static ByteString ComputeVerificationInputSha256(
        NyxIdChatActionPostconditionInput? input)
    {
        var canonical = input?.Clone() ?? new NyxIdChatActionPostconditionInput();
        // Credentials are execution-only and must never affect or enter the
        // durable verification binding.
        canonical.ToolContext = null;

        using var stream = new MemoryStream(canonical.CalculateSize());
        using var output = new CodedOutputStream(stream, leaveOpen: true)
        {
            Deterministic = true,
        };
        canonical.WriteTo(output);
        output.Flush();
        return ByteString.CopyFrom(SHA256.HashData(stream.ToArray()));
    }

    internal static bool Matches(
        NyxIdChatActionPostconditionInput? expectedInput,
        NyxIdChatActionPostconditionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var actual = result.VerificationInputSha256;
        if (actual.Length != Sha256Length)
            return false;

        var expected = ComputeVerificationInputSha256(expectedInput);
        return CryptographicOperations.FixedTimeEquals(expected.Span, actual.Span);
    }
}
