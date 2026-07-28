using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Foundation.Abstractions.Helpers;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgents.Channel.Identity.Broker;

/// <summary>
/// Production <see cref="INyxIdCapabilityBroker"/> implementation that talks
/// to NyxID's broker endpoints (ChronoAIProject/NyxID#549). Reads the
/// deployment-owned OAuth <c>client_id</c> and actor-owned HMAC/runtime facts
/// through <see cref="IAevatarOAuthClientProvider"/>.
/// </summary>
/// <remarks>
/// Resolves <see cref="HttpClient"/> per-request via
/// <see cref="IHttpClientFactory"/> (named client <see cref="HttpClientName"/>)
/// so the broker can be safely registered as a singleton without pinning the
/// inner <c>HttpMessageHandler</c>. Pinning would defeat IHttpClientFactory's
/// 2-min handler rotation: stale DNS, expired sockets, and TLS-cert refreshes
/// would never be picked up on long-running silos.
/// </remarks>
public sealed class NyxIdRemoteCapabilityBroker :
    INyxIdCapabilityBroker,
    INyxIdConnectedServiceInventoryCapabilityIssuer,
    INyxIdSkillCapabilityIssuer,
    INyxIdBrokerCallbackClient,
    INyxIdBindingRetirementPort
{
    public const string AuthorizeEndpoint = "/oauth/authorize";
    public const string TokenEndpoint = "/oauth/token";
    public const string BindingsEndpoint = "/oauth/bindings";
    public const string UserServicesEndpoint = "/api/v1/user-services";
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

    private string[] RequiredResourceUris() =>
        AevatarOAuthClientResources.RequiredResourceUris(
            _options.ResourceServerBaseUrl,
            _options.RequiredLlmServiceSlug,
            _options.AdditionalRequiredServiceSlugs);

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
        var existingBinding = await _queryPort.ResolveAsync(externalSubject, ct).ConfigureAwait(false);
        var expectedBindingHash = existingBinding is null
            ? null
            : HashBindingId(existingBinding.Value);
        var stateToken = await _stateTokenCodec
            .EncodeAsync(correlationId, externalSubject, pkce.CodeVerifier, expectedBindingHash, ct)
            .ConfigureAwait(false);

        var url = BuildAuthorizeUrl(
            snapshot,
            redirectUri,
            stateToken,
            pkce.CodeChallenge,
            externalSubject);
        var expiresAt = _timeProvider.GetUtcNow().Add(_options.StateTokenLifetime).ToUnixTimeSeconds();
        return new BindingChallenge
        {
            AuthorizeUrl = url,
            ExpiresAtUnix = expiresAt,
            RenewsExistingBinding = existingBinding is not null,
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
            Truncate(SecretScrubber.Scrub(body), 256));
        response.EnsureSuccessStatusCode();
    }

    public Task RetireAsync(string bindingId, CancellationToken ct = default) =>
        RevokeBindingByIdAsync(bindingId, ct);

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
        var requiredResources = RequiredResourceUris();
        var payload = await ExchangeBindingTokenAsync(
                snapshot,
                externalSubject,
                bindingId,
                scope,
                ct)
            .ConfigureAwait(false);
        var accessToken = payload.AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("NyxID returned a token-exchange response without an access token.");

        var missingResources = await FindMissingRequiredResourcesAsync(accessToken, requiredResources, ct)
            .ConfigureAwait(false);
        if (missingResources.Length > 0)
        {
            throw new BindingServiceAccessMismatchException(
                externalSubject,
                missingResources,
                "NyxID binding grant does not include every required service resource.");
        }

        return ToCapabilityHandle(payload, scope);
    }

    async Task<CapabilityHandle> INyxIdConnectedServiceInventoryCapabilityIssuer.IssueByBindingIdAsync(
        ExternalSubjectRef externalSubject,
        string bindingId,
        CancellationToken ct) =>
        await IssueProxyCapabilityByBindingIdAsync(externalSubject, bindingId, ct).ConfigureAwait(false);

    async Task<CapabilityHandle> INyxIdSkillCapabilityIssuer.IssueByBindingIdAsync(
        ExternalSubjectRef externalSubject,
        string bindingId,
        CancellationToken ct) =>
        await IssueProxyCapabilityByBindingIdAsync(externalSubject, bindingId, ct).ConfigureAwait(false);

    private async Task<CapabilityHandle> IssueProxyCapabilityByBindingIdAsync(
        ExternalSubjectRef externalSubject,
        string bindingId,
        CancellationToken ct)
    {
        ExternalSubjectRefExtensions.EnsureValid(externalSubject);
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingId);

        var scope = new CapabilityScope { Value = AevatarOAuthClientScopes.Proxy };
        var snapshot = await _clientProvider.GetAsync(ct).ConfigureAwait(false);
        var payload = await ExchangeBindingTokenAsync(
                snapshot,
                externalSubject,
                bindingId,
                scope,
                ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(payload.AccessToken))
            throw new InvalidOperationException("NyxID returned a token-exchange response without an access token.");

        return ToCapabilityHandle(payload, scope);
    }

    private CapabilityHandle ToCapabilityHandle(TokenResponse payload, CapabilityScope requestedScope) => new()
    {
        AccessToken = payload.AccessToken,
        ExpiresAtUnix = payload.ExpiresIn.HasValue
            ? _timeProvider.GetUtcNow().AddSeconds(payload.ExpiresIn.Value).ToUnixTimeSeconds()
            : 0,
        Scope = payload.Scope ?? requestedScope.Value,
    };

    private async Task<TokenResponse> ExchangeBindingTokenAsync(
        AevatarOAuthClientSnapshot snapshot,
        ExternalSubjectRef externalSubject,
        string bindingId,
        CapabilityScope scope,
        CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", TokenExchangeGrantType),
            new("subject_token", bindingId),
            new("subject_token_type", BindingIdSubjectTokenType),
            new("client_id", snapshot.ClientId),
        };
        if (!string.IsNullOrWhiteSpace(scope.Value))
            form.Add(new KeyValuePair<string, string>("scope", scope.Value));
        // Inherit the complete Consent grant. Supplying resource here would
        // narrow away optional services selected by the user.

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
            if ((int)response.StatusCode == 400 && IsInvalidTarget(body))
            {
                throw new BindingServiceAccessMismatchException(
                    externalSubject,
                    RequiredResourceUris(),
                    "NyxID returned invalid_target for the required services on token-exchange.");
            }
            _logger.LogError(
                "NyxID token-exchange failed: status={StatusCode}, body={Body}",
                (int)response.StatusCode,
                Truncate(SecretScrubber.Scrub(body), 256));
            response.EnsureSuccessStatusCode();
        }

        return await response.Content
            .ReadFromJsonAsync<TokenResponse>(JsonOptions, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("NyxID returned an empty token-exchange response.");
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
            result.Payload.PkceVerifier,
            result.Payload.ExpectedBindingHash);
    }

    public Task<BrokerAuthorizationCodeResult> ExchangeAuthorizationCodeAsync(
        string authorizationCode,
        string codeVerifier,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);

        return ExchangeAuthorizationCodeCoreAsync(
            authorizationCode,
            codeVerifier,
            ResolveRedirectUri(),
            requireProvisionedRedirectUri: true,
            ct);
    }

    public Task<BrokerAuthorizationCodeResult> ExchangeAuthorizationCodeAsync(
        string authorizationCode,
        string codeVerifier,
        string redirectUri,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);

        return ExchangeAuthorizationCodeCoreAsync(
            authorizationCode,
            codeVerifier,
            redirectUri.Trim(),
            requireProvisionedRedirectUri: false,
            ct);
    }

    public async Task<OwnerScopeId?> ResolveBindingOwnerScopeAsync(
        string bindingId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingId);

        var normalizedBindingId = bindingId.Trim();
        var snapshot = await _clientProvider.GetAsync(ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{snapshot.NyxIdAuthority.TrimEnd('/')}{BindingsEndpoint}/{Uri.EscapeDataString(normalizedBindingId)}?client_id={Uri.EscapeDataString(snapshot.ClientId)}");
        var http = CreateHttpClient();
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);

        if ((int)response.StatusCode is 404 or 410)
            return null;
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogError(
                "NyxID binding owner introspection failed: status={StatusCode}, body={Body}",
                (int)response.StatusCode,
                Truncate(SecretScrubber.Scrub(body), 256));
            response.EnsureSuccessStatusCode();
        }

        var binding = await response.Content
            .ReadFromJsonAsync<BindingIntrospectionResponse>(JsonOptions, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("NyxID returned an empty binding introspection response.");
        if (binding.Revoked)
            return null;
        if (!string.Equals(binding.BindingId, normalizedBindingId, StringComparison.Ordinal)
            || !string.Equals(binding.ClientId, snapshot.ClientId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("NyxID returned binding metadata for an unexpected binding or OAuth client.");
        }

        var ownerScopeId = binding.NyxSubject?.Trim();
        return string.IsNullOrWhiteSpace(ownerScopeId)
            ? null
            : new OwnerScopeId { Value = ownerScopeId };
    }

    private async Task<BrokerAuthorizationCodeResult> ExchangeAuthorizationCodeCoreAsync(
        string authorizationCode,
        string codeVerifier,
        string redirectUri,
        bool requireProvisionedRedirectUri,
        CancellationToken ct)
    {
        var snapshot = await _clientProvider.GetAsync(ct).ConfigureAwait(false);
        if (requireProvisionedRedirectUri)
            EnsureClientCurrent(snapshot, redirectUri);

        // The authorization code already carries the user's finalized Consent selection.
        // Repeating resource here would narrow that grant to Aevatar's minimum runtime set.
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
                Truncate(SecretScrubber.Scrub(body), 256));
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
        return new BrokerAuthorizationCodeResult(payload.BindingId, payload.IdToken, payload.AccessToken)
        {
            RefreshToken = payload.RefreshToken,
            TokenType = payload.TokenType,
            ExpiresIn = payload.ExpiresIn,
            Scope = payload.Scope,
            BindingUpdated = payload.BindingUpdated == true,
        };
    }

    private string BuildAuthorizeUrl(
        AevatarOAuthClientSnapshot snapshot,
        string redirectUri,
        string stateToken,
        string codeChallenge,
        ExternalSubjectRef externalSubject)
    {
        var queryParts = new List<string>
        {
            "response_type=code",
            $"client_id={Uri.EscapeDataString(snapshot.ClientId)}",
            $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
            $"scope={Uri.EscapeDataString(AevatarOAuthClientScopes.AuthorizationScope)}",
            "prompt=consent",
            $"external_subject_platform={Uri.EscapeDataString(externalSubject.Platform)}",
            $"external_subject_external_user_id={Uri.EscapeDataString(externalSubject.ExternalUserId)}",
        };
        if (!string.IsNullOrWhiteSpace(externalSubject.Tenant))
        {
            queryParts.Add(
                $"external_subject_tenant={Uri.EscapeDataString(externalSubject.Tenant)}");
        }
        queryParts.AddRange(RequiredResourceUris()
            .Select(static resource => $"resource={Uri.EscapeDataString(resource)}"));
        queryParts.AddRange(
        [
            $"state={Uri.EscapeDataString(stateToken)}",
            $"code_challenge={Uri.EscapeDataString(codeChallenge)}",
            "code_challenge_method=S256",
        ]);
        return $"{snapshot.NyxIdAuthority.TrimEnd('/')}{AuthorizeEndpoint}?{string.Join("&", queryParts)}";
    }

    private static void EnsureClientCurrent(AevatarOAuthClientSnapshot snapshot, string resolvedRedirectUri)
    {
        if (!string.IsNullOrEmpty(snapshot.RedirectUri)
            && string.Equals(snapshot.RedirectUri, resolvedRedirectUri, StringComparison.Ordinal)
            && AevatarOAuthClientScopes.ContainsRequiredAuthorizationScopes(snapshot.OauthScope))
        {
            return;
        }

        throw new AevatarOAuthClientNotProvisionedException(
            "Aevatar OAuth client redirect_uri or oauth_scope is not current. Bootstrap must reconcile configured client facts before issuing NyxID authorize URLs.");
    }

    private static bool IsInvalidGrant(string body) => IsOAuthError(body, "invalid_grant");

    private static bool IsInvalidScope(string body) => IsOAuthError(body, "invalid_scope");

    private static bool IsInvalidTarget(string body) => IsOAuthError(body, "invalid_target");

    private static bool IsOAuthError(string body, string expected)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var element)
                && element.ValueKind == JsonValueKind.String
                && string.Equals(element.GetString(), expected, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<string[]> FindMissingRequiredResourcesAsync(
        string accessToken,
        IReadOnlyCollection<string> requiredResources,
        CancellationToken ct)
    {
        var grant = ParseTokenServiceGrant(accessToken);
        if (!grant.Valid)
            return requiredResources.ToArray();

        var missingResources = AevatarOAuthClientResources.MissingRequiredResources(
            grant.ResourceUris,
            requiredResources);
        if (missingResources.Length == 0)
            return [];

        var catalogResources = await ResolveGrantedCatalogResourcesAsync(accessToken, grant, ct)
            .ConfigureAwait(false);
        return AevatarOAuthClientResources.MissingRequiredResources(
            grant.ResourceUris.Concat(catalogResources),
            requiredResources);
    }

    private async Task<string[]> ResolveGrantedCatalogResourcesAsync(
        string accessToken,
        TokenServiceGrant grant,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{_options.ResourceServerBaseUrl.Trim().TrimEnd('/')}{UserServicesEndpoint}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var http = CreateHttpClient();
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogError(
                "NyxID user-service catalog lookup failed: status={StatusCode}, body={Body}",
                (int)response.StatusCode,
                Truncate(SecretScrubber.Scrub(body), 256));
            response.EnsureSuccessStatusCode();
        }

        var catalog = await response.Content
            .ReadFromJsonAsync<UserServiceCatalogResponse>(JsonOptions, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("NyxID returned an empty user-service catalog response.");
        var allowedServiceIds = grant.AllowedServiceIds.ToHashSet(StringComparer.Ordinal);
        return catalog.Services
            .Where(service => grant.AllowsEveryService || allowedServiceIds.Contains(service.Id))
            .Select(static service => service.ResourceUri)
            .Where(static resource => !string.IsNullOrWhiteSpace(resource))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static TokenServiceGrant ParseTokenServiceGrant(string accessToken)
    {
        var parts = accessToken.Split('.');
        if (parts.Length != 3)
            return TokenServiceGrant.Invalid;

        try
        {
            var payload = parts[1]
                .Replace('-', '+')
                .Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            var root = document.RootElement;
            var resourceUris = ReadStringArray(root, "resources", out var hasResourceRestriction);
            var allowedServiceIds = ReadStringArray(
                root,
                "allowed_service_ids",
                out var hasServiceIdRestriction);
            var allowAllServices = root.TryGetProperty("allow_all_services", out var allowAll)
                                   && allowAll.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? allowAll.GetBoolean()
                : (bool?)null;
            var allowsEveryService = allowAllServices == true
                                     || allowAllServices is null
                                     && !hasResourceRestriction
                                     && !hasServiceIdRestriction;
            return new TokenServiceGrant(
                true,
                allowsEveryService,
                resourceUris,
                allowedServiceIds);
        }
        catch (FormatException)
        {
            return TokenServiceGrant.Invalid;
        }
        catch (JsonException)
        {
            return TokenServiceGrant.Invalid;
        }
    }

    private static string[] ReadStringArray(JsonElement owner, string propertyName, out bool present)
    {
        present = owner.TryGetProperty(propertyName, out var values)
                  && values.ValueKind == JsonValueKind.Array;
        return present
            ? values.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString()!)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : [];
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    internal static string HashBindingId(string bindingId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bindingId))).ToLowerInvariant();

    private sealed record TokenResponse
    {
        public string? AccessToken { get; init; }
        public string? RefreshToken { get; init; }
        public string? IdToken { get; init; }
        public string? BindingId { get; init; }
        public bool? BindingUpdated { get; init; }
        public string? Scope { get; init; }
        public string? TokenType { get; init; }
        public int? ExpiresIn { get; init; }
    }

    private sealed record TokenServiceGrant(
        bool Valid,
        bool AllowsEveryService,
        string[] ResourceUris,
        string[] AllowedServiceIds)
    {
        public static TokenServiceGrant Invalid { get; } = new(false, false, [], []);
    }

    private sealed record UserServiceCatalogResponse
    {
        public UserServiceCatalogItem[] Services { get; init; } = [];
    }

    private sealed record UserServiceCatalogItem
    {
        public string Id { get; init; } = string.Empty;
        public string ResourceUri { get; init; } = string.Empty;
    }

    private sealed record BindingIntrospectionResponse
    {
        public string BindingId { get; init; } = string.Empty;
        public string ClientId { get; init; } = string.Empty;
        public string NyxSubject { get; init; } = string.Empty;
        public bool Revoked { get; init; }
    }
}

/// <summary>
/// Result of a state-token decode call on the callback side.
/// </summary>
public sealed record CallbackStateDecode(
    bool Succeeded,
    string? CorrelationId,
    ExternalSubjectRef? ExternalSubject,
    string? PkceVerifier,
    string? ExpectedBindingHash,
    string? ErrorCode)
{
    public static CallbackStateDecode Ok(
        string correlationId,
        ExternalSubjectRef? subject,
        string verifier,
        string? expectedBindingHash = null) =>
        new(true, correlationId, subject, verifier, expectedBindingHash, null);

    public static CallbackStateDecode Failed(string errorCode) =>
        new(false, null, null, null, null, errorCode);
}
