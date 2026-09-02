using System.Security.Cryptography;
using Google.Protobuf;

namespace Aevatar.Audit.Core.Projection;

internal static class AuditRecordContentHasher
{
    public static string Compute(Audit.AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var semanticRecord = record.Clone();
        semanticRecord.OccurredAt = null;
        semanticRecord.RecordedAt = null;
        return Convert.ToHexString(SHA256.HashData(semanticRecord.ToByteArray())).ToLowerInvariant();
    }
}
