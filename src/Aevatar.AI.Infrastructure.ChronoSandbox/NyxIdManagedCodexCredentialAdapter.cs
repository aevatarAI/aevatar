using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Application.CodexExecution;
using Aevatar.AI.ToolProviders.NyxId;

namespace Aevatar.AI.Infrastructure.ChronoSandbox;

internal sealed class NyxIdManagedCodexCredentialAdapter(
    INyxIdApiClientFactory clientFactory) : IManagedCodexNyxIdCredentialPort
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly INyxIdApiClientFactory _clientFactory =
        clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));

    public async Task<string> GetCurrentUserIdAsync(
        string bearerToken,
        CancellationToken ct = default)
    {
        var response = await Client.GetCurrentUserAsync(bearerToken, ct).ConfigureAwait(false);
        using var document = ParseObject(response, "nyxid_identity_invalid");
        var userId = ReadString(document.RootElement, "id")?.Trim();
        return string.IsNullOrWhiteSpace(userId)
            ? throw Failure("nyxid_identity_invalid", "NyxID did not return a stable current-user identity.")
            : userId;
    }

    public async Task<IReadOnlyList<ManagedCodexNyxIdService>> ListUserServicesAsync(
        string bearerToken,
        CancellationToken ct = default)
    {
        var response = await Client.ListUserServicesAsync(bearerToken, ct).ConfigureAwait(false);
        using var document = ParseObject(response, "managed_service_catalog_invalid");
        if (!document.RootElement.TryGetProperty("services", out var services) ||
            services.ValueKind != JsonValueKind.Array)
        {
            throw Failure(
                "managed_service_catalog_invalid",
                "NyxID returned an invalid user-service catalog.");
        }

        return services.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.Object)
            .Select(ParseService)
            .ToArray();
    }

    public async Task<IReadOnlyList<ManagedCodexNyxIdApiKey>> ListApiKeysAsync(
        string bearerToken,
        CancellationToken ct = default)
    {
        var response = await Client.ListApiKeysAsync(bearerToken, ct).ConfigureAwait(false);
        using var document = ParseObject(response, "managed_api_key_list_invalid");
        if (!document.RootElement.TryGetProperty("keys", out var keys) ||
            keys.ValueKind != JsonValueKind.Array)
        {
            throw Failure(
                "managed_api_key_list_invalid",
                "NyxID returned an invalid API-key list.");
        }

        return keys.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.Object)
            .Select(static item => ParseKey(item, defaultIsActive: false))
            .ToArray();
    }

    public async Task<ManagedCodexNyxIdIssuedApiKey> CreateApiKeyAsync(
        string bearerToken,
        ManagedCodexNyxIdApiKeyIssueRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await Client.CreateApiKeyAsync(
            bearerToken,
            JsonSerializer.Serialize(new
            {
                name = request.Name,
                description = request.Description,
                scopes = request.Scopes,
                platform = request.Platform,
                allow_all_services = request.AllowAllServices,
                allowed_service_ids = request.AllowedServiceIds,
                allow_all_nodes = request.AllowAllNodes,
                allowed_node_ids = request.AllowedNodeIds,
                expires_at = request.ExpiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            }, RequestJsonOptions),
            ct).ConfigureAwait(false);
        return ParseIssued(response, request.Platform);
    }

    public async Task UpdateApiKeyPolicyAsync(
        string bearerToken,
        string apiKeyId,
        ManagedCodexNyxIdApiKeyPolicyUpdateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await Client.UpdateApiKeyAsync(
            bearerToken,
            apiKeyId,
            JsonSerializer.Serialize(new
            {
                scopes = request.Scopes,
                platform = request.Platform,
                allow_all_services = request.AllowAllServices,
                allowed_service_ids = request.AllowedServiceIds,
                allow_all_nodes = request.AllowAllNodes,
                allowed_node_ids = request.AllowedNodeIds,
            }, RequestJsonOptions),
            ct).ConfigureAwait(false);
        using var _ = ParseObject(response, "managed_api_key_update_invalid");
    }

    public async Task<ManagedCodexNyxIdIssuedApiKey> RotateApiKeyAsync(
        string bearerToken,
        string apiKeyId,
        CancellationToken ct = default)
    {
        var response = await Client.RotateApiKeyAsync(bearerToken, apiKeyId, ct)
            .ConfigureAwait(false);
        return ParseIssued(response, "codex");
    }

    public async Task<bool> RevokeApiKeyAsync(
        string bearerToken,
        string apiKeyId,
        CancellationToken ct = default)
    {
        try
        {
            var response = await Client.DeleteApiKeyAsync(bearerToken, apiKeyId, ct)
                .ConfigureAwait(false);
            return !TryReadErrorStatus(response, out var status) || status is 404 or 410;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private NyxIdApiClient Client => _clientFactory.CreateClient();

    private static ManagedCodexNyxIdService ParseService(JsonElement service)
    {
        var source = service.TryGetProperty("credential_source", out var sourceElement) &&
                     sourceElement.ValueKind == JsonValueKind.Object
            ? sourceElement
            : default;
        return new ManagedCodexNyxIdService(
            ReadString(service, "id") ?? string.Empty,
            ReadString(service, "slug") ?? string.Empty,
            ReadBoolean(service, "is_active") == true,
            source.ValueKind == JsonValueKind.Object
                ? ReadString(source, "type") ?? string.Empty
                : string.Empty,
            source.ValueKind == JsonValueKind.Object ? ReadBoolean(source, "allowed") : null,
            ReadBoolean(service, "forward_access_token"),
            ReadBoolean(service, "inject_delegation_token"),
            ReadString(service, "delegation_token_scope"));
    }

    private static ManagedCodexNyxIdIssuedApiKey ParseIssued(
        string response,
        string fallbackPlatform)
    {
        using var document = ParseObject(response, "managed_api_key_issue_invalid");
        var root = document.RootElement;
        var rawKey = ReadString(root, "full_key")?.Trim();
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            throw Failure(
                "managed_api_key_issue_invalid",
                "NyxID returned an invalid managed Codex key.");
        }

        var key = ParseKey(root, defaultIsActive: true, fallbackPlatform);
        return new ManagedCodexNyxIdIssuedApiKey(
            key,
            new ManagedCodexOpaqueSecret(rawKey));
    }

    private static ManagedCodexNyxIdApiKey ParseKey(
        JsonElement root,
        bool defaultIsActive,
        string fallbackPlatform = "") =>
        new(
            ReadString(root, "id")?.Trim() ?? string.Empty,
            ReadString(root, "name")?.Trim() ?? string.Empty,
            ReadString(root, "scopes")?.Trim() ?? string.Empty,
            ReadString(root, "platform")?.Trim() ?? fallbackPlatform,
            ReadBoolean(root, "is_active") ?? defaultIsActive,
            ReadBoolean(root, "allow_all_services") ?? true,
            ReadStringArray(root, "allowed_service_ids"),
            ReadBoolean(root, "allow_all_nodes") ?? true,
            ReadStringArray(root, "allowed_node_ids"),
            ReadDateTimeOffset(root, "expires_at"));

    private static IReadOnlyList<string> ReadStringArray(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];
        return value.EnumerateArray()
            .Select(static item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item!.Trim())
            .ToArray();
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(
            value.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;

    private static JsonDocument ParseObject(string? response, string code)
    {
        try
        {
            var document = JsonDocument.Parse(response ?? string.Empty);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                !(document.RootElement.TryGetProperty("error", out var error) &&
                  error.ValueKind == JsonValueKind.True))
            {
                return document;
            }
            document.Dispose();
        }
        catch (JsonException)
        {
        }
        throw Failure(code, "NyxID returned an invalid managed Codex response.");
    }

    private static bool TryReadErrorStatus(string? response, out int status)
    {
        status = 0;
        try
        {
            using var document = JsonDocument.Parse(response ?? string.Empty);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("error", out var error) ||
                error.ValueKind != JsonValueKind.True)
            {
                return false;
            }
            if (root.TryGetProperty("status", out var statusValue) &&
                statusValue.TryGetInt32(out var parsed))
            {
                status = parsed;
            }
            return true;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static string? ReadString(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? ReadBoolean(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static ManagedCodexCredentialLifecycleException Failure(
        string code,
        string message) =>
        new(code, message);
}
