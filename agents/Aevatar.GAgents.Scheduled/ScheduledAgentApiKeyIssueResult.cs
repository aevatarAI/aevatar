using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aevatar.GAgents.Scheduled;

public sealed record ScheduledAgentApiKeyIssueResult(
    bool Success,
    string? ApiKeyId,
    string? FullKey,
    string? Error,
    string? Detail = null,
    string? Hint = null,
    int? HttpStatus = null,
    string? ServiceSlug = null,
    string? SkillRef = null)
{
    private static readonly JsonSerializerOptions ErrorJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static ScheduledAgentApiKeyIssueResult Succeeded(string apiKeyId, string fullKey) =>
        new(true, apiKeyId, fullKey, null);

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
}
