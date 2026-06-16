using System.Security.Cryptography;
using System.Text;

namespace Aevatar.Workflow.Core.Primitives;

public static class WorkflowLeaseActorId
{
    private const string Prefix = "workflow.lease:";

    public static string NormalizeKey(string? leaseKey)
    {
        var normalized = leaseKey?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("workflow lease key is required.", nameof(leaseKey));

        return normalized;
    }

    public static string FromKey(string? leaseKey)
    {
        var canonicalKey = NormalizeKey(leaseKey);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalKey));
        var builder = new StringBuilder(Prefix.Length + 24);
        builder.Append(Prefix);
        for (var i = 0; i < 12; i++)
            builder.Append(bytes[i].ToString("x2"));

        return builder.ToString();
    }
}
