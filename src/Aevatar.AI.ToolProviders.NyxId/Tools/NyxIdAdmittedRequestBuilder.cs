using System.Globalization;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

internal sealed record NyxIdOperationRequest(
    string ServiceId,
    string Slug,
    string Method,
    string Path,
    string? Body,
    Dictionary<string, string>? Headers,
    bool FileArtifact);

internal sealed record NyxIdOperationRequestFailure(string Code, string Message);

internal sealed record NyxIdOperationRequestBuildResult(
    NyxIdOperationRequest? Request,
    NyxIdOperationRequestFailure? Failure)
{
    public bool Succeeded => Request is not null;

    public static NyxIdOperationRequestBuildResult Success(NyxIdOperationRequest request) =>
        new(request, null);

    public static NyxIdOperationRequestBuildResult Failed(string code, string message) =>
        new(null, new NyxIdOperationRequestFailure(code, message));
}

/// <summary>
/// Builds the concrete proxy request from a committed operation proof plus this call's runtime
/// values. The proof owns method, path template, parameter names and schemas; the caller owns only
/// values. Nothing here reads a contract, and any rejection happens before an HTTP request exists.
/// </summary>
internal static class NyxIdAdmittedRequestBuilder
{
    private const string PathParamsSlot = "path_params";
    private const string QuerySlot = "query";
    private const string HeadersSlot = "headers";
    private const string BodySlot = "body";
    private const string ResponseModeSlot = "response_mode";
    private const string TextResponseMode = "text";
    private const string FileArtifactResponseMode = "file_artifact";

    private static readonly HashSet<string> PublishedRuntimeSlots = new(StringComparer.Ordinal)
    {
        PathParamsSlot,
        QuerySlot,
        HeadersSlot,
        BodySlot,
        ResponseModeSlot,
    };

    private static readonly HashSet<string> AuthoredRuntimeSlots = new(StringComparer.Ordinal)
    {
        PathParamsSlot,
        QuerySlot,
        HeadersSlot,
        BodySlot,
    };

    public static NyxIdOperationRequestBuildResult Build(
        AgentToolOperationAdmission admission,
        string argumentsJson)
    {
        ArgumentNullException.ThrowIfNull(admission);

        JsonElement root;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        }
        catch (JsonException)
        {
            return NyxIdOperationRequestBuildResult.Failed(
                "NYXID_OPERATION_ARGUMENTS_INVALID",
                "nyxid_proxy runtime arguments must be a JSON object.");
        }

        using (document)
        {
            root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return NyxIdOperationRequestBuildResult.Failed(
                    "NYXID_OPERATION_ARGUMENTS_INVALID",
                    "nyxid_proxy runtime arguments must be a JSON object.");
            }

            var runtimeSlots = admission.Identity is AgentToolOperationIdentity.AuthoredRequest
                ? AuthoredRuntimeSlots
                : PublishedRuntimeSlots;
            foreach (var property in root.EnumerateObject())
            {
                if (!runtimeSlots.Contains(property.Name))
                {
                    return NyxIdOperationRequestBuildResult.Failed(
                        "NYXID_OPERATION_ARGUMENT_NOT_SUPPORTED",
                        $"nyxid_proxy runtime argument '{property.Name}' is not part of the operation contract.");
                }
            }

            var responseMode = ResolveResponseMode(root, admission, out var responseModeFailure);
            if (responseModeFailure is not null)
                return new NyxIdOperationRequestBuildResult(null, responseModeFailure);
            var method = admission.HttpMethod.Trim().ToUpperInvariant();
            if (responseMode == FileArtifactResponseMode && method != "GET")
            {
                return NyxIdOperationRequestBuildResult.Failed(
                    "NYXID_OPERATION_RESPONSE_MODE_REJECTED",
                    "response_mode=file_artifact only supports an admitted GET operation.");
            }

            var path = BuildPath(admission, root, out var pathFailure);
            if (pathFailure is not null)
                return new NyxIdOperationRequestBuildResult(null, pathFailure);

            var query = BuildQuery(admission, root, out var queryFailure);
            if (queryFailure is not null)
                return new NyxIdOperationRequestBuildResult(null, queryFailure);

            var headers = BuildHeaders(admission, root, out var headerFailure);
            if (headerFailure is not null)
                return new NyxIdOperationRequestBuildResult(null, headerFailure);

            var body = BuildBody(admission, root, out var bodyFailure);
            if (bodyFailure is not null)
                return new NyxIdOperationRequestBuildResult(null, bodyFailure);

            if (responseMode == FileArtifactResponseMode && body is not null)
            {
                return NyxIdOperationRequestBuildResult.Failed(
                    "NYXID_OPERATION_RESPONSE_MODE_REJECTED",
                    "response_mode=file_artifact does not accept a request body.");
            }

            return NyxIdOperationRequestBuildResult.Success(new NyxIdOperationRequest(
                admission.ServiceInstanceId,
                admission.ServiceSlug,
                method,
                path + query,
                body,
                headers,
                responseMode == FileArtifactResponseMode));
        }
    }

    private static string ResolveResponseMode(
        JsonElement root,
        AgentToolOperationAdmission admission,
        out NyxIdOperationRequestFailure? failure)
    {
        failure = null;
        if (admission.Identity is AgentToolOperationIdentity.AuthoredRequest)
        {
            if (admission.ResponsePolicy.TextAllowed == admission.ResponsePolicy.FileArtifactAllowed)
            {
                failure = new NyxIdOperationRequestFailure(
                    "NYXID_OPERATION_RESPONSE_POLICY_INVALID",
                    "The authored request must admit exactly one response mode.");
                return TextResponseMode;
            }

            return admission.ResponsePolicy.FileArtifactAllowed
                ? FileArtifactResponseMode
                : TextResponseMode;
        }

        if (!root.TryGetProperty(ResponseModeSlot, out var value))
            return TextResponseMode;

        if (value.ValueKind != JsonValueKind.String)
        {
            failure = new NyxIdOperationRequestFailure(
                "NYXID_OPERATION_RESPONSE_MODE_INVALID",
                "response_mode must be a string.");
            return TextResponseMode;
        }

        var mode = value.GetString()?.Trim().ToLowerInvariant() ?? TextResponseMode;
        if (mode.Length == 0)
            return TextResponseMode;
        if (mode is not (TextResponseMode or FileArtifactResponseMode))
        {
            failure = new NyxIdOperationRequestFailure(
                "NYXID_OPERATION_RESPONSE_MODE_INVALID",
                "response_mode must be text or file_artifact.");
            return TextResponseMode;
        }

        if (mode == FileArtifactResponseMode && !admission.ResponsePolicy.FileArtifactAllowed)
        {
            failure = new NyxIdOperationRequestFailure(
                "NYXID_OPERATION_RESPONSE_MODE_REJECTED",
                "The admitted operation does not publish a binary response.");
        }

        return mode;
    }

    private static string BuildPath(
        AgentToolOperationAdmission admission,
        JsonElement root,
        out NyxIdOperationRequestFailure? failure)
    {
        failure = null;
        var supplied = ReadObjectSlot(root, PathParamsSlot, out failure);
        if (failure is not null)
            return string.Empty;

        var declared = admission.PathParameters.ToDictionary(
            static parameter => parameter.Name,
            static parameter => parameter,
            StringComparer.Ordinal);
        var extra = supplied.Keys.FirstOrDefault(name => !declared.ContainsKey(name));
        if (extra is not null)
        {
            failure = new NyxIdOperationRequestFailure(
                "NYXID_OPERATION_PATH_PARAMETER_UNKNOWN",
                $"path parameter '{extra}' is not declared by the admitted operation.");
            return string.Empty;
        }

        var builder = new StringBuilder();
        var template = admission.PathTemplate ?? string.Empty;
        var index = 0;
        while (index < template.Length)
        {
            var open = template.IndexOf('{', index);
            if (open < 0)
            {
                builder.Append(template, index, template.Length - index);
                break;
            }

            var close = template.IndexOf('}', open + 1);
            if (close < 0)
            {
                failure = new NyxIdOperationRequestFailure(
                    "NYXID_OPERATION_PATH_TEMPLATE_INVALID",
                    "The admitted path template is malformed.");
                return string.Empty;
            }

            builder.Append(template, index, open - index);
            var name = template[(open + 1)..close];
            if (!supplied.TryGetValue(name, out var value))
            {
                failure = new NyxIdOperationRequestFailure(
                    "NYXID_OPERATION_PATH_PARAMETER_MISSING",
                    $"path parameter '{name}' is required by the admitted operation.");
                return string.Empty;
            }
            if (!declared.TryGetValue(name, out var parameter))
            {
                failure = new NyxIdOperationRequestFailure(
                    "NYXID_OPERATION_PATH_TEMPLATE_INVALID",
                    $"path parameter '{name}' is not declared by the admitted operation.");
                return string.Empty;
            }

            var schemaFailure = ValidateSchema(
                $"path_params.{name}",
                parameter.Schema,
                value,
                "NYXID_OPERATION_PATH_PARAMETER_INVALID");
            if (schemaFailure is not null)
            {
                failure = schemaFailure;
                return string.Empty;
            }

            var segment = EncodePathSegment(
                name,
                value,
                admission.Identity is AgentToolOperationIdentity.AuthoredRequest,
                out failure);
            if (failure is not null)
                return string.Empty;
            builder.Append(segment);
            index = close + 1;
        }

        var path = builder.ToString();
        if (path.Contains('{') || path.Contains('}'))
        {
            failure = new NyxIdOperationRequestFailure(
                "NYXID_OPERATION_PATH_TEMPLATE_INVALID",
                "The concrete path still contains an unresolved placeholder.");
            return string.Empty;
        }

        try
        {
            return "/" + NyxIdApiClient.NormalizeExactProxyPath(path);
        }
        catch (InvalidOperationException)
        {
            failure = new NyxIdOperationRequestFailure(
                "NYXID_OPERATION_PATH_TEMPLATE_INVALID",
                "The admitted path template does not resolve to a safe relative path.");
            return string.Empty;
        }
    }

    private static string EncodePathSegment(
        string name,
        JsonElement value,
        bool rejectPreEncodedOctets,
        out NyxIdOperationRequestFailure? failure)
    {
        failure = null;
        var raw = ToScalarText(value);
        if (raw is null)
        {
            failure = new NyxIdOperationRequestFailure(
                "NYXID_OPERATION_PATH_PARAMETER_INVALID",
                $"path parameter '{name}' must be a scalar value.");
            return string.Empty;
        }

        if (raw.Length == 0)
        {
            failure = new NyxIdOperationRequestFailure(
                "NYXID_OPERATION_PATH_PARAMETER_INVALID",
                $"path parameter '{name}' must not be empty.");
            return string.Empty;
        }

        if (!IsSafePathSegment(raw, rejectPreEncodedOctets))
        {
            failure = new NyxIdOperationRequestFailure(
                "NYXID_OPERATION_PATH_PARAMETER_INVALID",
                $"path parameter '{name}' is not a single safe path segment.");
            return string.Empty;
        }

        return Uri.EscapeDataString(raw);
    }

    private static bool IsSafePathSegment(string raw, bool rejectPreEncodedOctets)
    {
        if (raw is "." or "..")
            return false;
        if (raw.Contains('/') || raw.Contains('\\') || raw.Contains('\0'))
            return false;
        if (raw.Contains('?') || raw.Contains('#'))
            return false;
        if (raw.Contains("${", StringComparison.Ordinal))
            return false;

        if (rejectPreEncodedOctets)
        {
            for (var index = 0; index + 2 < raw.Length; index++)
            {
                if (raw[index] == '%' &&
                    Uri.IsHexDigit(raw[index + 1]) &&
                    Uri.IsHexDigit(raw[index + 2]))
                {
                    return false;
                }
            }
        }

        // Reject pre-encoded traversal delimiters so a caller cannot smuggle a separator through
        // an extra decode hop in a downstream service.
        foreach (var encoded in (ReadOnlySpan<string>)[
                     "%2f", "%5c", "%00", "%2e%2e", "%252f", "%255c", "%252e%252e"])
        {
            if (raw.Contains(encoded, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static string BuildQuery(
        AgentToolOperationAdmission admission,
        JsonElement root,
        out NyxIdOperationRequestFailure? failure)
    {
        failure = null;
        var supplied = ReadObjectSlot(root, QuerySlot, out failure);
        if (failure is not null)
            return string.Empty;

        var declared = admission.QueryParameters.ToDictionary(
            static parameter => parameter.Name,
            static parameter => parameter,
            StringComparer.Ordinal);
        var forbidden = declared.Keys.Concat(supplied.Keys).FirstOrDefault(static name =>
            name.StartsWith("_nyxid_", StringComparison.OrdinalIgnoreCase));
        if (forbidden is not null)
        {
            failure = new NyxIdOperationRequestFailure(
                "NYXID_OPERATION_QUERY_PARAMETER_FORBIDDEN",
                $"query parameter '{forbidden}' is reserved by NyxID.");
            return string.Empty;
        }
        var extra = supplied.Keys.FirstOrDefault(name => !declared.ContainsKey(name));
        if (extra is not null)
        {
            failure = new NyxIdOperationRequestFailure(
                "NYXID_OPERATION_QUERY_PARAMETER_UNKNOWN",
                $"query parameter '{extra}' is not declared by the admitted operation.");
            return string.Empty;
        }

        var missing = declared.Values.FirstOrDefault(parameter =>
            parameter.Required && !supplied.ContainsKey(parameter.Name));
        if (missing is not null)
        {
            failure = new NyxIdOperationRequestFailure(
                "NYXID_OPERATION_QUERY_PARAMETER_MISSING",
                $"query parameter '{missing.Name}' is required by the admitted operation.");
            return string.Empty;
        }

        var pairs = new List<string>();
        foreach (var name in supplied.Keys.OrderBy(static key => key, StringComparer.Ordinal))
        {
            if (admission.Identity is AgentToolOperationIdentity.AuthoredRequest &&
                supplied[name].ValueKind != JsonValueKind.String)
            {
                failure = new NyxIdOperationRequestFailure(
                    "NYXID_OPERATION_QUERY_PARAMETER_INVALID",
                    $"query parameter '{name}' must be a string for an authored request.");
                return string.Empty;
            }

            failure = ValidateSchema(
                $"query.{name}",
                declared[name].Schema,
                supplied[name],
                "NYXID_OPERATION_QUERY_PARAMETER_INVALID");
            if (failure is not null)
                return string.Empty;

            var raw = ToScalarText(supplied[name]);
            if (raw is null)
            {
                failure = new NyxIdOperationRequestFailure(
                    "NYXID_OPERATION_QUERY_PARAMETER_INVALID",
                    $"query parameter '{name}' must be a scalar value.");
                return string.Empty;
            }

            pairs.Add($"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(raw)}");
        }

        return pairs.Count == 0 ? string.Empty : "?" + string.Join("&", pairs);
    }

    private static Dictionary<string, string>? BuildHeaders(
        AgentToolOperationAdmission admission,
        JsonElement root,
        out NyxIdOperationRequestFailure? failure)
    {
        failure = null;
        var supplied = ReadObjectSlot(root, HeadersSlot, out failure);
        if (failure is not null)
            return null;

        var declared = admission.HeaderParameters.ToDictionary(
            static parameter => parameter.Name,
            static parameter => parameter,
            StringComparer.OrdinalIgnoreCase);
        var missing = declared.Values.FirstOrDefault(parameter =>
            parameter.Required && !supplied.ContainsKey(parameter.Name));
        if (missing is not null)
        {
            failure = new NyxIdOperationRequestFailure(
                "NYXID_OPERATION_HEADER_MISSING",
                $"header '{missing.Name}' is required by the admitted operation.");
            return null;
        }
        if (supplied.Count == 0)
            return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in supplied)
        {
            if (!declared.TryGetValue(name, out var parameter))
            {
                failure = new NyxIdOperationRequestFailure(
                    "NYXID_OPERATION_HEADER_NOT_DECLARED",
                    $"header '{name}' is not declared by the admitted operation.");
                return null;
            }

            if (NyxIdProxyHeaderPolicy.IsSensitive(name))
            {
                failure = new NyxIdOperationRequestFailure(
                    "NYXID_OPERATION_HEADER_FORBIDDEN",
                    $"header '{name}' carries credentials and cannot be supplied.");
                return null;
            }

            if (admission.Identity is AgentToolOperationIdentity.AuthoredRequest &&
                value.ValueKind != JsonValueKind.String)
            {
                failure = new NyxIdOperationRequestFailure(
                    "NYXID_OPERATION_HEADER_INVALID",
                    $"header '{name}' must be a string for an authored request.");
                return null;
            }

            failure = ValidateSchema(
                $"headers.{name}",
                parameter.Schema,
                value,
                "NYXID_OPERATION_HEADER_INVALID");
            if (failure is not null)
                return null;

            var raw = ToScalarText(value);
            if (raw is null)
            {
                failure = new NyxIdOperationRequestFailure(
                    "NYXID_OPERATION_HEADER_INVALID",
                    $"header '{name}' must be a scalar value.");
                return null;
            }
            if (!NyxIdOperationHeaderPolicy.IsValidWorkflowHeader(name, raw))
            {
                failure = new NyxIdOperationRequestFailure(
                    "NYXID_OPERATION_HEADER_INVALID",
                    $"header '{name}' does not satisfy the admitted workflow header policy.");
                return null;
            }

            headers[name] = raw;
        }

        return headers;
    }

    private static string? BuildBody(
        AgentToolOperationAdmission admission,
        JsonElement root,
        out NyxIdOperationRequestFailure? failure)
    {
        failure = null;
        var hasBody = root.TryGetProperty(BodySlot, out var body) && body.ValueKind != JsonValueKind.Null;
        if (!hasBody)
        {
            if (admission.RequestBody is { Required: true })
            {
                failure = new NyxIdOperationRequestFailure(
                    "NYXID_OPERATION_BODY_MISSING",
                    "The admitted operation requires a request body.");
            }

            return null;
        }

        if (admission.RequestBody is null)
        {
            failure = new NyxIdOperationRequestFailure(
                "NYXID_OPERATION_BODY_NOT_SUPPORTED",
                "The admitted operation does not accept a request body.");
            return null;
        }

        var schemaFailure = ValidateSchema(
            "body",
            admission.RequestBody.Schema,
            body,
            "NYXID_OPERATION_BODY_INVALID");
        if (schemaFailure is not null)
        {
            failure = schemaFailure;
            return null;
        }

        return body.GetRawText();
    }

    private static NyxIdOperationRequestFailure? ValidateSchema(
        string path,
        AgentToolOperationValueSchema schema,
        JsonElement value,
        string errorCode)
    {
        switch (schema.Kind)
        {
            case AgentToolOperationValueKind.Object:
                return ValidateObjectSchema(path, schema, value, errorCode);
            case AgentToolOperationValueKind.Array:
                if (value.ValueKind != JsonValueKind.Array)
                    return SchemaMismatch(path, "array", errorCode);
                if (schema.Items is null)
                    return null;
                var itemIndex = 0;
                foreach (var item in value.EnumerateArray())
                {
                    var nested = ValidateSchema(
                        $"{path}[{itemIndex++}]",
                        schema.Items,
                        item,
                        errorCode);
                    if (nested is not null)
                        return nested;
                }

                return null;
            case AgentToolOperationValueKind.String:
                if (value.ValueKind != JsonValueKind.String)
                    return SchemaMismatch(path, "string", errorCode);
                return ValidateAllowedValues(
                    path, schema, value.GetString() ?? string.Empty, errorCode);
            case AgentToolOperationValueKind.Integer:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var integer))
                    return SchemaMismatch(path, "integer", errorCode);
                return ValidateAllowedValues(
                    path,
                    schema,
                    integer.ToString(CultureInfo.InvariantCulture),
                    errorCode);
            case AgentToolOperationValueKind.Number:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number))
                    return SchemaMismatch(path, "number", errorCode);
                return ValidateAllowedValues(
                    path, schema, number.ToString("R", CultureInfo.InvariantCulture), errorCode);
            case AgentToolOperationValueKind.Boolean:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return SchemaMismatch(path, "boolean", errorCode);
                return ValidateAllowedValues(
                    path, schema, value.GetBoolean() ? "true" : "false", errorCode);
            default:
                return new NyxIdOperationRequestFailure(
                    errorCode,
                    $"'{path}' has no supported admitted contract type.");
        }
    }

    private static NyxIdOperationRequestFailure? ValidateObjectSchema(
        string path,
        AgentToolOperationValueSchema schema,
        JsonElement value,
        string errorCode)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return SchemaMismatch(path, "object", errorCode);

        foreach (var required in schema.RequiredProperties)
        {
            if (!value.TryGetProperty(required, out _))
            {
                return new NyxIdOperationRequestFailure(
                    errorCode,
                    $"'{path}.{required}' is required by the admitted operation contract.");
            }
        }

        foreach (var property in value.EnumerateObject())
        {
            var propertySchema = schema.FindProperty(property.Name);
            if (propertySchema is null)
            {
                if (schema.AdditionalPropertiesAllowed)
                    continue;
                return new NyxIdOperationRequestFailure(
                    errorCode,
                    $"'{path}.{property.Name}' is not declared by the admitted operation contract.");
            }

            var nested = ValidateSchema(
                $"{path}.{property.Name}",
                propertySchema,
                property.Value,
                errorCode);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private static NyxIdOperationRequestFailure? ValidateAllowedValues(
        string path,
        AgentToolOperationValueSchema schema,
        string value,
        string errorCode) =>
        schema.AllowedValues.Count == 0 || schema.AllowedValues.Contains(value, StringComparer.Ordinal)
            ? null
            : new NyxIdOperationRequestFailure(
                errorCode,
                $"'{path}' is not one of the admitted operation's allowed values.");

    private static NyxIdOperationRequestFailure SchemaMismatch(
        string path,
        string expected,
        string errorCode) =>
        new(
            errorCode,
            $"'{path}' must be {expected} according to the admitted operation contract.");

    private static Dictionary<string, JsonElement> ReadObjectSlot(
        JsonElement root,
        string slot,
        out NyxIdOperationRequestFailure? failure)
    {
        failure = null;
        var values = new Dictionary<string, JsonElement>(
            slot == HeadersSlot ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (!root.TryGetProperty(slot, out var value) || value.ValueKind == JsonValueKind.Null)
            return values;

        if (value.ValueKind != JsonValueKind.Object)
        {
            failure = new NyxIdOperationRequestFailure(
                "NYXID_OPERATION_ARGUMENTS_INVALID",
                $"nyxid_proxy '{slot}' must be a JSON object.");
            return values;
        }

        foreach (var property in value.EnumerateObject())
            values[property.Name] = property.Value.Clone();
        return values;
    }

    private static string? ToScalarText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };
}
