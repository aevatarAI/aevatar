using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>HTTP client for calling NyxID REST API endpoints.</summary>
public sealed class NyxIdApiClient
{
    private readonly HttpClient _http;
    private readonly NyxIdToolOptions _options;
    private readonly ILogger _logger;

    public NyxIdApiClient(
        NyxIdToolOptions options,
        HttpClient? httpClient = null,
        ILogger<NyxIdApiClient>? logger = null)
    {
        _options = options;
        _http = httpClient ?? new HttpClient();
        _logger = logger ?? NullLogger<NyxIdApiClient>.Instance;
    }

    // ─── Account ───

    public Task<string> GetCurrentUserAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/users/me", ct);

    // ─── Catalog ───

    public Task<string> ListCatalogAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/catalog", ct);

    public Task<string> GetCatalogEntryAsync(string token, string slug, CancellationToken ct) =>
        GetAsync(token, $"/api/v1/catalog/{Uri.EscapeDataString(slug)}", ct);

    // ─── AI Services (unified /keys) ───

    public Task<string> ListServicesAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/keys", ct);

    public Task<string> GetServiceAsync(string token, string id, CancellationToken ct) =>
        GetAsync(token, $"/api/v1/keys/{Uri.EscapeDataString(id)}", ct);

    public Task<string> DeleteServiceAsync(string token, string id, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/keys/{Uri.EscapeDataString(id)}", ct);

    public Task<string> CreateServiceAsync(string token, string body, CancellationToken ct) =>
        PostAsync(token, "/api/v1/keys", body, ct);

    // ─── Proxy ───

    public async Task<string> ProxyRequestAsync(
        string token,
        string slug,
        string path,
        string method,
        string? body,
        Dictionary<string, string>? extraHeaders,
        CancellationToken ct)
    {
        var baseUrl = GetBaseUrl();
        var normalizedPath = path.TrimStart('/');
        var url = $"{baseUrl}/api/v1/proxy/s/{Uri.EscapeDataString(slug)}/{normalizedPath}";

        var httpMethod = new HttpMethod(method.ToUpperInvariant());
        using var request = new HttpRequestMessage(httpMethod, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (extraHeaders != null)
        {
            foreach (var (key, value) in extraHeaders)
                request.Headers.TryAddWithoutValidation(key, value);
        }

        if (!string.IsNullOrEmpty(body) && httpMethod != HttpMethod.Get && httpMethod != HttpMethod.Head)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await SendAsync(request, ct);
    }

    // ─── API Keys ───

    public Task<string> ListApiKeysAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/api-keys", ct);

    public Task<string> CreateApiKeyAsync(string token, string requestBody, CancellationToken ct) =>
        PostAsync(token, "/api/v1/api-keys", requestBody, ct);

    // ─── Nodes ───

    public Task<string> ListNodesAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/nodes", ct);

    public Task<string> GetNodeAsync(string token, string id, CancellationToken ct) =>
        GetAsync(token, $"/api/v1/nodes/{Uri.EscapeDataString(id)}", ct);

    public Task<string> DeleteNodeAsync(string token, string id, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/nodes/{Uri.EscapeDataString(id)}", ct);

    // ─── Approvals ───

    public Task<string> ListApprovalsAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/approvals/requests", ct);

    public Task<string> DecideApprovalAsync(string token, string id, string decisionBody, CancellationToken ct) =>
        PostAsync(token, $"/api/v1/approvals/requests/{Uri.EscapeDataString(id)}/decide", decisionBody, ct);

    public Task<string> ListApprovalServiceConfigsAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/approvals/service-configs", ct);

    // ─── Profile ───

    public Task<string> UpdateProfileAsync(string token, string body, CancellationToken ct) =>
        PutAsync(token, "/api/v1/users/me", body, ct);

    public Task<string> DeleteAccountAsync(string token, CancellationToken ct) =>
        DeleteAsync(token, "/api/v1/users/me", ct);

    public Task<string> ListConsentsAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/users/me/consents", ct);

    public Task<string> RevokeConsentAsync(string token, string clientId, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/users/me/consents/{Uri.EscapeDataString(clientId)}", ct);

    // ─── MFA ───

    public Task<string> SetupMfaAsync(string token, CancellationToken ct) =>
        PostAsync(token, "/api/v1/mfa/setup", "{}", ct);

    public Task<string> VerifyMfaSetupAsync(string token, string body, CancellationToken ct) =>
        PostAsync(token, "/api/v1/mfa/verify-setup", body, ct);

    // ─── Sessions ───

    public Task<string> ListSessionsAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/sessions", ct);

    // ─── Services (additions) ───

    public Task<string> UpdateServiceAsync(string token, string id, string body, CancellationToken ct) =>
        PutAsync(token, $"/api/v1/keys/{Uri.EscapeDataString(id)}", body, ct);

    // ─── User Services (for route command) ───

    public Task<string> UpdateUserServiceAsync(string token, string id, string body, CancellationToken ct) =>
        PutAsync(token, $"/api/v1/user-services/{Uri.EscapeDataString(id)}", body, ct);

    // ─── Proxy (additions) ───

    public Task<string> DiscoverProxyServicesAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/proxy/services", ct);

    // ─── API Keys (additions) ───

    public Task<string> GetApiKeyAsync(string token, string id, CancellationToken ct) =>
        GetAsync(token, $"/api/v1/api-keys/{Uri.EscapeDataString(id)}", ct);

    public Task<string> RotateApiKeyAsync(string token, string id, CancellationToken ct) =>
        PostAsync(token, $"/api/v1/api-keys/{Uri.EscapeDataString(id)}/rotate", "{}", ct);

    public Task<string> DeleteApiKeyAsync(string token, string id, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/api-keys/{Uri.EscapeDataString(id)}", ct);

    public Task<string> UpdateApiKeyAsync(string token, string id, string body, CancellationToken ct) =>
        PutAsync(token, $"/api/v1/api-keys/{Uri.EscapeDataString(id)}", body, ct);

    // ─── Approvals (additions) ───

    public Task<string> CreateApprovalRequestAsync(string token, string body, CancellationToken ct) =>
        PostAsync(token, "/api/v1/approvals/requests", body, ct);

    public Task<string> GetApprovalAsync(string token, string id, CancellationToken ct) =>
        GetAsync(token, $"/api/v1/approvals/requests/{Uri.EscapeDataString(id)}", ct);

    public Task<string> ListApprovalGrantsAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/approvals/grants", ct);

    public Task<string> RevokeApprovalGrantAsync(string token, string id, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/approvals/grants/{Uri.EscapeDataString(id)}", ct);

    public Task<string> SetApprovalConfigAsync(string token, string id, string body, CancellationToken ct) =>
        PutAsync(token, $"/api/v1/approvals/service-configs/{Uri.EscapeDataString(id)}", body, ct);

    // ─── Global Approval ───

    /// <summary>Enable or disable global approval protection via notification settings.</summary>
    public Task<string> SetGlobalApprovalAsync(string token, bool enabled, CancellationToken ct) =>
        PutAsync(token, "/api/v1/notifications/settings",
            enabled ? """{"approval_required":true}""" : """{"approval_required":false}""", ct);

    // ─── Endpoints ───

    public Task<string> ListEndpointsAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/endpoints", ct);

    public Task<string> UpdateEndpointAsync(string token, string id, string body, CancellationToken ct) =>
        PutAsync(token, $"/api/v1/endpoints/{Uri.EscapeDataString(id)}", body, ct);

    public Task<string> DeleteEndpointAsync(string token, string id, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/endpoints/{Uri.EscapeDataString(id)}", ct);

    // ─── External Keys ───

    public Task<string> ListExternalKeysAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/api-keys/external", ct);

    public Task<string> UpdateExternalKeyAsync(string token, string id, string body, CancellationToken ct) =>
        PutAsync(token, $"/api/v1/api-keys/external/{Uri.EscapeDataString(id)}", body, ct);

    public Task<string> DeleteExternalKeyAsync(string token, string id, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/api-keys/external/{Uri.EscapeDataString(id)}", ct);

    // ─── Notifications ───

    public Task<string> GetNotificationSettingsAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/notifications/settings", ct);

    public Task<string> UpdateNotificationSettingsAsync(string token, string body, CancellationToken ct) =>
        PutAsync(token, "/api/v1/notifications/settings", body, ct);

    public Task<string> TelegramLinkAsync(string token, CancellationToken ct) =>
        PostAsync(token, "/api/v1/notifications/telegram/link", "{}", ct);

    public Task<string> TelegramDisconnectAsync(string token, CancellationToken ct) =>
        DeleteAsync(token, "/api/v1/notifications/telegram", ct);

    // ─── Nodes (additions) ───

    public Task<string> GenerateNodeRegistrationTokenAsync(string token, string body, CancellationToken ct) =>
        PostAsync(token, "/api/v1/nodes/register-token", body, ct);

    public Task<string> RotateNodeTokenAsync(string token, string id, CancellationToken ct) =>
        PostAsync(token, $"/api/v1/nodes/{Uri.EscapeDataString(id)}/rotate-token", "{}", ct);

    // ─── LLM ───

    public Task<string> GetLlmStatusAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/llm/status", ct);

    // ─── Providers ───

    public Task<string> ListProviderTokensAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/providers/my-tokens", ct);

    public Task<string> InitiateOAuthConnectAsync(string token, string providerId, CancellationToken ct) =>
        GetAsync(token, $"/api/v1/providers/{Uri.EscapeDataString(providerId)}/connect/oauth", ct);

    public Task<string> InitiateDeviceCodeAsync(string token, string providerId, CancellationToken ct) =>
        PostAsync(token, $"/api/v1/providers/{Uri.EscapeDataString(providerId)}/connect/device-code/initiate", "{}", ct);

    public Task<string> PollDeviceCodeAsync(string token, string providerId, string state, CancellationToken ct) =>
        PostAsync(token, $"/api/v1/providers/{Uri.EscapeDataString(providerId)}/connect/device-code/poll",
            System.Text.Json.JsonSerializer.Serialize(new { state }), ct);

    public Task<string> DisconnectProviderAsync(string token, string providerId, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/providers/{Uri.EscapeDataString(providerId)}/disconnect", ct);

    // ─── User Provider Credentials ───

    public Task<string> GetUserCredentialsAsync(string token, string providerId, CancellationToken ct) =>
        GetAsync(token, $"/api/v1/providers/{Uri.EscapeDataString(providerId)}/credentials", ct);

    public Task<string> SetUserCredentialsAsync(string token, string providerId, string body, CancellationToken ct) =>
        PutAsync(token, $"/api/v1/providers/{Uri.EscapeDataString(providerId)}/credentials", body, ct);

    public Task<string> DeleteUserCredentialsAsync(string token, string providerId, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/providers/{Uri.EscapeDataString(providerId)}/credentials", ct);

    // ─── Channel Bot Relay ───

    public Task<string> ListChannelBotsAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/channel-bots", ct);

    public Task<string> GetChannelBotAsync(string token, string id, CancellationToken ct) =>
        GetAsync(token, $"/api/v1/channel-bots/{Uri.EscapeDataString(id)}", ct);

    public Task<string> RegisterChannelBotAsync(string token, string body, CancellationToken ct) =>
        PostAsync(token, "/api/v1/channel-bots", body, ct);

    public Task<string> DeleteChannelBotAsync(string token, string id, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/channel-bots/{Uri.EscapeDataString(id)}", ct);

    public Task<string> VerifyChannelBotAsync(string token, string id, CancellationToken ct) =>
        PostAsync(token, $"/api/v1/channel-bots/{Uri.EscapeDataString(id)}/verify", "{}", ct);

    public Task<string> ListConversationRoutesAsync(string token, string? botId, CancellationToken ct) =>
        GetAsync(token, string.IsNullOrWhiteSpace(botId)
            ? "/api/v1/channel-conversations"
            : $"/api/v1/channel-conversations?bot_id={Uri.EscapeDataString(botId)}", ct);

    public Task<string> GetConversationRouteAsync(string token, string id, CancellationToken ct) =>
        GetAsync(token, $"/api/v1/channel-conversations/{Uri.EscapeDataString(id)}", ct);

    public Task<string> CreateConversationRouteAsync(string token, string body, CancellationToken ct) =>
        PostAsync(token, "/api/v1/channel-conversations", body, ct);

    public Task<string> UpdateConversationRouteAsync(string token, string id, string body, CancellationToken ct) =>
        PutAsync(token, $"/api/v1/channel-conversations/{Uri.EscapeDataString(id)}", body, ct);

    public Task<string> DeleteConversationRouteAsync(string token, string id, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/channel-conversations/{Uri.EscapeDataString(id)}", ct);

    // ─── Organizations ───

    public Task<string> ListOrgsAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/orgs", ct);

    public Task<string> GetOrgAsync(string token, string id, CancellationToken ct) =>
        GetAsync(token, $"/api/v1/orgs/{Uri.EscapeDataString(id)}", ct);

    public Task<string> CreateOrgAsync(string token, string body, CancellationToken ct) =>
        PostAsync(token, "/api/v1/orgs", body, ct);

    public Task<string> UpdateOrgAsync(string token, string id, string body, CancellationToken ct) =>
        PatchAsync(token, $"/api/v1/orgs/{Uri.EscapeDataString(id)}", body, ct);

    public Task<string> DeleteOrgAsync(string token, string id, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/orgs/{Uri.EscapeDataString(id)}", ct);

    public Task<string> JoinOrgAsync(string token, string nonce, CancellationToken ct) =>
        PostAsync(token, $"/api/v1/orgs/join/{Uri.EscapeDataString(nonce)}", "{}", ct);

    public Task<string> SetPrimaryOrgAsync(string token, string body, CancellationToken ct) =>
        PatchAsync(token, "/api/v1/users/me/primary-org", body, ct);

    // ─── Org Members ───

    public Task<string> ListOrgMembersAsync(string token, string orgId, CancellationToken ct) =>
        GetAsync(token, $"/api/v1/orgs/{Uri.EscapeDataString(orgId)}/members", ct);

    public Task<string> AddOrgMemberAsync(string token, string orgId, string body, CancellationToken ct) =>
        PostAsync(token, $"/api/v1/orgs/{Uri.EscapeDataString(orgId)}/members", body, ct);

    public Task<string> UpdateOrgMemberAsync(string token, string orgId, string memberId, string body, CancellationToken ct) =>
        PatchAsync(token, $"/api/v1/orgs/{Uri.EscapeDataString(orgId)}/members/{Uri.EscapeDataString(memberId)}", body, ct);

    public Task<string> RemoveOrgMemberAsync(string token, string orgId, string memberId, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/orgs/{Uri.EscapeDataString(orgId)}/members/{Uri.EscapeDataString(memberId)}", ct);

    // ─── Org Invites ───

    public Task<string> ListOrgInvitesAsync(string token, string orgId, CancellationToken ct) =>
        GetAsync(token, $"/api/v1/orgs/{Uri.EscapeDataString(orgId)}/invites", ct);

    public Task<string> CreateOrgInviteAsync(string token, string orgId, string body, CancellationToken ct) =>
        PostAsync(token, $"/api/v1/orgs/{Uri.EscapeDataString(orgId)}/invites", body, ct);

    public Task<string> CancelOrgInviteAsync(string token, string orgId, string inviteId, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/orgs/{Uri.EscapeDataString(orgId)}/invites/{Uri.EscapeDataString(inviteId)}", ct);

    // ─── Channel Events ───

    public Task<string> PushChannelEventAsync(string token, string conversationId, string body, CancellationToken ct) =>
        PostAsync(token, $"/api/v1/channel-events/{Uri.EscapeDataString(conversationId)}", body, ct);

    // ─── Admin Invite Codes ───

    public Task<string> ListInviteCodesAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/admin/invite-codes", ct);

    public Task<string> CreateInviteCodeAsync(string token, string body, CancellationToken ct) =>
        PostAsync(token, "/api/v1/admin/invite-codes", body, ct);

    public Task<string> DeactivateInviteCodeAsync(string token, string id, CancellationToken ct) =>
        DeleteAsync(token, $"/api/v1/admin/invite-codes/{Uri.EscapeDataString(id)}", ct);

    // ─── API Key Bindings ───

    public Task<string> BindApiKeyAsync(string token, string keyId, string body, CancellationToken ct) =>
        PostAsync(token, $"/api/v1/api-keys/{Uri.EscapeDataString(keyId)}/bindings", body, ct);

    // ─── HTTP helpers ───

    private string GetBaseUrl() =>
        _options.BaseUrl?.TrimEnd('/') ?? throw new InvalidOperationException("NyxID base URL is not configured.");

    internal async Task<string> GetAsync(string token, string path, CancellationToken ct)
    {
        var url = $"{GetBaseUrl()}{path}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await SendAsync(request, ct);
    }

    private async Task<string> PostAsync(string token, string path, string body, CancellationToken ct)
    {
        var url = $"{GetBaseUrl()}{path}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await SendAsync(request, ct);
    }

    private async Task<string> PatchAsync(string token, string path, string body, CancellationToken ct)
    {
        var url = $"{GetBaseUrl()}{path}";
        using var request = new HttpRequestMessage(HttpMethod.Patch, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await SendAsync(request, ct);
    }

    private async Task<string> PutAsync(string token, string path, string body, CancellationToken ct)
    {
        var url = $"{GetBaseUrl()}{path}";
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await SendAsync(request, ct);
    }

    private async Task<string> DeleteAsync(string token, string path, CancellationToken ct)
    {
        var url = $"{GetBaseUrl()}{path}";
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await SendAsync(request, ct);
    }

    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var response = await _http.SendAsync(request, ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "NyxID API request failed: {Method} {Url} -> {Status}",
                    request.Method, request.RequestUri, (int)response.StatusCode);
                return $"{{\"error\": true, \"status\": {(int)response.StatusCode}, \"body\": {EscapeJsonString(content)}}}";
            }

            return content;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NyxID API request exception: {Method} {Url}", request.Method, request.RequestUri);
            return $"{{\"error\": true, \"message\": {EscapeJsonString(ex.Message)}}}";
        }
    }

    private static string EscapeJsonString(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);
}
