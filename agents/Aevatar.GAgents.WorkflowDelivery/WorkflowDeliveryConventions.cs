using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.WorkflowDelivery;

public static class WorkflowDeliveryConventions
{
    public const string ActorIdPrefix = "workflow-delivery:";
    private const int MaximumAcceptanceInputFields = 32;
    private const int MaximumAcceptanceInputKeyLength = 128;
    private const int MaximumAcceptanceStringLength = 4096;
    private const int MaximumAcceptanceDateOffsetDays = 3650;

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
        return Digest(SerializeDeterministically(content));
    }

    public static void ValidateAcceptanceInput(WorkflowDeliveryAcceptanceInputRecipe? recipe)
    {
        if (recipe?.Literals == null)
            throw new InvalidOperationException("workflow delivery acceptance input recipe is required.");
        if (recipe.Literals.Fields.Count + recipe.Bindings.Count > MaximumAcceptanceInputFields)
            throw new InvalidOperationException("workflow delivery acceptance input has too many fields.");

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, value) in recipe.Literals.Fields)
        {
            ValidateAcceptanceInputKey(key);
            keys.Add(key);

            if (value == null || value.KindCase is not (
                    Value.KindOneofCase.StringValue or
                    Value.KindOneofCase.NumberValue or
                    Value.KindOneofCase.BoolValue))
            {
                throw new InvalidOperationException(
                    "workflow delivery acceptance input values must be typed scalars.");
            }
            if (value.KindCase == Value.KindOneofCase.StringValue &&
                value.StringValue.Length > MaximumAcceptanceStringLength)
            {
                throw new InvalidOperationException(
                    "workflow delivery acceptance input string is too long.");
            }
            if (value.KindCase == Value.KindOneofCase.NumberValue &&
                !double.IsFinite(value.NumberValue))
            {
                throw new InvalidOperationException(
                    "workflow delivery acceptance input number must be finite.");
            }
        }

        string? previousBindingKey = null;
        foreach (var binding in recipe.Bindings)
        {
            ArgumentNullException.ThrowIfNull(binding);
            ValidateAcceptanceInputKey(binding.Key);
            if (previousBindingKey != null &&
                string.CompareOrdinal(previousBindingKey, binding.Key) >= 0)
            {
                throw new InvalidOperationException(
                    "workflow delivery acceptance input bindings must use stable key ordering.");
            }
            previousBindingKey = binding.Key;
            if (!keys.Add(binding.Key))
            {
                throw new InvalidOperationException(
                    "workflow delivery acceptance input keys must be unique.");
            }
            if (binding.Prefix.Any(char.IsControl) || binding.Suffix.Any(char.IsControl) ||
                binding.Prefix.Length + binding.Suffix.Length > MaximumAcceptanceStringLength)
            {
                throw new InvalidOperationException(
                    "workflow delivery acceptance input binding affixes are invalid.");
            }

            switch (binding.SourceCase)
            {
                case WorkflowDeliveryAcceptanceInputBinding.SourceOneofCase.InstallationCreatedAtUtc:
                    if (binding.InstallationCreatedAtUtc == null ||
                        binding.InstallationCreatedAtUtc.DateProjection is not (
                            WorkflowDeliveryAcceptanceDateProjection.UtcDate or
                            WorkflowDeliveryAcceptanceDateProjection.UtcYearMonth or
                            WorkflowDeliveryAcceptanceDateProjection.UtcIsoWeek or
                            WorkflowDeliveryAcceptanceDateProjection.UtcCompactDate) ||
                        binding.InstallationCreatedAtUtc.DayOffset is < -MaximumAcceptanceDateOffsetDays or
                            > MaximumAcceptanceDateOffsetDays)
                    {
                        throw new InvalidOperationException(
                            "workflow delivery acceptance date binding is invalid.");
                    }
                    break;
                case WorkflowDeliveryAcceptanceInputBinding.SourceOneofCase.AuthenticatedOwnerExternalUserId:
                    if (binding.AuthenticatedOwnerExternalUserId == null)
                        throw new InvalidOperationException(
                            "workflow delivery acceptance owner binding is invalid.");
                    break;
                default:
                    throw new InvalidOperationException(
                        "workflow delivery acceptance input binding source is unsupported.");
            }
        }
    }

    public static Struct ResolveAcceptanceInput(
        WorkflowDeliveryAcceptanceInputRecipe? recipe,
        Timestamp? installationCreatedAtUtc,
        string? authenticatedOwnerExternalUserId)
    {
        ValidateAcceptanceInput(recipe);
        var createdAtUtc = RequireTimestamp(installationCreatedAtUtc).ToDateTime().ToUniversalTime();
        var resolved = new Struct();
        foreach (var literal in recipe!.Literals.Fields.OrderBy(static item => item.Key, StringComparer.Ordinal))
            resolved.Fields.Add(literal.Key, literal.Value.Clone());

        foreach (var binding in recipe.Bindings)
        {
            var source = binding.SourceCase switch
            {
                WorkflowDeliveryAcceptanceInputBinding.SourceOneofCase.InstallationCreatedAtUtc =>
                    ProjectInstallationDate(createdAtUtc, binding.InstallationCreatedAtUtc),
                WorkflowDeliveryAcceptanceInputBinding.SourceOneofCase.AuthenticatedOwnerExternalUserId =>
                    NormalizeRequired(authenticatedOwnerExternalUserId, "authenticated_owner.subject_external_user_id"),
                _ => throw new InvalidOperationException(
                    "workflow delivery acceptance input binding source is unsupported."),
            };
            var value = string.Concat(binding.Prefix, source, binding.Suffix);
            if (value.Length > MaximumAcceptanceStringLength)
            {
                throw new InvalidOperationException(
                    "workflow delivery resolved acceptance input string is too long.");
            }
            resolved.Fields.Add(binding.Key, Value.ForString(value));
        }

        return resolved;
    }

    private static string ProjectInstallationDate(
        DateTime createdAtUtc,
        WorkflowDeliveryInstallationCreatedAtUtcInput specification)
    {
        DateTime date;
        try
        {
            date = createdAtUtc.Date.AddDays(specification.DayOffset);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidOperationException(
                "workflow delivery acceptance date binding exceeds the supported timestamp range.",
                exception);
        }

        return specification.DateProjection switch
        {
            WorkflowDeliveryAcceptanceDateProjection.UtcDate =>
                date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            WorkflowDeliveryAcceptanceDateProjection.UtcYearMonth =>
                date.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            WorkflowDeliveryAcceptanceDateProjection.UtcIsoWeek =>
                ISOWeek.GetYear(date).ToString("D4", CultureInfo.InvariantCulture) +
                "-W" + ISOWeek.GetWeekOfYear(date).ToString("D2", CultureInfo.InvariantCulture),
            WorkflowDeliveryAcceptanceDateProjection.UtcCompactDate =>
                date.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException(
                "workflow delivery acceptance date projection is unsupported."),
        };
    }

    private static Timestamp RequireTimestamp(Timestamp? value) =>
        value ?? throw new InvalidOperationException(
            "workflow delivery installation creation timestamp is required.");

    private static void ValidateAcceptanceInputKey(string key)
    {
        var normalizedKey = NormalizeRequired(key, "acceptance_input key");
        if (normalizedKey.Length > MaximumAcceptanceInputKeyLength ||
            !string.Equals(normalizedKey, key, StringComparison.Ordinal) ||
            key.Any(char.IsControl))
        {
            throw new InvalidOperationException("workflow delivery acceptance input key is invalid.");
        }
    }

    private static string Digest(string value) => Digest(Encoding.UTF8.GetBytes(value));

    private static byte[] SerializeDeterministically(IMessage message)
    {
        using var stream = new MemoryStream(message.CalculateSize());
        using var output = new CodedOutputStream(stream, leaveOpen: true) { Deterministic = true };
        message.WriteTo(output);
        output.Flush();
        return stream.ToArray();
    }

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
