using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public sealed record NyxRelayApiKeyOwnershipVerification(bool Succeeded, string Detail);

public interface INyxRelayApiKeyOwnershipVerifier
{
    Task<NyxRelayApiKeyOwnershipVerification> VerifyAsync(
        string accessToken,
        string expectedScopeId,
        string nyxAgentApiKeyId,
        CancellationToken ct);
}

public sealed class NyxRelayApiKeyOwnershipVerifier : INyxRelayApiKeyOwnershipVerifier
{
    private readonly NyxIdApiClient _nyxClient;
    private readonly ILogger<NyxRelayApiKeyOwnershipVerifier> _logger;

    private sealed record OwnerScopeResolution(string? ScopeId, string? FailureDetail);

    public NyxRelayApiKeyOwnershipVerifier(
        NyxIdApiClient nyxClient,
        ILogger<NyxRelayApiKeyOwnershipVerifier>? logger = null)
    {
        _nyxClient = nyxClient ?? throw new ArgumentNullException(nameof(nyxClient));
        _logger = logger ?? NullLogger<NyxRelayApiKeyOwnershipVerifier>.Instance;
    }

    public async Task<NyxRelayApiKeyOwnershipVerification> VerifyAsync(
        string accessToken,
        string expectedScopeId,
        string nyxAgentApiKeyId,
        CancellationToken ct)
    {
        var token = NormalizeOptional(accessToken);
        var scopeId = NormalizeOptional(expectedScopeId);
        var apiKeyId = NormalizeOptional(nyxAgentApiKeyId);
        if (token is null)
            return Failure("missing_access_token");
        if (scopeId is null)
            return Failure("missing_scope_id");
        if (apiKeyId is null)
            return Failure("missing_nyx_agent_api_key_id");

        try
        {
            var response = await _nyxClient.GetApiKeyAsync(token, apiKeyId, ct);
            if (TryReadErrorEnvelope(response, out var errorDetail))
                return Failure($"api_key_lookup_failed {errorDetail}");

            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            var returnedId = ReadOptionalString(root, "id");
            if (!string.Equals(returnedId, apiKeyId, StringComparison.Ordinal))
                return Failure("api_key_id_mismatch");

            var owner = await ResolveOwnerScopeIdAsync(token, root, ct);
            if (owner.FailureDetail is not null)
                return Failure(owner.FailureDetail);
            if (owner.ScopeId is null)
                return Failure("api_key_owner_scope_unresolved");

            if (!string.Equals(owner.ScopeId, scopeId, StringComparison.Ordinal))
                return Failure("api_key_owner_scope_mismatch");

            return new NyxRelayApiKeyOwnershipVerification(true, "verified");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "NyxID api-key ownership verification returned invalid JSON: apiKeyId={ApiKeyId}", apiKeyId);
            return Failure("invalid_json");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NyxID api-key ownership verification failed: apiKeyId={ApiKeyId}", apiKeyId);
            return Failure("verification_exception");
        }
    }

    private async Task<OwnerScopeResolution> ResolveOwnerScopeIdAsync(
        string accessToken,
        JsonElement apiKeyRoot,
        CancellationToken ct)
    {
        if (apiKeyRoot.TryGetProperty("credential_source", out var source) &&
            source.ValueKind == JsonValueKind.Object)
        {
            var sourceType = ReadOptionalString(source, "type");
            if (string.Equals(sourceType, "org", StringComparison.OrdinalIgnoreCase))
            {
                var role = ReadOptionalString(source, "role");
                if (!string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase))
                    return Unresolved($"org_role={role ?? "missing"}");

                var orgId = NormalizeOptional(ReadOptionalString(source, "org_id"));
                return orgId is null
                    ? Unresolved("org_id_missing")
                    : Resolved(orgId);
            }

            if (string.Equals(sourceType, "personal", StringComparison.OrdinalIgnoreCase))
            {
                return await ResolvePersonalOwnerScopeIdAsync(accessToken, apiKeyRoot, ct);
            }

            return Unresolved($"source_type={sourceType ?? "missing"}");
        }

        return Unresolved("credential_source_missing");
    }

    private async Task<OwnerScopeResolution> ResolvePersonalOwnerScopeIdAsync(
        string accessToken,
        JsonElement apiKeyRoot,
        CancellationToken ct)
    {
        // Personal key ownership relies on NyxID's read-owner gate; verify key.user_id too when the response exposes it.
        var currentUserResponse = await _nyxClient.GetCurrentUserAsync(accessToken, ct);
        if (TryReadErrorEnvelope(currentUserResponse, out var errorDetail))
            return Unresolved($"current_user_lookup_failed {errorDetail}");

        using var currentUser = JsonDocument.Parse(currentUserResponse);
        var currentUserId = NormalizeOptional(ReadOptionalString(currentUser.RootElement, "id"));
        if (currentUserId is null)
            return Unresolved("current_user_id_missing");

        var keyUserId = NormalizeOptional(ReadOptionalString(apiKeyRoot, "user_id"));
        if (keyUserId is not null &&
            !string.Equals(keyUserId, currentUserId, StringComparison.Ordinal))
        {
            return new OwnerScopeResolution(null, "api_key_owner_scope_mismatch key_user_id_mismatch");
        }

        return Resolved(currentUserId);
    }

    private static bool TryReadErrorEnvelope(string response, out string detail)
    {
        detail = string.Empty;
        if (string.IsNullOrWhiteSpace(response))
        {
            detail = "empty_response";
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (!root.TryGetProperty("error", out var error) ||
                error.ValueKind != JsonValueKind.True)
            {
                return false;
            }

            var status = root.TryGetProperty("status", out var statusProp) &&
                         statusProp.ValueKind == JsonValueKind.Number
                ? statusProp.GetInt32().ToString()
                : "unknown";
            var body = ReadOptionalString(root, "body");
            var message = ReadOptionalString(root, "message");
            detail = $"nyx_status={status}" +
                     (body is null ? string.Empty : $" body={body}") +
                     (message is null ? string.Empty : $" message={message}");
            return true;
        }
        catch (JsonException)
        {
            detail = "invalid_error_envelope";
            return true;
        }
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? NormalizeOptional(value.GetString())
            : null;

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static OwnerScopeResolution Resolved(string scopeId) =>
        new(scopeId, null);

    private static OwnerScopeResolution Unresolved(string detail) =>
        new(null, $"api_key_owner_scope_unresolved {detail}");

    private static NyxRelayApiKeyOwnershipVerification Failure(string detail) =>
        new(false, detail);
}
