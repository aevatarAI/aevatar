using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgents.Channel.Identity.Broker;

/// <summary>
/// Production <see cref="INyxIdCapabilityBroker"/> implementation that talks
/// to NyxID's broker endpoints (ChronoAIProject/NyxID#549). Reads the
/// cluster-shared OAuth <c>client_id</c> + HMAC key from
/// <see cref="IAevatarOAuthClientProvider"/> (cluster singleton actor) so
/// production deploys need zero broker-specific appsettings — the bootstrap
/// service self-registers the client at NyxID DCR on first startup.
/// </summary>
/// <remarks>
/// Resolves <see cref="HttpClient"/> per-request via
/// <see cref="IHttpClientFactory"/> (named client <see cref="HttpClientName"/>)
/// so the broker can be safely registered as a singleton without pinning the
/// inner <c>HttpMessageHandler</c>. Pinning would defeat IHttpClientFactory's
/// 2-min handler rotation: stale DNS, expired sockets, and TLS-cert refreshes
/// would never be picked up on long-running silos.
/// </remarks>
public sealed class NyxIdRemoteCapabilityBroker : INyxIdCapabilityBroker, INyxIdBrokerCallbackClient
{
    public const string AuthorizeEndpoint = "/oauth/authorize";
    public const string TokenEndpoint = "/oauth/token";
    public const string BindingsEndpoint = "/oauth/bindings";
    public const string TokenExchangeGrantType = "urn:ietf:params:oauth:grant-type:token-exchange";
    public const string BindingIdSubjectTokenType = "urn:nyxid:params:oauth:token-type:binding-id";

    /// <summary>
    /// Named <see cref="IHttpClientFactory"/> client used for all NyxID broker
    /// HTTP calls. Configured in
    /// <c>IdentityServiceCollectionExtensions.AddChannelIdentity</c>.
    /// </summary>
    public const string HttpClientName = "nyxid-broker";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAevatarOAuthClientProvider _clientProvider;
    private readonly NyxIdBrokerOptions _options;
    private readonly StateTokenCodec _stateTokenCodec;
    private readonly IExternalIdentityBindingQueryPort _queryPort;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NyxIdRemoteCapabilityBroker> _logger;

    public NyxIdRemoteCapabilityBroker(
        IHttpClientFactory httpClientFactory,
        IAevatarOAuthClientProvider clientProvider,
        IOptions<NyxIdBrokerOptions> options,
        StateTokenCodec stateTokenCodec,
        IExternalIdentityBindingQueryPort queryPort,
        TimeProvider timeProvider,
        ILogger<NyxIdRemoteCapabilityBroker> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _stateTokenCodec = stateTokenCodec ?? throw new ArgumentNullException(nameof(stateTokenCodec));
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private HttpClient CreateHttpClient() => _httpClientFactory.CreateClient(HttpClientName);

    private string ResolveRedirectUri() => NyxIdRedirectUriResolver.Resolve(_logger);

    public async Task<BindingChallenge> StartExternalBindingAsync(
        ExternalSubjectRef externalSubject,
        CancellationToken ct = default)
    {
        ExternalSubjectRefExtensions.EnsureValid(externalSubject);

        var snapshot = await _clientProvider.GetAsync(ct).ConfigureAwait(false);
        var redirectUri = ResolveRedirectUri();
        EnsureClientCurrent(snapshot, redirectUri);

        var pkce = PkceHelper.GeneratePair();
        var correlationId = Guid.NewGuid().ToString("N");
        var stateToken = await _stateTokenCodec
            .EncodeAsync(correlationId, externalSubject, pkce.CodeVerifier, ct)
            .ConfigureAwait(false);

        var url = BuildAuthorizeUrl(snapshot, redirectUri, stateToken, pkce.CodeChallenge);
        var expiresAt = _timeProvider.GetUtcNow().Add(_options.StateTokenLifetime).ToUnixTimeSeconds();
        return new BindingChallenge
        {
            AuthorizeUrl = url,
            ExpiresAtUnix = expiresAt,
        };
    }

    public async Task RevokeBindingAsync(
        ExternalSubjectRef externalSubject,
        CancellationToken ct = default)
    {
        ExternalSubjectRefExtensions.EnsureValid(externalSubject);

        var bindingId = await _queryPort.ResolveAsync(externalSubject, ct).ConfigureAwait(false);
        if (bindingId is null)
        {
            _logger.LogInformation(
                "Revoke skipped: no active binding for {Platform}:{Tenant}:{User}",
                externalSubject.Platform,
                externalSubject.Tenant,
                externalSubject.ExternalUserId);
            return;
        }

        await RevokeBindingByIdAsync(bindingId.Value, ct).ConfigureAwait(false);
    }

    public async Task RevokeBindingByIdAsync(string bindingId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingId);

        var snapshot = await _clientProvider.GetAsync(ct).ConfigureAwait(false);
        // NyxID delete_binding requires client_id from Basic auth OR query
        // params; missing → silent 204 with no revocation. Aevatar uses the
        // public-client + PKCE shape (no client_secret stored), so we send
        // client_id as a query param. NyxID validates ownership inside
        // revoke_binding_by_client (oauth_broker_service.rs) — only the
        // binding's owning client_id revokes; mismatches still 204 cleanly.
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"{snapshot.NyxIdAuthority.TrimEnd('/')}{BindingsEndpoint}/{Uri.EscapeDataString(bindingId)}?client_id={Uri.EscapeDataString(snapshot.ClientId)}");
        var http = CreateHttpClient();
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);

        if ((int)response.StatusCode is 404 or 410)
            return;
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        _logger.LogError(
            "NyxID revoke binding failed: status={StatusCode}, binding_id={BindingId}, body={Body}",
            (int)response.StatusCode,
            bindingId,
            Truncate(body, 256));
        response.EnsureSuccessStatusCode();
    }

    public async Task<CapabilityHandle> IssueShortLivedAsync(
        ExternalSubjectRef externalSubject,
        CapabilityScope scope,
        CancellationToken ct = default)
    {
        ExternalSubjectRefExtensions.EnsureValid(externalSubject);
        ArgumentNullException.ThrowIfNull(scope);

        var bindingId = await _queryPort.ResolveAsync(externalSubject, ct).ConfigureAwait(false);
        if (bindingId is null)
            throw new BindingNotFoundException(externalSubject);

        return await IssueShortLivedByBindingIdAsync(externalSubject, bindingId.Value, scope, ct).ConfigureAwait(false);
    }

    public async Task<CapabilityHandle> IssueShortLivedByBindingIdAsync(
        ExternalSubjectRef externalSubject,
        string bindingId,
        CapabilityScope scope,
        CancellationToken ct = default)
    {
        ExternalSubjectRefExtensions.EnsureValid(externalSubject);
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingId);
        ArgumentNullException.ThrowIfNull(scope);

        var snapshot = await _clientProvider.GetAsync(ct).ConfigureAwait(false);

        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", TokenExchangeGrantType),
            new("subject_token", bindingId),
            new("subject_token_type", BindingIdSubjectTokenType),
            new("client_id", snapshot.ClientId),
        };
        if (!string.IsNullOrWhiteSpace(scope.Value))
            form.Add(new KeyValuePair<string, string>("scope", scope.Value));

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{snapshot.NyxIdAuthority.TrimEnd('/')}{TokenEndpoint}")
        {
            Content = new FormUrlEncodedContent(form),
        };

        var http = CreateHttpClient();
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if ((int)response.StatusCode == 400 && IsInvalidGrant(body))
                throw new BindingRevokedException(externalSubject, "NyxID returned invalid_grant on token-exchange.");
            if ((int)response.StatusCode == 400 && IsInvalidScope(body))
                throw new BindingScopeMismatchException(externalSubject, "NyxID returned invalid_scope on token-exchange.");
            _logger.LogError(
                "NyxID token-exchange failed: status={StatusCode}, body={Body}",
                (int)response.StatusCode,
                Truncate(body, 256));
            response.EnsureSuccessStatusCode();
        }

        var payload = await response.Content
            .ReadFromJsonAsync<TokenResponse>(JsonOptions, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("NyxID returned an empty token-exchange response.");

        return new CapabilityHandle
        {
            AccessToken = payload.AccessToken ?? string.Empty,
            ExpiresAtUnix = payload.ExpiresIn.HasValue
                ? _timeProvider.GetUtcNow().AddSeconds(payload.ExpiresIn.Value).ToUnixTimeSeconds()
                : 0,
            Scope = payload.Scope ?? scope.Value,
        };
    }

    // ─── INyxIdBrokerCallbackClient ───

    public async Task<CallbackStateDecode> TryDecodeStateTokenAsync(string stateToken, CancellationToken ct = default)
    {
        var result = await _stateTokenCodec.TryDecodeAsync(stateToken, ct).ConfigureAwait(false);
        if (!result.Succeeded || result.Payload is null)
            return CallbackStateDecode.Failed(result.ErrorCode ?? "state_unknown");
        return CallbackStateDecode.Ok(
            result.Payload.CorrelationId,
            result.Payload.ExternalSubject?.Clone(),
            result.Payload.PkceVerifier);
    }

    public async Task<BrokerAuthorizationCodeResult> ExchangeAuthorizationCodeAsync(
        string authorizationCode,
        string codeVerifier,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);

        var snapshot = await _clientProvider.GetAsync(ct).ConfigureAwait(false);
        var redirectUri = ResolveRedirectUri();
        EnsureClientCurrent(snapshot, redirectUri);

        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("code", authorizationCode),
            new("code_verifier", codeVerifier),
            new("redirect_uri", redirectUri),
            new("client_id", snapshot.ClientId),
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{snapshot.NyxIdAuthority.TrimEnd('/')}{TokenEndpoint}")
        {
            Content = new FormUrlEncodedContent(form),
        };

        var http = CreateHttpClient();
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogError(
                "NyxID authorization-code exchange failed: status={StatusCode}, body={Body}",
                (int)response.StatusCode,
                Truncate(body, 256));
            response.EnsureSuccessStatusCode();
        }

        var payload = await response.Content
            .ReadFromJsonAsync<TokenResponse>(JsonOptions, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("NyxID returned an empty authorization-code response.");

        // binding_id is null when broker_capability_enabled=false on this
        // client at NyxID. We surface the gap to the caller (callback handler)
        // rather than throwing — the user-visible error message guides ops to
        // toggle the flag at NyxID admin (one-time per cluster).
        return new BrokerAuthorizationCodeResult(payload.BindingId, payload.IdToken, payload.AccessToken);
    }

    private string BuildAuthorizeUrl(
        AevatarOAuthClientSnapshot snapshot,
        string redirectUri,
        string stateToken,
        string codeChallenge)
    {
        var queryParts = new List<string>
        {
            "response_type=code",
            $"client_id={Uri.EscapeDataString(snapshot.ClientId)}",
            $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
            $"scope={Uri.EscapeDataString(AevatarOAuthClientScopes.AuthorizationScope)}",
            $"state={Uri.EscapeDataString(stateToken)}",
            $"code_challenge={Uri.EscapeDataString(codeChallenge)}",
            "code_challenge_method=S256",
        };
        return $"{snapshot.NyxIdAuthority.TrimEnd('/')}{AuthorizeEndpoint}?{string.Join("&", queryParts)}";
    }

    private static void EnsureClientCurrent(AevatarOAuthClientSnapshot snapshot, string resolvedRedirectUri)
    {
        if (!string.IsNullOrEmpty(snapshot.RedirectUri)
            && string.Equals(snapshot.RedirectUri, resolvedRedirectUri, StringComparison.Ordinal)
            && AevatarOAuthClientScopes.ContainsRequiredScopes(snapshot.OauthScope))
        {
            return;
        }

        throw new AevatarOAuthClientNotProvisionedException(
            "Aevatar OAuth client redirect_uri or oauth_scope is not current. Bootstrap must re-run DCR before issuing NyxID authorize URLs.");
    }

    private static bool IsInvalidGrant(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var element)
                && element.ValueKind == JsonValueKind.String
                && string.Equals(element.GetString(), "invalid_grant", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsInvalidScope(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var element)
                && element.ValueKind == JsonValueKind.String
                && string.Equals(element.GetString(), "invalid_scope", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    private sealed record TokenResponse
    {
        public string? AccessToken { get; init; }
        public string? IdToken { get; init; }
        public string? BindingId { get; init; }
        public string? Scope { get; init; }
        public string? TokenType { get; init; }
        public int? ExpiresIn { get; init; }
    }
}

/// <summary>
/// Result of a state-token decode call on the callback side.
/// </summary>
public sealed record CallbackStateDecode(bool Succeeded, string? CorrelationId, ExternalSubjectRef? ExternalSubject, string? PkceVerifier, string? ErrorCode)
{
    public static CallbackStateDecode Ok(string correlationId, ExternalSubjectRef? subject, string verifier) =>
        new(true, correlationId, subject, verifier, null);

    public static CallbackStateDecode Failed(string errorCode) =>
        new(false, null, null, null, errorCode);
}
