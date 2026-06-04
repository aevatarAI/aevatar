using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.Ornn.Publishing;

public sealed class OrnnSkillPackageFormatValidator
{
    private readonly OrnnOptions _options;
    private readonly NyxIdApiClient _nyxApi;
    private readonly ILogger _logger;

    public OrnnSkillPackageFormatValidator(
        OrnnOptions options,
        NyxIdApiClient nyxApi,
        ILogger<OrnnSkillPackageFormatValidator>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _nyxApi = nyxApi ?? throw new ArgumentNullException(nameof(nyxApi));
        _logger = logger ?? NullLogger<OrnnSkillPackageFormatValidator>.Instance;
    }

    public async Task<OrnnSkillPackageFormatValidationResult> ValidateAsync(
        string accessToken,
        byte[] zipBytes,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            return new OrnnSkillPackageFormatValidationResult(false, [], "missing_access_token");
        if (zipBytes.Length == 0)
            return new OrnnSkillPackageFormatValidationResult(false, [new("non-empty-zip", "ZIP package is empty.")]);

        var response = await _nyxApi.ProxyRequestBinaryAsync(
            token: accessToken,
            slug: _options.NyxIdSlug,
            path: "/api/v1/skill-format/validate",
            method: "POST",
            body: zipBytes,
            contentType: "application/zip",
            extraHeaders: null,
            ct: ct);

        if (TryUnwrapProxyError(response, out var proxyError))
            return new OrnnSkillPackageFormatValidationResult(false, [], proxyError);

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            var data = root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Object
                ? dataProp
                : root;

            var valid = data.TryGetProperty("valid", out var validProp) &&
                        validProp.ValueKind == JsonValueKind.True;
            var violations = ReadViolations(data);
            return valid && violations.Count == 0
                ? OrnnSkillPackageFormatValidationResult.Valid
                : new OrnnSkillPackageFormatValidationResult(false, violations);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Ornn skill format validation returned invalid JSON");
            return new OrnnSkillPackageFormatValidationResult(false, [], "invalid_format_validation_response");
        }
    }

    private static IReadOnlyList<OrnnSkillPackageFormatViolation> ReadViolations(JsonElement data)
    {
        if (!data.TryGetProperty("violations", out var violationsProp) ||
            violationsProp.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var violations = new List<OrnnSkillPackageFormatViolation>();
        foreach (var item in violationsProp.EnumerateArray())
        {
            var rule = item.TryGetProperty("rule", out var ruleProp) && ruleProp.ValueKind == JsonValueKind.String
                ? ruleProp.GetString() ?? "unknown"
                : "unknown";
            var message = item.TryGetProperty("message", out var messageProp) && messageProp.ValueKind == JsonValueKind.String
                ? messageProp.GetString() ?? string.Empty
                : string.Empty;
            violations.Add(new OrnnSkillPackageFormatViolation(rule, message));
        }

        return violations;
    }

    private static bool TryUnwrapProxyError(string response, out string detail)
    {
        detail = string.Empty;
        if (string.IsNullOrWhiteSpace(response))
            return false;

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (!root.TryGetProperty("error", out var errorProp) ||
                errorProp.ValueKind != JsonValueKind.True)
            {
                return false;
            }

            var status = root.TryGetProperty("status", out var statusProp) &&
                         statusProp.ValueKind == JsonValueKind.Number
                ? statusProp.GetInt32()
                : 0;
            detail = $"NyxID proxy returned status={status}.";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
