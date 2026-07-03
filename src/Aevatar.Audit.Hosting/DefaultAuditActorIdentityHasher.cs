using System.Security.Cryptography;
using System.Text;

namespace Aevatar.Audit.Hosting;

public sealed class DefaultAuditActorIdentityHasher : IAuditActorIdentityHasher
{
    public string ComputeAuditActorId(AuditExternalActorIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var provider = NormalizeRequired(identity.Provider, nameof(identity.Provider));
        var subject = NormalizeRequired(identity.Subject, nameof(identity.Subject));
        var canonical = $"{provider.ToLowerInvariant()}\u001f{subject}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return "audit_actor:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizeRequired(string value, string paramName)
    {
        var normalized = value.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException($"{paramName} is required.", paramName);

        return normalized;
    }
}
