using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Calls NyxID's RFC 7591 dynamic client registration endpoint
/// (<c>POST /oauth/register</c>) to provision aevatar's OAuth public client.
/// Unauthenticated; NyxID accepts open self-registration with
/// <c>token_endpoint_auth_method=none</c> for public clients.
/// </summary>
public sealed class NyxIdDynamicClientRegistrationClient
{
    public const string RegisterEndpoint = "/oauth/register";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly ILogger<NyxIdDynamicClientRegistrationClient> _logger;

    public NyxIdDynamicClientRegistrationClient(
        HttpClient http,
        ILogger<NyxIdDynamicClientRegistrationClient> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Registers a public OAuth client at <paramref name="authority"/>. Returns
    /// the issued <c>client_id</c> + issuance timestamp. Throws when the
    /// registration call fails (HTTP non-success, malformed body, missing
    /// client_id) so the bootstrap caller can decide whether to retry.
    /// </summary>
    public async Task<RegistrationResult> RegisterPublicClientAsync(
        string authority,
        string clientName,
        string redirectUri,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authority);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);

        var url = $"{authority.TrimEnd('/')}{RegisterEndpoint}";
        var request = new RegistrationRequest
        {
            ClientName = clientName,
            RedirectUris = [redirectUri],
            GrantTypes = ["authorization_code"],
            ResponseTypes = ["code"],
            TokenEndpointAuthMethod = "none",
            Scope = "openid urn:nyxid:scope:broker_binding",
        };

        using var response = await _http.PostAsJsonAsync(url, request, JsonOptions, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogError(
                "NyxID DCR failed: status={StatusCode}, body={Body}",
                (int)response.StatusCode,
                Truncate(body, 256));
            response.EnsureSuccessStatusCode();
        }

        var registration = await response.Content
            .ReadFromJsonAsync<RegistrationResponse>(JsonOptions, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("NyxID DCR returned an empty response.");

        if (string.IsNullOrWhiteSpace(registration.ClientId))
            throw new InvalidOperationException("NyxID DCR response did not include a client_id.");

        var issuedAt = registration.ClientIdIssuedAt > 0
            ? DateTimeOffset.FromUnixTimeSeconds(registration.ClientIdIssuedAt)
            : DateTimeOffset.UtcNow;

        _logger.LogInformation(
            "Registered aevatar OAuth client at NyxID: client_id={ClientId}, authority={Authority}",
            registration.ClientId,
            authority);

        return new RegistrationResult(registration.ClientId, issuedAt);
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    public sealed record RegistrationResult(string ClientId, DateTimeOffset IssuedAt);

    private sealed record RegistrationRequest
    {
        public string ClientName { get; init; } = string.Empty;
        public string[] RedirectUris { get; init; } = Array.Empty<string>();
        public string[] GrantTypes { get; init; } = Array.Empty<string>();
        public string[] ResponseTypes { get; init; } = Array.Empty<string>();
        public string TokenEndpointAuthMethod { get; init; } = "none";
        public string? Scope { get; init; }
    }

    private sealed record RegistrationResponse
    {
        public string? ClientId { get; init; }
        public string? ClientName { get; init; }
        public string[]? RedirectUris { get; init; }
        public string[]? GrantTypes { get; init; }
        public string[]? ResponseTypes { get; init; }
        public string? TokenEndpointAuthMethod { get; init; }
        public string? Scope { get; init; }
        public long ClientIdIssuedAt { get; init; }
    }
}
