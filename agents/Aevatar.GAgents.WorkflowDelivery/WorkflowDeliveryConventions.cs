using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;

namespace Aevatar.GAgents.WorkflowDelivery;

public static class WorkflowDeliveryConventions
{
    public const string ActorIdPrefix = "workflow-delivery:";

    public static string BuildActorId(string deliveryId) =>
        ActorIdPrefix + NormalizeRequired(deliveryId, nameof(deliveryId));

    public static string BuildInstallationId(string deliveryId, string scopeId) =>
        "installation-" + Digest($"{NormalizeRequired(deliveryId, nameof(deliveryId))}\n{NormalizeRequired(scopeId, nameof(scopeId))}")[..24];

    public static string BuildPackageVersionId(string workflowName, string packageHash) =>
        $"{NormalizeRequired(workflowName, nameof(workflowName))}@{NormalizeRequired(packageHash, nameof(packageHash))[..Math.Min(16, packageHash.Trim().Length)]}";

    public static string ComputePackageHash(WorkflowPackageVersionSnapshot package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var content = package.Clone();
        content.PackageVersionId = string.Empty;
        content.Version = string.Empty;
        content.PackageHash = string.Empty;
        content.CreatedBy = string.Empty;
        content.CreatedAtUtc = null;
        return Digest(content.ToByteArray());
    }

    private static string Digest(string value) => Digest(Encoding.UTF8.GetBytes(value));

    private static string Digest(ReadOnlySpan<byte> value) =>
        Convert.ToHexStringLower(SHA256.HashData(value));

    /// <summary>
    /// Normalizes a single-line identifier. Rejects every control character, so it must not
    /// be used for document content — see <see cref="NormalizeRequiredDocument"/>.
    /// </summary>
    internal static string NormalizeRequired(string? value, string name)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new InvalidOperationException($"{name} is required.");
        if (normalized.Any(char.IsControl))
            throw new InvalidOperationException($"{name} must not contain control characters.");
        return normalized;
    }

    /// <summary>
    /// Normalizes required multi-line content such as workflow YAML. Line breaks and tabs are
    /// structural there, so only the control characters that cannot appear in a text document
    /// are rejected. Validating YAML with <see cref="NormalizeRequired"/> rejects every
    /// document that has more than one line.
    /// </summary>
    internal static string NormalizeRequiredDocument(string? value, string name)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new InvalidOperationException($"{name} is required.");
        if (normalized.Any(static character => char.IsControl(character) && character is not ('\n' or '\r' or '\t')))
            throw new InvalidOperationException($"{name} must not contain control characters other than tab, carriage return, and line feed.");
        return normalized;
    }
}
