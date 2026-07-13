using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aevatar.GAgents.Scheduled;

public sealed class ScheduledAgentApiKeyIssueResult
{
    private static readonly JsonSerializerOptions ErrorJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private ScheduledAgentApiKeyIssueResult(
        bool success,
        string? apiKeyId,
        ScheduledAgentOpaqueSecret? secret,
        string? error,
        string? detail,
        string? hint,
        int? httpStatus,
        string? serviceSlug,
        string? skillRef,
        long keyExpiresAtUnixMs)
    {
        Success = success;
        ApiKeyId = apiKeyId;
        Secret = secret;
        Error = error;
        Detail = detail;
        Hint = hint;
        HttpStatus = httpStatus;
        ServiceSlug = serviceSlug;
        SkillRef = skillRef;
        KeyExpiresAtUnixMs = keyExpiresAtUnixMs;
    }

    public bool Success { get; }
    public string? ApiKeyId { get; }
    [JsonIgnore]
    public ScheduledAgentOpaqueSecret? Secret { get; }
    public string? Error { get; }
    public string? Detail { get; }
    public string? Hint { get; }
    public int? HttpStatus { get; }
    public string? ServiceSlug { get; }
    public string? SkillRef { get; }
    public long KeyExpiresAtUnixMs { get; }

    public static ScheduledAgentApiKeyIssueResult Succeeded(
        string apiKeyId,
        string fullKey,
        long keyExpiresAtUnixMs = 0) =>
        new(true, apiKeyId, new ScheduledAgentOpaqueSecret(fullKey), null, null, null, null, null, null, keyExpiresAtUnixMs);

    public static ScheduledAgentApiKeyIssueResult Failed(
        string error,
        string? detail = null,
        string? hint = null,
        int? httpStatus = null,
        string? serviceSlug = null,
        string? skillRef = null) =>
        new(false, null, null, error, detail, hint, httpStatus, serviceSlug, skillRef, 0);

    public static ScheduledAgentApiKeyIssueResult FailedAfterIssue(
        string apiKeyId,
        string error,
        string? detail = null,
        string? hint = null,
        int? httpStatus = null,
        string? serviceSlug = null,
        string? skillRef = null) =>
        new(false, apiKeyId, null, error, detail, hint, httpStatus, serviceSlug, skillRef, 0);

    public string ToErrorJson() =>
        JsonSerializer.Serialize(new
        {
            error = Error ?? "api_key_issue_failed",
            detail = Detail,
            hint = Hint,
            http_status = HttpStatus,
            service_slug = ServiceSlug,
            skill_ref = SkillRef,
        }, ErrorJsonOptions);

    public override string ToString() =>
        $"{nameof(ScheduledAgentApiKeyIssueResult)} {{ Success = {Success}, ApiKeyId = {ApiKeyId}, Secret = {(Secret is null ? "null" : "[redacted]")}, Error = {Error}, KeyExpiresAtUnixMs = {KeyExpiresAtUnixMs} }}";
}
