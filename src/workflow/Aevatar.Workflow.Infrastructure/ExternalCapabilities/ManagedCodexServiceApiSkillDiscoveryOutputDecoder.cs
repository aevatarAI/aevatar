using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Infrastructure.ExternalCapabilities;

internal sealed partial class ManagedCodexServiceApiSkillDiscoveryOutputDecoder
{
    private const string SchemaVersion = "service_api_skill_discovery.v1";

    public ManagedCodexServiceApiSkillDiscoveryResult Decode(
        string stdout,
        string targetUserServiceId,
        string capabilityFingerprint)
    {
        var target = RequireStaticToken(targetUserServiceId, "target_user_service_id");
        var fingerprint = RequireFingerprint(capabilityFingerprint, "capability_fingerprint");
        using var document = ParseSingleJsonObject(stdout);
        var root = document.RootElement;

        EnsureOnlyProperties(
            root,
            ["schema_version", "target_user_service_id", "capability_fingerprint", "outcome", "reliable_skill", "no_reliable_skill"],
            "managed_service_api_discovery_unknown_field");

        if (ReadRequiredString(root, "schema_version") != SchemaVersion)
        {
            throw Failure(
                "managed_service_api_discovery_schema_unsupported",
                "Managed Codex Service API discovery schema_version is unsupported.");
        }
        if (!string.Equals(ReadRequiredString(root, "target_user_service_id"), target, StringComparison.Ordinal) ||
            !string.Equals(ReadRequiredString(root, "capability_fingerprint"), fingerprint, StringComparison.Ordinal))
        {
            throw Failure(
                "managed_service_api_discovery_correlation_mismatch",
                "Managed Codex Service API discovery correlation fields do not match the authoritative request.");
        }

        var outcome = ReadRequiredString(root, "outcome");
        return outcome switch
        {
            "reliable_skill" => DecodeReliable(root, target),
            "no_reliable_skill" => DecodeNoReliable(root),
            _ => throw Failure(
                "managed_service_api_discovery_outcome_invalid",
                "Managed Codex Service API discovery outcome is unsupported."),
        };
    }

    private static ManagedCodexServiceApiSkillDiscoveryResult DecodeReliable(
        JsonElement root,
        string targetUserServiceId)
    {
        if (!root.TryGetProperty("reliable_skill", out var reliable) ||
            root.TryGetProperty("no_reliable_skill", out _))
        {
            throw Failure(
                "managed_service_api_discovery_branch_invalid",
                "Managed Codex Service API discovery reliable_skill branch is invalid.");
        }
        EnsureObject(reliable, "reliable_skill");
        EnsureOnlyProperties(
            reliable,
            ["canonical_name", "guid", "literal_version", "skill_hash", "publisher_id", "request_shape", "evidence"],
            "managed_service_api_discovery_unknown_field");

        var candidate = new ReliableServiceApiSkillCandidate
        {
            CanonicalName = ReadPattern(
                reliable,
                "canonical_name",
                CanonicalNamePattern(),
                128,
                "managed_service_api_discovery_skill_identity_invalid"),
            Guid = ReadGuid(reliable, "guid"),
            LiteralVersion = ReadPattern(
                reliable,
                "literal_version",
                LiteralVersionPattern(),
                32,
                "managed_service_api_discovery_skill_identity_invalid"),
            SkillHash = ReadHash(reliable, "skill_hash"),
            PublisherId = ReadBoundedString(
                reliable,
                "publisher_id",
                128,
                "managed_service_api_discovery_skill_identity_invalid"),
            RequestShape = new AdmittedNyxIdRequestShape
            {
                Selector = DecodeRequestShape(ReadRequiredProperty(reliable, "request_shape"), targetUserServiceId),
            },
        };
        candidate.Evidence.AddRange(DecodeEvidence(ReadRequiredProperty(reliable, "evidence")));
        return new ManagedCodexServiceApiSkillDiscoveryResult
        {
            ReliableSkill = candidate,
        };
    }

    private static ManagedCodexServiceApiSkillDiscoveryResult DecodeNoReliable(JsonElement root)
    {
        if (!root.TryGetProperty("no_reliable_skill", out var noReliable) ||
            root.TryGetProperty("reliable_skill", out _))
        {
            throw Failure(
                "managed_service_api_discovery_branch_invalid",
                "Managed Codex Service API discovery no_reliable_skill branch is invalid.");
        }
        EnsureObject(noReliable, "no_reliable_skill");
        EnsureOnlyProperties(
            noReliable,
            ["reason"],
            "managed_service_api_discovery_unknown_field");
        return new ManagedCodexServiceApiSkillDiscoveryResult
        {
            NoReliableApiSkill = new NoReliableServiceApiSkill
            {
                Reason = ReadNoReliableReason(ReadRequiredString(noReliable, "reason")),
            },
        };
    }

    private static NyxIdRequestSelector DecodeRequestShape(
        JsonElement shape,
        string targetUserServiceId)
    {
        EnsureObject(shape, "request_shape");
        EnsureOnlyProperties(
            shape,
            ["method", "path_template", "query_parameters", "header_parameters", "body_mode", "body_required", "response_mode", "risk"],
            "managed_service_api_discovery_unknown_field");

        var selector = new NyxIdRequestSelector
        {
            UserServiceId = targetUserServiceId,
            Method = ReadMethod(ReadRequiredString(shape, "method")),
            PathTemplate = ReadBoundedString(
                shape,
                "path_template",
                2048,
                "managed_service_api_discovery_request_shape_invalid"),
            BodyMode = ReadBodyMode(ReadRequiredString(shape, "body_mode")),
            BodyRequired = ReadRequiredBoolean(shape, "body_required"),
            ResponseMode = ReadResponseMode(ReadRequiredString(shape, "response_mode")),
            Risk = ReadRisk(ReadRequiredString(shape, "risk")),
        };
        selector.QueryParameters.AddRange(ReadStringArray(
            ReadRequiredProperty(shape, "query_parameters"),
            64,
            "managed_service_api_discovery_request_shape_invalid"));
        selector.HeaderParameters.AddRange(ReadStringArray(
            ReadRequiredProperty(shape, "header_parameters"),
            3,
            "managed_service_api_discovery_request_shape_invalid"));
        if (!NyxIdRequestSelectorContract.TryNormalize(selector, out var normalized, out var error))
        {
            throw Failure(
                "managed_service_api_discovery_request_shape_rejected",
                error);
        }

        return normalized;
    }

    private static IReadOnlyList<ExactOrnnApiSkillEvidence> DecodeEvidence(JsonElement evidence)
    {
        if (evidence.ValueKind != JsonValueKind.Array ||
            evidence.GetArrayLength() is < 1 or > 16)
        {
            throw Failure(
                "managed_service_api_discovery_evidence_invalid",
                "Managed Codex Service API discovery evidence must contain one to sixteen locators.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ExactOrnnApiSkillEvidence>();
        foreach (var item in evidence.EnumerateArray())
        {
            EnsureObject(item, "evidence");
            EnsureOnlyProperties(
                item,
                ["skill_file_path", "section", "operation_id"],
                "managed_service_api_discovery_unknown_field");
            var locator = new ExactOrnnApiSkillEvidence
            {
                SkillFilePath = ReadPattern(
                    item,
                    "skill_file_path",
                    SkillFilePathPattern(),
                    512,
                    "managed_service_api_discovery_evidence_invalid"),
                Section = ReadBoundedString(
                    item,
                    "section",
                    256,
                    "managed_service_api_discovery_evidence_invalid"),
                OperationId = ReadPattern(
                    item,
                    "operation_id",
                    OperationIdPattern(),
                    256,
                    "managed_service_api_discovery_evidence_invalid"),
            };
            if (!seen.Add($"{locator.SkillFilePath}\n{locator.Section}\n{locator.OperationId}"))
            {
                throw Failure(
                    "managed_service_api_discovery_evidence_invalid",
                    "Managed Codex Service API discovery evidence locators must be unique.");
            }
            result.Add(locator);
        }

        return result;
    }

    private static JsonDocument ParseSingleJsonObject(string stdout)
    {
        var bytes = Encoding.UTF8.GetBytes(stdout ?? string.Empty);
        var reader = new Utf8JsonReader(
            bytes,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        try
        {
            var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw Failure(
                    "managed_service_api_discovery_stdout_not_json_object",
                    "Managed Codex stdout must be exactly one JSON object.");
            }
            if (ContainsNonWhitespace(bytes.AsSpan((int)reader.BytesConsumed)))
            {
                document.Dispose();
                throw Failure(
                    "managed_service_api_discovery_stdout_not_single_json_object",
                    "Managed Codex stdout must not contain trailing content.");
            }

            return document;
        }
        catch (JsonException exception)
        {
            throw Failure(
                StartsWithJsonObject(bytes.AsSpan())
                    ? "managed_service_api_discovery_stdout_not_single_json_object"
                    : "managed_service_api_discovery_stdout_not_json_object",
                "Managed Codex stdout must be exactly one JSON object.",
                exception);
        }
    }

    private static void EnsureOnlyProperties(
        JsonElement element,
        IReadOnlyCollection<string> allowed,
        string code)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw Failure(
                    code,
                    $"Managed Codex Service API discovery field '{property.Name}' is not allowed.");
            }
        }
    }

    private static bool ContainsNonWhitespace(ReadOnlySpan<byte> bytes)
    {
        foreach (var item in bytes)
        {
            if (item is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
                return true;
        }

        return false;
    }

    private static bool StartsWithJsonObject(ReadOnlySpan<byte> bytes)
    {
        foreach (var item in bytes)
        {
            if (item is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                continue;
            return item == (byte)'{';
        }

        return false;
    }

    private static JsonElement ReadRequiredProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            throw Failure(
                "managed_service_api_discovery_field_missing",
                $"Managed Codex Service API discovery field '{name}' is required.");
        }
        return value;
    }

    private static string ReadRequiredString(JsonElement element, string name)
    {
        var value = ReadRequiredProperty(element, name);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Failure(
                "managed_service_api_discovery_field_type_invalid",
                $"Managed Codex Service API discovery field '{name}' must be a string.");
        }
        var text = value.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw Failure(
                "managed_service_api_discovery_field_invalid",
                $"Managed Codex Service API discovery field '{name}' must not be blank.");
        }
        return text;
    }

    private static bool ReadRequiredBoolean(JsonElement element, string name)
    {
        var value = ReadRequiredProperty(element, name);
        if (value.ValueKind is JsonValueKind.True)
            return true;
        if (value.ValueKind is JsonValueKind.False)
            return false;
        throw Failure(
            "managed_service_api_discovery_field_type_invalid",
            $"Managed Codex Service API discovery field '{name}' must be a boolean.");
    }

    private static string ReadBoundedString(
        JsonElement element,
        string name,
        int maxLength,
        string code)
    {
        var value = ReadRequiredString(element, name);
        if (value.Length > maxLength)
            throw Failure(code, $"Managed Codex Service API discovery field '{name}' is too long.");
        return value;
    }

    private static string ReadPattern(
        JsonElement element,
        string name,
        Regex pattern,
        int maxLength,
        string code)
    {
        var value = ReadBoundedString(element, name, maxLength, code);
        if (!pattern.IsMatch(value))
            throw Failure(code, $"Managed Codex Service API discovery field '{name}' has an invalid shape.");
        return value;
    }

    private static string ReadGuid(JsonElement element, string name)
    {
        var value = ReadBoundedString(element, name, 128, "managed_service_api_discovery_skill_identity_invalid");
        if (!System.Guid.TryParseExact(value, "D", out var parsed) ||
            parsed == System.Guid.Empty ||
            !string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal))
        {
            throw Failure(
                "managed_service_api_discovery_skill_identity_invalid",
                "Managed Codex Service API discovery guid is invalid.");
        }
        return value;
    }

    private static string ReadHash(JsonElement element, string name) =>
        ReadPattern(
            element,
            name,
            Sha256Pattern(),
            64,
            "managed_service_api_discovery_skill_identity_invalid");

    private static string RequireStaticToken(string value, string name)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > 256)
            throw Failure("managed_service_api_discovery_correlation_invalid", $"{name} is invalid.");
        return normalized;
    }

    private static string RequireFingerprint(string value, string name)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (!Sha256Pattern().IsMatch(normalized))
            throw Failure("managed_service_api_discovery_correlation_invalid", $"{name} is invalid.");
        return normalized;
    }

    private static IReadOnlyList<string> ReadStringArray(
        JsonElement element,
        int maxCount,
        string code)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > maxCount)
            throw Failure(code, "Managed Codex Service API discovery array field is invalid.");

        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw Failure(code, "Managed Codex Service API discovery array item must be a string.");
            values.Add(item.GetString()?.Trim() ?? string.Empty);
        }
        return values;
    }

    private static NyxIdRequestMethod ReadMethod(string value) => value switch
    {
        "GET" => NyxIdRequestMethod.Get,
        "HEAD" => NyxIdRequestMethod.Head,
        "OPTIONS" => NyxIdRequestMethod.Options,
        "POST" => NyxIdRequestMethod.Post,
        "PUT" => NyxIdRequestMethod.Put,
        "PATCH" => NyxIdRequestMethod.Patch,
        "DELETE" => NyxIdRequestMethod.Delete,
        _ => throw Failure(
            "managed_service_api_discovery_request_shape_invalid",
            "Managed Codex Service API discovery method is unsupported."),
    };

    private static NyxIdRequestBodyMode ReadBodyMode(string value) => value switch
    {
        "none" => NyxIdRequestBodyMode.None,
        "json" => NyxIdRequestBodyMode.Json,
        _ => throw Failure(
            "managed_service_api_discovery_request_shape_invalid",
            "Managed Codex Service API discovery body_mode is unsupported."),
    };

    private static NyxIdRequestResponseMode ReadResponseMode(string value) => value switch
    {
        "text" => NyxIdRequestResponseMode.Text,
        "file_artifact" => NyxIdRequestResponseMode.FileArtifact,
        _ => throw Failure(
            "managed_service_api_discovery_request_shape_invalid",
            "Managed Codex Service API discovery response_mode is unsupported."),
    };

    private static NyxIdOperationRisk ReadRisk(string value) => value switch
    {
        "READ_ONLY" => NyxIdOperationRisk.ReadOnly,
        "WRITE" => NyxIdOperationRisk.Write,
        "DESTRUCTIVE" => NyxIdOperationRisk.Destructive,
        _ => throw Failure(
            "managed_service_api_discovery_request_shape_invalid",
            "Managed Codex Service API discovery risk is unsupported."),
    };

    private static ServiceApiNoReliableSkillReason ReadNoReliableReason(string value) => value switch
    {
        "NO_MATCHING_SKILL" => ServiceApiNoReliableSkillReason.NoMatchingSkill,
        "ALL_CANDIDATES_REJECTED" => ServiceApiNoReliableSkillReason.AllCandidatesRejected,
        "EXACT_SKILL_READ_FAILED" => ServiceApiNoReliableSkillReason.ExactSkillReadFailed,
        "SKILL_IDENTITY_MISMATCH" => ServiceApiNoReliableSkillReason.SkillIdentityMismatch,
        "SKILL_INTEGRITY_MISMATCH" => ServiceApiNoReliableSkillReason.SkillIntegrityMismatch,
        "REQUEST_SHAPE_UNSUPPORTED" => ServiceApiNoReliableSkillReason.RequestShapeUnsupported,
        "REQUEST_SHAPE_ADMISSION_REJECTED" => ServiceApiNoReliableSkillReason.RequestShapeAdmissionRejected,
        _ => throw Failure(
            "managed_service_api_discovery_no_reliable_reason_invalid",
            "Managed Codex Service API discovery no_reliable_skill reason is unsupported."),
    };

    private static void EnsureObject(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Failure(
                "managed_service_api_discovery_field_type_invalid",
                $"Managed Codex Service API discovery field '{name}' must be an object.");
        }
    }

    private static ManagedCodexServiceApiSkillDiscoveryOutputException Failure(
        string code,
        string message,
        Exception? inner = null) =>
        new(code, message, inner);

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalNamePattern();

    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex LiteralVersionPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*(?:/[A-Za-z0-9][A-Za-z0-9._-]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SkillFilePathPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex OperationIdPattern();
}

internal sealed class ManagedCodexServiceApiSkillDiscoveryOutputException : InvalidOperationException
{
    public ManagedCodexServiceApiSkillDiscoveryOutputException(
        string code,
        string message,
        Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    public string Code { get; }
}
