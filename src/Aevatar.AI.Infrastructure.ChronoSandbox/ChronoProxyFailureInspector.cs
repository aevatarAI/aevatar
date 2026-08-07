using System.Text;
using System.Text.Json;

namespace Aevatar.AI.Infrastructure.ChronoSandbox;

internal sealed record ChronoProxyFailureInspection(
    string? UpstreamCode,
    string? DiagnosticId,
    int BodyBytes,
    string BodyShape);

internal static class ChronoProxyFailureInspector
{
    public static ChronoProxyFailureInspection Inspect(
        string? response,
        IReadOnlySet<string> allowedCodes)
    {
        ArgumentNullException.ThrowIfNull(allowedCodes);
        var bodyBytes = string.IsNullOrEmpty(response)
            ? 0
            : Encoding.UTF8.GetByteCount(response);
        var bodyShape = ClassifyBodyShape(response);
        if (string.IsNullOrWhiteSpace(response))
            return new ChronoProxyFailureInspection(null, null, bodyBytes, bodyShape);

        try
        {
            using var document = JsonDocument.Parse(response);
            var evidence = Inspect(document.RootElement, allowedCodes, allowNestedBody: true);
            return new ChronoProxyFailureInspection(
                evidence.UpstreamCode,
                evidence.DiagnosticId,
                bodyBytes,
                bodyShape);
        }
        catch (JsonException)
        {
            return new ChronoProxyFailureInspection(null, null, bodyBytes, bodyShape);
        }
    }

    public static string ClassifyBodyShape(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return "empty";

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return "json_non_object";
            if (root.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object)
            {
                return error.TryGetProperty("code", out var code) &&
                       code.ValueKind == JsonValueKind.String
                    ? "json_error_code"
                    : "json_error_untyped";
            }

            if (!root.TryGetProperty("body", out var body))
                return error.ValueKind == JsonValueKind.Undefined
                    ? "json_without_error"
                    : "json_error_untyped";

            return body.ValueKind switch
            {
                JsonValueKind.Object => "nested_body_object",
                JsonValueKind.String => "nested_body_string",
                _ => "nested_body_other",
            };
        }
        catch (JsonException)
        {
            return "non_json";
        }
    }

    public static string? SanitizeDiagnosticId(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ||
               normalized.Length > 128 ||
               normalized.Any(static character =>
                   !char.IsAsciiLetterOrDigit(character) &&
                   character is not ('_' or '-' or '.' or ':'))
            ? null
            : normalized;
    }

    private static (string? UpstreamCode, string? DiagnosticId) Inspect(
        JsonElement root,
        IReadOnlySet<string> allowedCodes,
        bool allowNestedBody)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return (null, null);

        var diagnosticId = ReadDiagnosticId(root);
        if (root.TryGetProperty("error", out var error) &&
            error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("code", out var codeElement) &&
            codeElement.ValueKind == JsonValueKind.String &&
            codeElement.GetString() is { } code &&
            allowedCodes.Contains(code))
        {
            return (code, diagnosticId ?? ReadDiagnosticId(error));
        }

        if (!allowNestedBody || !root.TryGetProperty("body", out var body))
            return (null, diagnosticId);

        (string? UpstreamCode, string? DiagnosticId) nested;
        if (body.ValueKind == JsonValueKind.Object)
        {
            nested = Inspect(body, allowedCodes, allowNestedBody: false);
        }
        else if (body.ValueKind == JsonValueKind.String &&
                 !string.IsNullOrWhiteSpace(body.GetString()))
        {
            try
            {
                using var bodyDocument = JsonDocument.Parse(body.GetString()!);
                nested = Inspect(
                    bodyDocument.RootElement,
                    allowedCodes,
                    allowNestedBody: false);
            }
            catch (JsonException)
            {
                nested = (null, null);
            }
        }
        else
        {
            nested = (null, null);
        }

        return (nested.UpstreamCode, nested.DiagnosticId ?? diagnosticId);
    }

    private static string? ReadDiagnosticId(JsonElement root) =>
        root.TryGetProperty("diagnostic_id", out var diagnosticId) &&
        diagnosticId.ValueKind == JsonValueKind.String
            ? SanitizeDiagnosticId(diagnosticId.GetString())
            : null;
}
