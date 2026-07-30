using System.Security.Cryptography;
using Aevatar.AI.Abstractions;
using Google.Protobuf;

namespace Aevatar.AI.Core.AgentProfiles;

public static class AgentProfileSnapshotCodec
{
    private const int Sha256Length = 32;

    public static AgentProfileSnapshot Seal(AgentProfileSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.DeterministicPolicySha256.IsEmpty)
            throw new ArgumentException("The profile snapshot digest must be empty before sealing.", nameof(snapshot));

        var sealedSnapshot = snapshot.Clone();
        sealedSnapshot.DeterministicPolicySha256 = ByteString.CopyFrom(
            SHA256.HashData(SerializeWithoutDigest(sealedSnapshot)));
        return sealedSnapshot;
    }

    public static bool Verify(AgentProfileSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.DeterministicPolicySha256.Length != Sha256Length)
            return false;

        var expected = SHA256.HashData(SerializeWithoutDigest(snapshot));
        return CryptographicOperations.FixedTimeEquals(
            expected,
            snapshot.DeterministicPolicySha256.Span);
    }

    public static bool ByteEquivalent(AgentProfileSnapshot left, AgentProfileSnapshot right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return SerializeDeterministically(left).AsSpan()
            .SequenceEqual(SerializeDeterministically(right));
    }

    private static byte[] SerializeWithoutDigest(AgentProfileSnapshot snapshot)
    {
        var hashInput = snapshot.Clone();
        hashInput.DeterministicPolicySha256 = ByteString.Empty;
        return SerializeDeterministically(hashInput);
    }

    private static byte[] SerializeDeterministically(IMessage message)
    {
        using var stream = new MemoryStream(message.CalculateSize());
        using (var output = new CodedOutputStream(stream, leaveOpen: true))
        {
            output.Deterministic = true;
            message.WriteTo(output);
            output.Flush();
        }

        return stream.ToArray();
    }
}
