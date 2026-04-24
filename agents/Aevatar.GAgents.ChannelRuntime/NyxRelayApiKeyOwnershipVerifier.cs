using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime;

internal sealed record NyxRelayApiKeyOwnershipVerification(bool Succeeded, string Detail);

internal interface INyxRelayApiKeyOwnershipVerifier
{
    Task<NyxRelayApiKeyOwnershipVerification> VerifyAsync(
        string accessToken,
        string expectedScopeId,
        string nyxAgentApiKeyId,
        CancellationToken ct);
}

internal sealed class NyxRelayApiKeyOwnershipVerifier : INyxRelayApiKeyOwnershipVerifier
{
    private readonly NyxIdApiClient _nyxClient;
    private readonly ILogger<NyxRelayApiKeyOwnershipVerifier> _logger;

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

            var ownerScopeId = await ResolveOwnerScopeIdAsync(token, root, ct);
            if (ownerScopeId is null)
                return Failure("api_key_owner_scope_unresolved");

            if (!string.Equals(ownerScopeId, scopeId, StringComparison.Ordinal))
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

    private async Task<string?> ResolveOwnerScopeIdAsync(string accessToken, JsonElement apiKeyRoot, CancellationToken ct)
    {
        if (apiKeyRoot.TryGetProperty("credential_source", out var source) &&
            source.ValueKind == JsonValueKind.Object)
        {
            var sourceType = ReadOptionalString(source, "type");
            if (string.Equals(sourceType, "org", StringComparison.OrdinalIgnoreCase))
            {
                var role = ReadOptionalString(source, "role");
                if (!string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase))
                    return null;

                return NormalizeOptional(ReadOptionalString(source, "org_id"));
            }
        }

        var currentUserResponse = await _nyxClient.GetCurrentUserAsync(accessToken, ct);
        if (TryReadErrorEnvelope(currentUserResponse, out _))
            return null;

        using var currentUser = JsonDocument.Parse(currentUserResponse);
        return NormalizeOptional(ReadOptionalString(currentUser.RootElement, "id"));
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

    private static NyxRelayApiKeyOwnershipVerification Failure(string detail) =>
        new(false, detail);
}
