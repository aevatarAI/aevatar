using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aevatar.GAgents.Scheduled;

public sealed record ScheduledAgentApiKeyIssueResult(
    bool Success,
    string? ApiKeyId,
    [property: JsonIgnore]
    string? FullKey,
    string? Error,
    string? Detail = null,
    string? Hint = null,
    int? HttpStatus = null,
    string? ServiceSlug = null,
    string? SkillRef = null,
    long KeyExpiresAtUnixMs = 0)
{
    private static readonly JsonSerializerOptions ErrorJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static ScheduledAgentApiKeyIssueResult Succeeded(
        string apiKeyId,
        string fullKey,
        long keyExpiresAtUnixMs = 0) =>
        new(true, apiKeyId, fullKey, null, KeyExpiresAtUnixMs: keyExpiresAtUnixMs);

    public static ScheduledAgentApiKeyIssueResult Failed(
        string error,
        string? detail = null,
        string? hint = null,
        int? httpStatus = null,
        string? serviceSlug = null,
        string? skillRef = null) =>
        new(false, null, null, error, detail, hint, httpStatus, serviceSlug, skillRef);

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
        $"{nameof(ScheduledAgentApiKeyIssueResult)} {{ Success = {Success}, ApiKeyId = {ApiKeyId}, FullKey = {(FullKey is null ? "null" : "[redacted]")}, Error = {Error}, KeyExpiresAtUnixMs = {KeyExpiresAtUnixMs} }}";
}
