using System.Security.Cryptography;
using Google.Protobuf;

namespace Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;

internal static class GarnetSecretRecordIds
{
    public static string NewSecretReference(string prefix)
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return prefix + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static byte[] VaultAssociatedData(GarnetSecretVaultRecord record)
    {
        return new GarnetSecretVaultAssociatedData
        {
            Ref = record.Ref,
            Purpose = record.Purpose,
            OwnerScopeKey = record.OwnerScopeKey,
            SubjectId = record.SubjectId,
            Version = record.Version,
            Status = record.Status,
            Fingerprint = record.Fingerprint,
            CreatedAtUnixMs = record.CreatedAtUnixMs,
            RotatedAtUnixMs = record.RotatedAtUnixMs,
            RevokedAtUnixMs = record.RevokedAtUnixMs,
            ExpiresAtUnixMs = record.ExpiresAtUnixMs,
        }.ToByteArray();
    }

    public static byte[] RuntimeAssociatedData(GarnetRuntimeSecretRecord record)
    {
        return new GarnetRuntimeSecretAssociatedData
        {
            Ref = record.Ref,
            Purpose = record.Purpose,
            OwnerRunId = record.OwnerRunId,
            OwnerStepId = record.OwnerStepId,
            ConsumeOnce = record.ConsumeOnce,
            Status = record.Status,
            Fingerprint = record.Fingerprint,
            CreatedAtUnixMs = record.CreatedAtUnixMs,
            ExpiresAtUnixMs = record.ExpiresAtUnixMs,
            ConsumedAtUnixMs = record.ConsumedAtUnixMs,
            RevokedAtUnixMs = record.RevokedAtUnixMs,
        }.ToByteArray();
    }
}
