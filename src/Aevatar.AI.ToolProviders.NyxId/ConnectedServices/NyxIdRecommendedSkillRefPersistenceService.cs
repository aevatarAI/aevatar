using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

public enum NyxIdRecommendedSkillRefPersistenceStatus
{
    Succeeded,
    EmptyInput,
    ReadDenied,
    ReadUnavailable,
    WriteDenied,
    WriteUnavailable,
}

public sealed record NyxIdRecommendedSkillRefPersistenceResult(
    NyxIdRecommendedSkillRefPersistenceStatus Status,
    IReadOnlyList<NyxIdRecommendedSkillRef> Refs,
    string FailureCode)
{
    public bool IsSuccess => Status is NyxIdRecommendedSkillRefPersistenceStatus.Succeeded;

    public static NyxIdRecommendedSkillRefPersistenceResult Succeeded(
        IReadOnlyList<NyxIdRecommendedSkillRef> refs) =>
        new(NyxIdRecommendedSkillRefPersistenceStatus.Succeeded, refs, string.Empty);

    public static NyxIdRecommendedSkillRefPersistenceResult EmptyInput() =>
        new(NyxIdRecommendedSkillRefPersistenceStatus.EmptyInput, [], string.Empty);

    public static NyxIdRecommendedSkillRefPersistenceResult Failed(
        NyxIdRecommendedSkillRefPersistenceStatus status,
        string failureCode) =>
        new(status, [], failureCode);
}

public sealed class NyxIdRecommendedSkillRefPersistenceService
{
    private readonly NyxIdApiClient _client;
    private readonly ILogger<NyxIdRecommendedSkillRefPersistenceService> _logger;

    public NyxIdRecommendedSkillRefPersistenceService(
        NyxIdApiClient client,
        ILogger<NyxIdRecommendedSkillRefPersistenceService>? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? NullLogger<NyxIdRecommendedSkillRefPersistenceService>.Instance;
    }

    public async Task<NyxIdRecommendedSkillRefPersistenceResult> PersistRecommendedSkillRefsAsync(
        string serverToken,
        NyxIdServiceInstance instance,
        IReadOnlyList<NyxIdRecommendedSkillRef> refs,
        CancellationToken ct)
    {
        if (refs.Count == 0)
            return NyxIdRecommendedSkillRefPersistenceResult.EmptyInput();

        if (!TrySelectCatalogServiceKeyId(instance, out var catalogServiceKeyId))
        {
            return NyxIdRecommendedSkillRefPersistenceResult.Failed(
                NyxIdRecommendedSkillRefPersistenceStatus.WriteUnavailable,
                "catalog_service_id_missing");
        }

        IReadOnlyList<NyxIdRecommendedSkillRef> currentRefs = instance.RecommendedSkillRefs
            .Select(static skillRef => skillRef.Clone())
            .ToArray();
        if (currentRefs.Count == 0)
        {
            var currentResponse = await _client.GetServiceAsync(
                serverToken,
                catalogServiceKeyId,
                ct).ConfigureAwait(false);
            var readFailure = ClassifyFailure(
                currentResponse,
                NyxIdRecommendedSkillRefPersistenceStatus.ReadDenied,
                NyxIdRecommendedSkillRefPersistenceStatus.ReadUnavailable);
            if (readFailure is not null)
                return readFailure;

            currentRefs = ParseRecommendedSkillRefs(currentResponse);
        }

        var mergedRefs = MergeRefs(currentRefs, refs);
        if (SameRefs(currentRefs, mergedRefs))
            return NyxIdRecommendedSkillRefPersistenceResult.Succeeded(currentRefs);

        return await WriteRecommendedSkillRefsAsync(
            serverToken,
            catalogServiceKeyId,
            mergedRefs,
            ct).ConfigureAwait(false);
    }

    private static bool TrySelectCatalogServiceKeyId(
        NyxIdServiceInstance instance,
        out string catalogServiceKeyId)
    {
        catalogServiceKeyId = instance.CatalogServiceId?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(catalogServiceKeyId);
    }

    private async Task<NyxIdRecommendedSkillRefPersistenceResult> WriteRecommendedSkillRefsAsync(
        string serverToken,
        string servicePersistenceId,
        IReadOnlyList<NyxIdRecommendedSkillRef> refs,
        CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            recommended_skill_refs = refs.Select(ToJsonContract).ToArray(),
        });

        var updateResponse = await _client.UpdateServiceAsync(
            serverToken,
            servicePersistenceId,
            body,
            ct).ConfigureAwait(false);
        var writeFailure = ClassifyFailure(
            updateResponse,
            NyxIdRecommendedSkillRefPersistenceStatus.WriteDenied,
            NyxIdRecommendedSkillRefPersistenceStatus.WriteUnavailable);
        if (writeFailure is not null)
        {
            LogRecommendedSkillRefsUpdateFailure(
                servicePersistenceId,
                refs,
                updateResponse,
                writeFailure);
            return writeFailure;
        }

        return NyxIdRecommendedSkillRefPersistenceResult.Succeeded(refs);
    }

    private void LogRecommendedSkillRefsUpdateFailure(
        string catalogServiceKeyId,
        IReadOnlyList<NyxIdRecommendedSkillRef> refs,
        string updateResponse,
        NyxIdRecommendedSkillRefPersistenceResult writeFailure)
    {
        _logger.LogWarning(
            "NyxID recommended skill refs update failed. catalogServiceId={CatalogServiceId} status={Status} failureCode={FailureCode} refCount={RefCount} refs={Refs} response={Response}",
            catalogServiceKeyId,
            writeFailure.Status,
            writeFailure.FailureCode,
            refs.Count,
            BuildRefSummary(refs),
            LimitDiagnosticText(updateResponse, 1200));
    }

    private static string BuildRefSummary(IReadOnlyList<NyxIdRecommendedSkillRef> refs) =>
        string.Join(
            "; ",
            refs.Select(static skillRef =>
                string.Join(
                    '/',
                    SourceToJson(skillRef.Source),
                    skillRef.SkillId.Trim(),
                    skillRef.LiteralVersion.Trim(),
                    LimitDiagnosticText(skillRef.ManifestDigest.Trim().ToLowerInvariant(), 16))));

    private static string LimitDiagnosticText(string value, int maxLength)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ');
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength];
    }

    private static IReadOnlyList<NyxIdRecommendedSkillRef> ParseRecommendedSkillRefs(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = UnwrapServiceNode(document.RootElement);
        if (!root.TryGetProperty("recommended_skill_refs", out var refsElement) ||
            refsElement.ValueKind == JsonValueKind.Null)
        {
            return [];
        }
        if (refsElement.ValueKind != JsonValueKind.Array)
            return [];

        var refs = new List<NyxIdRecommendedSkillRef>();
        foreach (var refElement in refsElement.EnumerateArray())
        {
            var skillRef = ParseRecommendedSkillRef(refElement);
            if (skillRef is not null)
                refs.Add(skillRef);
        }

        return refs;
    }

    private static JsonElement UnwrapServiceNode(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return root;
        foreach (var property in new[] { "key", "service", "data" })
        {
            if (root.TryGetProperty(property, out var nested) && nested.ValueKind == JsonValueKind.Object)
                return nested;
        }

        return root;
    }

    private static NyxIdRecommendedSkillRef? ParseRecommendedSkillRef(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;
        if (!TryParseRecommendedSkillSource(ReadString(item, "source") ?? ReadString(item, "provider"), out var source))
            return null;
        var skillId = ReadString(item, "skill_id") ?? ReadString(item, "id") ?? ReadString(item, "guid");
        var literalVersion = ReadString(item, "literal_version") ?? ReadString(item, "version");
        var manifestDigest = ReadString(item, "manifest_digest") ??
                             ReadString(item, "digest") ??
                             ReadString(item, "skill_hash") ??
                             ReadString(item, "sha256");
        if (string.IsNullOrWhiteSpace(skillId) ||
            string.IsNullOrWhiteSpace(literalVersion) ||
            string.IsNullOrWhiteSpace(manifestDigest))
        {
            return null;
        }

        return new NyxIdRecommendedSkillRef
        {
            Source = source,
            SkillId = skillId.Trim(),
            LiteralVersion = literalVersion.Trim(),
            ManifestDigest = manifestDigest.Trim(),
            DisplayName = ReadString(item, "display_name") ?? ReadString(item, "name") ?? string.Empty,
            RecommendationName = ReadString(item, "recommendation_name") ?? ReadString(item, "recommended_name") ?? string.Empty,
            Revision = ReadString(item, "revision") ?? string.Empty,
        };
    }

    private static IReadOnlyList<NyxIdRecommendedSkillRef> MergeRefs(
        IReadOnlyList<NyxIdRecommendedSkillRef> currentRefs,
        IReadOnlyList<NyxIdRecommendedSkillRef> createdRefs)
    {
        var mergedRefs = currentRefs
            .Select(static skillRef => skillRef.Clone())
            .ToList();
        foreach (var skillRef in createdRefs)
        {
            if (mergedRefs.Any(existing => SameRef(existing, skillRef)))
                continue;

            var replacementIndex = mergedRefs.FindIndex(existing => SameRecommendationSlot(existing, skillRef));
            if (replacementIndex >= 0)
            {
                mergedRefs[replacementIndex] = skillRef.Clone();
                continue;
            }

            mergedRefs.Add(skillRef.Clone());
        }

        return mergedRefs;
    }

    private static bool SameRefs(
        IReadOnlyList<NyxIdRecommendedSkillRef> left,
        IReadOnlyList<NyxIdRecommendedSkillRef> right) =>
        left.Count == right.Count &&
        left.Zip(right).All(pair => SameRef(pair.First, pair.Second));

    private static bool SameRef(NyxIdRecommendedSkillRef left, NyxIdRecommendedSkillRef right) =>
        left.Source == right.Source &&
        string.Equals(left.SkillId, right.SkillId, StringComparison.Ordinal) &&
        string.Equals(left.LiteralVersion, right.LiteralVersion, StringComparison.Ordinal) &&
        string.Equals(left.ManifestDigest, right.ManifestDigest, StringComparison.Ordinal);

    private static bool SameRecommendationSlot(NyxIdRecommendedSkillRef left, NyxIdRecommendedSkillRef right)
    {
        var leftName = RefPersistenceName(left);
        var rightName = RefPersistenceName(right);
        return left.Source == right.Source &&
               !string.IsNullOrWhiteSpace(leftName) &&
               string.Equals(leftName, rightName, StringComparison.Ordinal);
    }

    private static string RefPersistenceName(NyxIdRecommendedSkillRef skillRef) =>
        string.IsNullOrWhiteSpace(skillRef.RecommendationName)
            ? skillRef.DisplayName.Trim()
            : skillRef.RecommendationName.Trim();

    private static object ToJsonContract(NyxIdRecommendedSkillRef skillRef) => new
    {
        source = SourceToJson(skillRef.Source),
        skill_id = skillRef.SkillId,
        name = string.IsNullOrWhiteSpace(skillRef.RecommendationName)
            ? skillRef.DisplayName
            : skillRef.RecommendationName,
        version = skillRef.LiteralVersion,
        sha256 = skillRef.ManifestDigest,
        dependencies = Array.Empty<object>(),
    };

    private static NyxIdRecommendedSkillRefPersistenceResult? ClassifyFailure(
        string response,
        NyxIdRecommendedSkillRefPersistenceStatus deniedStatus,
        NyxIdRecommendedSkillRefPersistenceStatus unavailableStatus)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var failureCode = ReadFailureCode(document.RootElement);
            if (failureCode is null)
                return null;

            var status = IsAccessDenied(document.RootElement, failureCode)
                ? deniedStatus
                : unavailableStatus;
            return NyxIdRecommendedSkillRefPersistenceResult.Failed(status, failureCode);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadFailureCode(JsonElement root)
    {
        if (root.TryGetProperty("body", out var body) &&
            body.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(body.GetString()))
        {
            try
            {
                using var bodyDocument = JsonDocument.Parse(body.GetString()!);
                if (bodyDocument.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var bodyCode = ReadFailureCode(bodyDocument.RootElement);
                    if (!string.IsNullOrWhiteSpace(bodyCode))
                        return TryReadStatus(root, out var wrapperStatus) && wrapperStatus >= 400
                            ? AppendFailureDetail($"http_{wrapperStatus}", bodyCode)
                            : bodyCode;
                }
            }
            catch (JsonException)
            {
                var bodyText = body.GetString();
                if (TryReadStatus(root, out var wrapperStatus) && wrapperStatus >= 400)
                    return AppendFailureDetail($"http_{wrapperStatus}", bodyText);

                return AppendFailureDetail(ReadString(root, "error") ?? "upstream_error", bodyText);
            }
        }

        if (TryReadStatus(root, out var status) && status >= 400)
            return BuildFailureCode(root, ReadString(root, "code") ?? ReadString(root, "error") ?? $"http_{status}");

        var code = ReadString(root, "code") ?? ReadString(root, "error");
        if (!string.IsNullOrWhiteSpace(code))
            return BuildFailureCode(root, code);

        if (!string.IsNullOrWhiteSpace(ReadFailureDetail(root, "message")) ||
            !string.IsNullOrWhiteSpace(ReadFailureDetail(root, "detail")) ||
            !string.IsNullOrWhiteSpace(ReadFailureDetail(root, "error_description")) ||
            !string.IsNullOrWhiteSpace(ReadFailureDetail(root, "errors")))
        {
            return BuildFailureCode(root, "upstream_error");
        }

        return root.TryGetProperty("error", out var error) &&
               error.ValueKind is not (JsonValueKind.False or JsonValueKind.Null)
            ? BuildFailureCode(root, "upstream_error")
            : null;
    }

    private static string BuildFailureCode(JsonElement root, string code)
    {
        var detail = ReadFailureDetail(root, "message") ??
                     ReadFailureDetail(root, "detail") ??
                     ReadFailureDetail(root, "error_description") ??
                     ReadFailureDetail(root, "errors");
        return AppendFailureDetail(code, detail);
    }

    private static string? ReadFailureDetail(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Array => string.Join("; ", value.EnumerateArray()
                .Select(ReadFailureDetail)
                .Where(static detail => !string.IsNullOrWhiteSpace(detail))),
            JsonValueKind.Object => value.ToString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static string? ReadFailureDetail(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString();
        if (value.ValueKind != JsonValueKind.Object)
            return value.ToString();

        return ReadString(value, "message") ??
               ReadString(value, "detail") ??
               ReadString(value, "reason") ??
               ReadFailureDetail(value, "loc") ??
               ReadString(value, "msg") ??
               value.ToString();
    }

    private static string AppendFailureDetail(string code, string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail) || string.Equals(code, detail, StringComparison.Ordinal))
            return code;

        var normalized = detail.Trim().Replace('\r', ' ').Replace('\n', ' ');
        return normalized.Length <= 180
            ? $"{code}:{normalized}"
            : $"{code}:{normalized[..180]}";
    }

    private static bool IsAccessDenied(JsonElement root, string failureCode)
    {
        if (TryReadStatus(root, out var status) && status is 401 or 403)
            return true;

        if (root.TryGetProperty("body", out var body) &&
            body.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(body.GetString()))
        {
            try
            {
                using var bodyDocument = JsonDocument.Parse(body.GetString()!);
                return bodyDocument.RootElement.ValueKind == JsonValueKind.Object &&
                       IsAccessDenied(bodyDocument.RootElement, failureCode);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return failureCode.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
               failureCode.Contains("forbidden", StringComparison.OrdinalIgnoreCase) ||
               failureCode.Contains("unauthorized", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadStatus(JsonElement root, out int status)
    {
        status = 0;
        return root.TryGetProperty("status", out var statusElement) &&
               statusElement.TryGetInt32(out status);
    }

    private static string SourceToJson(NyxIdRecommendedSkillSource source) => source switch
    {
        NyxIdRecommendedSkillSource.Ornn => "ornn",
        _ => string.Empty,
    };

    private static bool TryParseRecommendedSkillSource(string? value, out NyxIdRecommendedSkillSource source)
    {
        source = value switch
        {
            "ornn" => NyxIdRecommendedSkillSource.Ornn,
            _ => NyxIdRecommendedSkillSource.Unspecified,
        };
        return source != NyxIdRecommendedSkillSource.Unspecified;
    }

    private static string? ReadString(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }
}
