using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Identity.Broker;

/// <summary>
/// Production <see cref="INyxIdCapabilityBroker"/> implementation that talks
/// to NyxID's broker endpoints (ChronoAIProject/NyxID#549). Holds no
/// long-lived user secret material in its in-process state — see ADR-0017
/// §Storage Boundary. Service-level secrets (OAuth <c>client_secret</c>, the
/// state-token HMAC key) come from <see cref="NyxIdBrokerOptions"/> and are
/// expected to be loaded from KMS / secure config.
/// </summary>
public sealed class NyxIdRemoteCapabilityBroker : INyxIdCapabilityBroker, INyxIdBrokerCallbackClient
{
    public const string AuthorizeEndpoint = "/oauth/authorize";
    public const string TokenEndpoint = "/oauth/token";
    public const string BindingsEndpoint = "/oauth/bindings";
    public const string TokenExchangeGrantType = "urn:ietf:params:oauth:grant-type:token-exchange";
    public const string BindingIdSubjectTokenType = "urn:nyxid:params:oauth:token-type:binding-id";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly NyxIdBrokerOptions _options;
    private readonly StateTokenCodec _stateTokenCodec;
    private readonly IExternalIdentityBindingQueryPort _queryPort;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NyxIdRemoteCapabilityBroker> _logger;

    public NyxIdRemoteCapabilityBroker(
        HttpClient http,
        NyxIdBrokerOptions options,
        StateTokenCodec stateTokenCodec,
        IExternalIdentityBindingQueryPort queryPort,
        TimeProvider timeProvider,
        ILogger<NyxIdRemoteCapabilityBroker> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _stateTokenCodec = stateTokenCodec ?? throw new ArgumentNullException(nameof(stateTokenCodec));
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ─── INyxIdCapabilityBroker ───

    public Task<BindingChallenge> StartExternalBindingAsync(
        ExternalSubjectRef externalSubject,
        CancellationToken ct = default)
    {
        ExternalSubjectRefExtensions.EnsureValid(externalSubject);
        EnsureConfigured();

        var pkce = PkceHelper.GeneratePair();
        var correlationId = Guid.NewGuid().ToString("N");
        var stateToken = _stateTokenCodec.Encode(correlationId, externalSubject, pkce.CodeVerifier);

        var url = BuildAuthorizeUrl(stateToken, pkce.CodeChallenge);
        var expiresAt = _timeProvider.GetUtcNow().Add(_options.StateTokenLifetime).ToUnixTimeSeconds();
        return Task.FromResult(new BindingChallenge
        {
            AuthorizeUrl = url,
            ExpiresAtUnix = expiresAt,
        });
    }

    public async Task RevokeBindingAsync(
        ExternalSubjectRef externalSubject,
        CancellationToken ct = default)
    {
        ExternalSubjectRefExtensions.EnsureValid(externalSubject);
        EnsureConfigured();

        var bindingId = await _queryPort.ResolveAsync(externalSubject, ct).ConfigureAwait(false);
        if (bindingId is null)
        {
            // Idempotent: already revoked or never bound. NyxID's source-of-
            // truth role lives in the contract — local lack of binding means
            // the caller has nothing to do here.
            _logger.LogInformation(
                "Revoke skipped: no active binding for {Platform}:{Tenant}:{User}",
                externalSubject.Platform,
                externalSubject.Tenant,
                externalSubject.ExternalUserId);
            return;
        }

        await RevokeBindingByIdAsync(bindingId.Value, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Revokes a binding directly by its opaque id. Maps NyxID 4xx responses
    /// to silent success (already-revoked is idempotent) and 5xx to an
    /// exception so callers can retry / surface the failure.
    /// </summary>
    public async Task RevokeBindingByIdAsync(string bindingId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingId);
        EnsureConfigured();

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{TrimAuthority()}{BindingsEndpoint}/{Uri.EscapeDataString(bindingId)}");
        ApplyClientSecretBasic(request);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

        // 404 / 410 — binding already gone (NyxID-side revoke beat us, or
        // the id was never persisted). DELETE is idempotent; treat as success.
        if ((int)response.StatusCode is 404 or 410)
            return;

        if (response.IsSuccessStatusCode)
            return;

        // Anything else (401/403 client misauth, 422 validation, 5xx server
        // outage, etc.) is a real error. Surface body context for diagnosis
        // and throw so the caller can decide whether to retry / abort the
        // unbind workflow rather than masking it as success.
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
        EnsureConfigured();

        var bindingId = await _queryPort.ResolveAsync(externalSubject, ct).ConfigureAwait(false);
        if (bindingId is null)
            throw new BindingNotFoundException(externalSubject);

        return await IssueShortLivedByBindingIdAsync(externalSubject, bindingId.Value, scope, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Issues a short-lived access token for a known <paramref name="bindingId"/>
    /// via RFC 8693 token-exchange. Throws <see cref="BindingRevokedException"/>
    /// when NyxID reports <c>invalid_grant</c> on the binding.
    /// </summary>
    public async Task<CapabilityHandle> IssueShortLivedByBindingIdAsync(
        ExternalSubjectRef externalSubject,
        string bindingId,
        CapabilityScope scope,
        CancellationToken ct = default)
    {
        ExternalSubjectRefExtensions.EnsureValid(externalSubject);
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingId);
        ArgumentNullException.ThrowIfNull(scope);
        EnsureConfigured();

        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", TokenExchangeGrantType),
            new("subject_token", bindingId),
            new("subject_token_type", BindingIdSubjectTokenType),
        };
        if (!string.IsNullOrWhiteSpace(scope.Value))
            form.Add(new KeyValuePair<string, string>("scope", scope.Value));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{TrimAuthority()}{TokenEndpoint}")
        {
            Content = new FormUrlEncodedContent(form),
        };
        ApplyClientSecretBasic(request);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if ((int)response.StatusCode == 400 && IsInvalidGrant(body))
            {
                _logger.LogInformation(
                    "Binding revoked by NyxID for {Platform}:{Tenant}:{User}",
                    externalSubject.Platform,
                    externalSubject.Tenant,
                    externalSubject.ExternalUserId);
                throw new BindingRevokedException(externalSubject, "NyxID returned invalid_grant on token-exchange.");
            }

            _logger.LogError(
                "NyxID token-exchange failed: status={StatusCode}, body={Body}",
                (int)response.StatusCode,
                Truncate(body, 256));
            response.EnsureSuccessStatusCode();
        }

        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions, ct).ConfigureAwait(false)
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

    public bool TryDecodeStateToken(
        string stateToken,
        out string correlationId,
        out ExternalSubjectRef? externalSubject,
        out string pkceVerifier,
        out string? errorCode)
    {
        correlationId = string.Empty;
        externalSubject = null;
        pkceVerifier = string.Empty;
        if (!_stateTokenCodec.TryDecode(stateToken, out var payload, out errorCode) || payload is null)
            return false;

        correlationId = payload.CorrelationId;
        externalSubject = payload.ExternalSubject?.Clone();
        pkceVerifier = payload.PkceVerifier;
        return true;
    }

    public async Task<BrokerAuthorizationCodeResult> ExchangeAuthorizationCodeAsync(
        string authorizationCode,
        string codeVerifier,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);
        EnsureConfigured();

        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("code", authorizationCode),
            new("code_verifier", codeVerifier),
            new("redirect_uri", _options.RedirectUri),
            new("client_id", _options.ClientId),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{TrimAuthority()}{TokenEndpoint}")
        {
            Content = new FormUrlEncodedContent(form),
        };
        ApplyClientSecretBasic(request);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogError(
                "NyxID authorization-code exchange failed: status={StatusCode}, body={Body}",
                (int)response.StatusCode,
                Truncate(body, 256));
            response.EnsureSuccessStatusCode();
        }

        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("NyxID returned an empty authorization-code response.");

        if (string.IsNullOrWhiteSpace(payload.BindingId))
        {
            // The NyxID#549 contract requires `binding_id` in the response when
            // the broker scope is requested. A missing value implies the client
            // is misconfigured (likely missing the broker scope) — surface
            // loudly so deploys catch it.
            throw new InvalidOperationException(
                "NyxID authorization-code response did not include binding_id; verify the client has urn:nyxid:scope:broker_binding scope.");
        }

        return new BrokerAuthorizationCodeResult(payload.BindingId, payload.IdToken, payload.AccessToken);
    }

    // ─── Internals ───

    private string BuildAuthorizeUrl(string stateToken, string codeChallenge)
    {
        var queryParts = new List<string>
        {
            $"response_type=code",
            $"client_id={Uri.EscapeDataString(_options.ClientId)}",
            $"redirect_uri={Uri.EscapeDataString(_options.RedirectUri)}",
            $"scope={Uri.EscapeDataString(_options.Scope)}",
            $"state={Uri.EscapeDataString(stateToken)}",
            $"code_challenge={Uri.EscapeDataString(codeChallenge)}",
            $"code_challenge_method=S256",
        };
        return $"{TrimAuthority()}{AuthorizeEndpoint}?{string.Join("&", queryParts)}";
    }

    private void ApplyClientSecretBasic(HttpRequestMessage request)
    {
        var raw = $"{_options.ClientId}:{_options.ClientSecret}";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.Authority))
            throw new InvalidOperationException("NyxIdBrokerOptions.Authority is not configured.");
        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new InvalidOperationException("NyxIdBrokerOptions.ClientId is not configured.");
        if (string.IsNullOrWhiteSpace(_options.ClientSecret))
            throw new InvalidOperationException("NyxIdBrokerOptions.ClientSecret is not configured.");
        if (string.IsNullOrWhiteSpace(_options.RedirectUri))
            throw new InvalidOperationException("NyxIdBrokerOptions.RedirectUri is not configured.");
    }

    private string TrimAuthority() => _options.Authority.TrimEnd('/');

    private static bool IsInvalidGrant(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var errorElement)
                && errorElement.ValueKind == JsonValueKind.String
                && string.Equals(errorElement.GetString(), "invalid_grant", StringComparison.Ordinal);
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
