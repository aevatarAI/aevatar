using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

public sealed record NyxIdSessionRefreshResult(
    bool Succeeded,
    string? AccessToken = null,
    string? RefreshToken = null,
    int? ExpiresIn = null,
    string? Detail = null);

public sealed record NyxIdChannelRelayReplyResult(
    bool Succeeded,
    string? MessageId = null,
    string? PlatformMessageId = null,
    string? Detail = null,
    bool EditUnsupported = false);

/// <summary>HTTP client for calling NyxID REST API endpoints.</summary>
public sealed class NyxIdApiClient
{
    /// <summary>
    /// Default <c>User-Agent</c> injected on every call to <see cref="ProxyRequestAsync"/>
    /// when the caller does not specify one in <c>extraHeaders</c>. GitHub's REST API rejects
    /// requests without a <c>User-Agent</c> with HTTP 403 ("Request forbidden by administrative
    /// rules") — see https://docs.github.com/en/rest/overview/resources-in-the-rest-api#user-agent-required.
    /// .NET's <c>HttpClient</c> does not set one by default; NyxID proxies the client's headers
    /// through to GitHub, so the absence at the .NET layer manifests as a GitHub 403 in
    /// production. CLI tools written against <c>reqwest</c> (e.g. <c>nyxid proxy request</c>)
    /// happen to send <c>reqwest/x.y</c> as their default and so never hit this.
    /// </summary>
    public const string DefaultProxyUserAgent = "aevatar-agent-builder";
    private const string UserAgentHeaderName = "User-Agent";

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

    // ─── Session Refresh ───

    public async Task<NyxIdSessionRefreshResult> RefreshSessionAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return new NyxIdSessionRefreshResult(false, Detail: "missing_refresh_token");

        var response = await PostWithoutAuthAsync(
            "/api/v1/auth/refresh",
            JsonSerializer.Serialize(new { refresh_token = refreshToken.Trim() }),
            ct);

        if (TryParseErrorEnvelope(response, out var errorDetail))
            return new NyxIdSessionRefreshResult(false, Detail: errorDetail);

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;

            if (!root.TryGetProperty("access_token", out var accessTokenProp) ||
                accessTokenProp.ValueKind != JsonValueKind.String)
            {
                return new NyxIdSessionRefreshResult(false, Detail: "invalid_refresh_response missing_access_token");
            }

            var refreshTokenValue = root.TryGetProperty("refresh_token", out var refreshTokenProp) &&
                                    refreshTokenProp.ValueKind == JsonValueKind.String
                ? refreshTokenProp.GetString()
                : null;
            var expiresIn = root.TryGetProperty("expires_in", out var expiresInProp) &&
                            expiresInProp.ValueKind == JsonValueKind.Number
                ? expiresInProp.GetInt32()
                : (int?)null;

            return new NyxIdSessionRefreshResult(
                true,
                AccessToken: accessTokenProp.GetString(),
                RefreshToken: refreshTokenValue,
                ExpiresIn: expiresIn);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "NyxID session refresh returned invalid JSON");
            return new NyxIdSessionRefreshResult(false, Detail: "invalid_refresh_response invalid_json");
        }
    }

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

        var callerSpecifiedUserAgent = false;
        if (extraHeaders != null)
        {
            foreach (var (key, value) in extraHeaders)
            {
                request.Headers.TryAddWithoutValidation(key, value);
                if (string.Equals(key, UserAgentHeaderName, StringComparison.OrdinalIgnoreCase))
                    callerSpecifiedUserAgent = true;
            }
        }

        // GitHub-required User-Agent (#417 follow-up). NyxID proxies whatever the .NET client
        // sends, and HttpClient sends none by default, so without this every GitHub call lands
        // as 403 "Request forbidden by administrative rules". Inject a default for *all* proxy
        // targets — non-GitHub services don't care about UA either way, and pinning it at the
        // proxy boundary means SkillRunner / agent-builder / preflight all benefit without
        // every call site remembering to pass it.
        if (!callerSpecifiedUserAgent)
            request.Headers.TryAddWithoutValidation(UserAgentHeaderName, DefaultProxyUserAgent);

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

    public Task<string> ListUserServicesAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/user-services", ct);

    public Task<string> UpdateUserServiceAsync(string token, string id, string body, CancellationToken ct) =>
        PutAsync(token, $"/api/v1/user-services/{Uri.EscapeDataString(id)}", body, ct);

    // ─── Proxy (additions) ───

    public Task<string> DiscoverProxyServicesAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/proxy/services?per_page=100", ct);

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

    public Task<string> GetLlmServicesAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/v1/llm/services", ct);

    public Task<string> ProvisionLlmServiceAsync(string token, string provisionEndpointId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provisionEndpointId);
        var normalized = provisionEndpointId.Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Contains("..", StringComparison.Ordinal) ||
            normalized.Contains("://", StringComparison.Ordinal))
        {
            throw new ArgumentException("Provision endpoint id must be a relative NyxID LLM service endpoint id.", nameof(provisionEndpointId));
        }

        return PostAsync(token, $"/api/v1/llm/services/{Uri.EscapeDataString(normalized)}", "{}", ct);
    }

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

    /// <summary>
    /// Sends a channel relay reply with plain text only.
    /// </summary>
    /// <remarks>
    /// Kept as a thin wrapper over <see cref="SendChannelRelayReplyAsync"/> so legacy call sites that
    /// only need a text fallback continue to compile. New call sites should prefer the rich overload.
    /// </remarks>
    public Task<NyxIdChannelRelayReplyResult> SendChannelRelayTextReplyAsync(
        string token,
        string messageId,
        string text,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Task.FromResult(new NyxIdChannelRelayReplyResult(false, Detail: "missing_reply_text"));

        return SendChannelRelayReplyAsync(token, messageId, new ChannelRelayReplyBody(text), ct);
    }

    /// <summary>
    /// Sends a channel relay reply with arbitrary body shape — text fallback and/or rich card metadata.
    /// </summary>
    /// <remarks>
    /// The <paramref name="body"/> is serialized as <c>{ message_id, reply: { text?, metadata: { card? } } }</c>.
    /// Transport-neutral callers (for example, the interactive reply dispatcher) use this overload to
    /// forward composer output verbatim; NyxID's per-platform adapter renders the card for each platform.
    /// </remarks>
    public async Task<NyxIdChannelRelayReplyResult> SendChannelRelayReplyAsync(
        string token,
        string messageId,
        ChannelRelayReplyBody body,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (string.IsNullOrWhiteSpace(token))
            return new NyxIdChannelRelayReplyResult(false, Detail: "missing_access_token");
        if (string.IsNullOrWhiteSpace(messageId))
            return new NyxIdChannelRelayReplyResult(false, Detail: "missing_message_id");
        if (string.IsNullOrWhiteSpace(body.Text) && body.Metadata?.Card is null)
            return new NyxIdChannelRelayReplyResult(false, Detail: "missing_reply_payload");

        var response = await PostAsync(
            token,
            "/api/v1/channel-relay/reply",
            JsonSerializer.Serialize(new
            {
                message_id = messageId,
                reply = BuildReplyNode(body),
            }),
            ct);

        if (TryParseErrorEnvelope(response, out var errorDetail))
            return new NyxIdChannelRelayReplyResult(false, Detail: errorDetail);

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            return new NyxIdChannelRelayReplyResult(
                true,
                MessageId: root.TryGetProperty("message_id", out var replyMessageId) && replyMessageId.ValueKind == JsonValueKind.String
                    ? replyMessageId.GetString()
                    : null,
                PlatformMessageId: root.TryGetProperty("platform_message_id", out var platformMessageId) &&
                                   platformMessageId.ValueKind == JsonValueKind.String
                    ? platformMessageId.GetString()
                    : null);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Nyx channel relay reply returned invalid JSON");
            return new NyxIdChannelRelayReplyResult(false, Detail: "invalid_channel_relay_reply_response");
        }
    }

    private static object BuildReplyNode(ChannelRelayReplyBody body)
    {
        var hasText = !string.IsNullOrWhiteSpace(body.Text);
        var hasCard = body.Metadata?.Card is not null;

        if (hasText && hasCard)
            return new { text = body.Text, metadata = new { card = body.Metadata!.Card } };
        if (hasText)
            return new { text = body.Text };

        return new { metadata = new { card = body.Metadata!.Card } };
    }

    /// <summary>
    /// Edits a previously sent channel-relay reply so the downstream platform sees updated content
    /// (per NyxID #480 / #483: <c>POST /api/v1/channel-relay/reply/update</c>).
    /// </summary>
    /// <param name="platformMessageId">
    /// The upstream platform-owned message identifier (for Lark, the <c>om_xxx</c> value) returned
    /// by a prior send call.
    /// </param>
    /// <remarks>
    /// Callers must treat <see cref="NyxIdChannelRelayReplyResult.EditUnsupported"/> as a terminal
    /// signal and stop issuing edits against this message for the remainder of the turn.
    /// </remarks>
    public async Task<NyxIdChannelRelayReplyResult> UpdateChannelRelayReplyAsync(
        string token,
        string platformMessageId,
        ChannelRelayReplyBody body,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (string.IsNullOrWhiteSpace(token))
            return new NyxIdChannelRelayReplyResult(false, Detail: "missing_access_token");
        if (string.IsNullOrWhiteSpace(platformMessageId))
            return new NyxIdChannelRelayReplyResult(false, Detail: "missing_platform_message_id");
        if (string.IsNullOrWhiteSpace(body.Text) && body.Metadata?.Card is null)
            return new NyxIdChannelRelayReplyResult(false, Detail: "missing_reply_payload");

        var response = await PostAsync(
            token,
            "/api/v1/channel-relay/reply/update",
            JsonSerializer.Serialize(new
            {
                message_id = platformMessageId,
                reply = BuildReplyNode(body),
            }),
            ct);

        if (TryParseErrorEnvelope(response, out var errorDetail))
        {
            var editUnsupported =
                errorDetail.Contains("edit_unsupported", StringComparison.Ordinal) ||
                errorDetail.Contains("nyx_status=501", StringComparison.Ordinal);
            return new NyxIdChannelRelayReplyResult(
                false,
                Detail: errorDetail,
                EditUnsupported: editUnsupported);
        }

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            var upstream = root.TryGetProperty("upstream_message_id", out var upstreamProp) &&
                           upstreamProp.ValueKind == JsonValueKind.String
                ? upstreamProp.GetString()
                : null;
            return new NyxIdChannelRelayReplyResult(
                true,
                MessageId: null,
                PlatformMessageId: upstream);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Nyx channel relay reply update returned invalid JSON");
            return new NyxIdChannelRelayReplyResult(false, Detail: "invalid_channel_relay_reply_update_response");
        }
    }

    /// <summary>
    /// Text-only convenience wrapper over
    /// <see cref="UpdateChannelRelayReplyAsync(string, string, ChannelRelayReplyBody, CancellationToken)"/>.
    /// </summary>
    public Task<NyxIdChannelRelayReplyResult> UpdateChannelRelayTextReplyAsync(
        string token,
        string platformMessageId,
        string text,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Task.FromResult(new NyxIdChannelRelayReplyResult(false, Detail: "missing_reply_text"));

        return UpdateChannelRelayReplyAsync(token, platformMessageId, new ChannelRelayReplyBody(text), ct);
    }

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

    internal async Task<string> PostAsync(string token, string path, string body, CancellationToken ct)
    {
        var url = $"{GetBaseUrl()}{path}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await SendAsync(request, ct);
    }

    internal async Task<string> PostWithoutAuthAsync(string path, string body, CancellationToken ct)
    {
        var url = $"{GetBaseUrl()}{path}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await SendAsync(request, ct);
    }

    internal async Task<string> PatchAsync(string token, string path, string body, CancellationToken ct)
    {
        var url = $"{GetBaseUrl()}{path}";
        using var request = new HttpRequestMessage(HttpMethod.Patch, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await SendAsync(request, ct);
    }

    internal async Task<string> PutAsync(string token, string path, string body, CancellationToken ct)
    {
        var url = $"{GetBaseUrl()}{path}";
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await SendAsync(request, ct);
    }

    internal async Task<string> DeleteAsync(string token, string path, CancellationToken ct)
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

    private static bool TryParseErrorEnvelope(string response, out string detail)
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
            if (!root.TryGetProperty("error", out var errorProp) ||
                errorProp.ValueKind != JsonValueKind.True)
            {
                return false;
            }

            var status = root.TryGetProperty("status", out var statusProp) &&
                         statusProp.ValueKind == JsonValueKind.Number
                ? statusProp.GetInt32()
                : (int?)null;
            var body = root.TryGetProperty("body", out var bodyProp) &&
                       bodyProp.ValueKind == JsonValueKind.String
                ? bodyProp.GetString()
                : null;
            var message = root.TryGetProperty("message", out var messageProp) &&
                          messageProp.ValueKind == JsonValueKind.String
                ? messageProp.GetString()
                : null;

            detail = $"nyx_status={status?.ToString() ?? "unknown"}" +
                     (string.IsNullOrWhiteSpace(body) ? string.Empty : $" body={body}") +
                     (string.IsNullOrWhiteSpace(message) ? string.Empty : $" message={message}");
            return true;
        }
        catch (JsonException)
        {
            detail = $"invalid_error_envelope response_length={response.Length}";
            return true;
        }
    }
}
