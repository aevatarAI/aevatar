using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;

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
        long keyExpiresAtUnixMs,
        IReadOnlyList<NyxIdDurableOperationGrantRef>? durableOperationGrants,
        ScheduledAuthorizationPlanMismatchReason authorizationPlanMismatchReason)
    {
        Success = success;
        ApiKeyId = apiKeyId;
        _secret = secret;
        Error = error;
        Detail = detail;
        Hint = hint;
        HttpStatus = httpStatus;
        ServiceSlug = serviceSlug;
        SkillRef = skillRef;
        KeyExpiresAtUnixMs = keyExpiresAtUnixMs;
        _durableOperationGrants = durableOperationGrants?
            .Select(static grant => grant.Clone())
            .ToArray() ?? [];
        AuthorizationPlanMismatchReason = authorizationPlanMismatchReason;
    }

    private readonly ScheduledAgentOpaqueSecret? _secret;
    private readonly IReadOnlyList<NyxIdDurableOperationGrantRef> _durableOperationGrants;

    public bool Success { get; }
    public string? ApiKeyId { get; }
    public string? Error { get; }
    public string? Detail { get; }
    public string? Hint { get; }
    public int? HttpStatus { get; }
    public string? ServiceSlug { get; }
    public string? SkillRef { get; }
    public long KeyExpiresAtUnixMs { get; }
    public IReadOnlyList<NyxIdDurableOperationGrantRef> DurableOperationGrants =>
        _durableOperationGrants.Select(static grant => grant.Clone()).ToArray();
    public ScheduledAuthorizationPlanMismatchReason AuthorizationPlanMismatchReason { get; }

    public static ScheduledAgentApiKeyIssueResult Succeeded(
        string apiKeyId,
        string fullKey,
        long keyExpiresAtUnixMs = 0,
        IReadOnlyList<NyxIdDurableOperationGrantRef>? durableOperationGrants = null) =>
        new(
            true,
            apiKeyId,
            new ScheduledAgentOpaqueSecret(fullKey),
            null,
            null,
            null,
            null,
            null,
            null,
            keyExpiresAtUnixMs,
            durableOperationGrants,
            ScheduledAuthorizationPlanMismatchReason.Unspecified);

    public static ScheduledAgentApiKeyIssueResult Failed(
        string error,
        string? detail = null,
        string? hint = null,
        int? httpStatus = null,
        string? serviceSlug = null,
        string? skillRef = null,
        ScheduledAuthorizationPlanMismatchReason authorizationPlanMismatchReason =
            ScheduledAuthorizationPlanMismatchReason.Unspecified) =>
        new(
            false,
            null,
            null,
            error,
            detail,
            hint,
            httpStatus,
            serviceSlug,
            skillRef,
            0,
            null,
            authorizationPlanMismatchReason);

    public static ScheduledAgentApiKeyIssueResult FailedAfterIssue(
        string apiKeyId,
        string error,
        string? detail = null,
        string? hint = null,
        int? httpStatus = null,
        string? serviceSlug = null,
        string? skillRef = null,
        ScheduledAuthorizationPlanMismatchReason authorizationPlanMismatchReason =
            ScheduledAuthorizationPlanMismatchReason.Unspecified) =>
        new(
            false,
            apiKeyId,
            null,
            error,
            detail,
            hint,
            httpStatus,
            serviceSlug,
            skillRef,
            0,
            null,
            authorizationPlanMismatchReason);

    public Task<Aevatar.Foundation.Abstractions.Credentials.StoreSecretResult> StoreSecretAsync(
        Aevatar.Foundation.Abstractions.Credentials.ISecretVault secretVault,
        Aevatar.Foundation.Abstractions.Credentials.StoreSecretRequest request,
        CancellationToken ct = default)
    {
        if (!Success || _secret is null)
            throw new InvalidOperationException("A successful issued credential is required before storing its secret.");

        return _secret.StoreAsync(secretVault, request, ct);
    }

    public string ToErrorJson() =>
        JsonSerializer.Serialize(new
        {
            error = Error ?? "api_key_issue_failed",
            detail = Detail,
            hint = Hint,
            http_status = HttpStatus,
            service_slug = ServiceSlug,
            skill_ref = SkillRef,
            authorization_plan_mismatch_reason = ScheduledAuthorizationPlanMismatchReasons.ToWireValue(
                AuthorizationPlanMismatchReason),
        }, ErrorJsonOptions);

    public override string ToString() =>
        $"{nameof(ScheduledAgentApiKeyIssueResult)} {{ Success = {Success}, ApiKeyId = {ApiKeyId}, Secret = {(_secret is null ? "null" : "[redacted]")}, Error = {Error}, KeyExpiresAtUnixMs = {KeyExpiresAtUnixMs}, DurableOperationGrantCount = {_durableOperationGrants.Count} }}";
}
