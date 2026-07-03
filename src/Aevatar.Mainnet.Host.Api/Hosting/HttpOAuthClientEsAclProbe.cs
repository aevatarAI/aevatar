using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.GAgents.Channel.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Mainnet.Host.Api.Hosting;

/// <summary>
/// HTTP-backed <see cref="IOAuthClientEsAclProbe"/> that inspects the live
/// Elasticsearch cluster security API to determine whether the read grant on the
/// <c>aevatar-oauth-clients</c> index (state-token HMAC key material) is actually
/// restricted, instead of trusting a configuration flag.
/// </summary>
/// <remarks>
/// Reuses the SAME Elasticsearch endpoint + Basic-auth credentials the projection
/// document store uses — it binds the identical
/// <see cref="ElasticsearchProjectionConfiguration.SectionPath"/> options section
/// and builds its <see cref="HttpClient"/> the same way
/// <c>ElasticsearchProjectionDocumentStore</c> does (a raw HttpClient, no Elastic
/// SDK dependency). It only ever performs read-only security lookups:
/// <c>POST _security/user/_has_privileges</c> (confirms the security layer is
/// reachable and that the internal service identity can read the index) and
/// <c>GET _security/role_mapping</c> (enumerates the configured mappings for the
/// observed-state description). It classifies conservatively: a reachable,
/// authenticated security layer is reported as <see cref="EsAclProbeStatus.Restricted"/>;
/// only a cluster whose security is disabled (so the index has no ACL at all) is
/// reported as <see cref="EsAclProbeStatus.Unrestricted"/>; transport/auth errors
/// are <see cref="EsAclProbeStatus.Unverifiable"/> so the guard never crashes on
/// a probe failure.
/// </remarks>
public sealed class HttpOAuthClientEsAclProbe : IOAuthClientEsAclProbe, IDisposable
{
    private const string IndexName = AevatarOAuthClientDocumentMetadataProvider.IndexName;

    private readonly HttpClient _httpClient;
    private readonly bool _configured;
    private readonly ILogger<HttpOAuthClientEsAclProbe> _logger;

    public HttpOAuthClientEsAclProbe(
        ElasticsearchProjectionDocumentStoreOptions options,
        ILogger<HttpOAuthClientEsAclProbe>? logger = null,
        HttpMessageHandler? httpMessageHandler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? NullLogger<HttpOAuthClientEsAclProbe>.Instance;

        _httpClient = httpMessageHandler == null
            ? new HttpClient()
            : new HttpClient(httpMessageHandler, disposeHandler: true);

        var endpoint = ResolvePrimaryEndpoint(options.Endpoints);
        if (endpoint is null)
        {
            _configured = false;
            return;
        }

        _httpClient.BaseAddress = endpoint;
        _httpClient.Timeout = TimeSpan.FromMilliseconds(Math.Max(500, options.RequestTimeoutMs));
        if (!string.IsNullOrWhiteSpace(options.Username))
        {
            var raw = $"{options.Username}:{options.Password}";
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }

        _configured = true;
    }

    public async Task<EsAclProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_configured)
        {
            return EsAclProbeResult.Unavailable(
                "Elasticsearch endpoint is not configured; cannot probe the OAuth-clients index ACL.");
        }

        try
        {
            var hasPrivileges = await ProbeHasPrivilegesAsync(cancellationToken);
            if (hasPrivileges.Status is EsAclProbeStatus.Unrestricted or EsAclProbeStatus.Unverifiable)
            {
                // Security disabled or unreachable — the has-privileges call already
                // gives the definitive classification; skip role-mapping enumeration.
                return hasPrivileges;
            }

            var roleMappingSummary = await ProbeRoleMappingsAsync(cancellationToken);
            return EsAclProbeResult.Restricted(
                $"Elasticsearch security API is reachable and enforcing; internal service identity has read on '{IndexName}'. {roleMappingSummary}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "AevatarOAuthClient ES ACL probe failed to reach the Elasticsearch security API. index={IndexName} errorType={ErrorType}",
                IndexName,
                ex.GetType().Name);
            return EsAclProbeResult.Unverifiable(
                $"Elasticsearch security API probe failed ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    private async Task<EsAclProbeResult> ProbeHasPrivilegesAsync(CancellationToken cancellationToken)
    {
        var payload =
            $$"""
            {"index":[{"names":["{{IndexName}}"],"privileges":["read"]}]}
            """;
        using var request = new HttpRequestMessage(HttpMethod.Post, "_security/user/_has_privileges")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (IsSecurityNotEnabled(response.StatusCode, body))
        {
            return EsAclProbeResult.Unrestricted(
                $"Elasticsearch security is not enabled (status={(int)response.StatusCode}); the '{IndexName}' index has no index-level read ACL.");
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return EsAclProbeResult.Unverifiable(
                $"Elasticsearch security API returned {(int)response.StatusCode} for _has_privileges; cannot classify the '{IndexName}' read grant.");
        }

        if (!response.IsSuccessStatusCode)
        {
            return EsAclProbeResult.Unverifiable(
                $"Elasticsearch _has_privileges returned {(int)response.StatusCode}; cannot classify the '{IndexName}' read grant.");
        }

        var hasRead = TryReadIndexHasRead(body);
        // Security layer is reachable and authenticated. Whether the internal
        // identity itself has read or not, the presence of an enforcing security
        // layer means the index is not world-readable at the cluster level.
        return EsAclProbeResult.Restricted(
            $"_has_privileges succeeded (indexReadGranted={hasRead}).");
    }

    private async Task<string> ProbeRoleMappingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync("_security/role_mapping", cancellationToken);
            if (!response.IsSuccessStatusCode)
                return $"role_mapping lookup returned {(int)response.StatusCode}.";

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var jsonDoc = JsonDocument.Parse(body);
            if (jsonDoc.RootElement.ValueKind != JsonValueKind.Object)
                return "role_mapping response had no mappings.";

            var names = jsonDoc.RootElement
                .EnumerateObject()
                .Select(static property => property.Name)
                .ToArray();
            return names.Length == 0
                ? "no security role mappings are configured."
                : $"{names.Length} security role mapping(s) configured: [{string.Join(", ", names)}].";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Role-mapping enumeration is descriptive-only (it enriches the observed
            // state, it does not drive the classification), so a failure here is
            // logged and folded into the returned description rather than propagated.
            _logger.LogWarning(
                ex,
                "AevatarOAuthClient ES ACL probe could not enumerate Elasticsearch role mappings. index={IndexName} errorType={ErrorType}",
                IndexName,
                ex.GetType().Name);
            return $"role_mapping lookup failed ({ex.GetType().Name}).";
        }
    }

    private static bool IsSecurityNotEnabled(HttpStatusCode statusCode, string body)
    {
        // Elasticsearch returns 400 Bad Request with a "Security must be explicitly
        // enabled" / "no handler found" style error when X-Pack security is off, so
        // the _security endpoints are unavailable and the index carries no ACL.
        if (statusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.NotFound))
            return false;

        if (string.IsNullOrEmpty(body))
            return true;

        return body.Contains("security", StringComparison.OrdinalIgnoreCase) &&
               (body.Contains("not enabled", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("must be explicitly enabled", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("no handler found", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("disabled", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryReadIndexHasRead(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;

        try
        {
            using var jsonDoc = JsonDocument.Parse(body);
            if (!jsonDoc.RootElement.TryGetProperty("index", out var indexNode) ||
                indexNode.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!indexNode.TryGetProperty(IndexName, out var indexPrivileges) ||
                indexPrivileges.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return indexPrivileges.TryGetProperty("read", out var readNode) &&
                   readNode.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static Uri? ResolvePrimaryEndpoint(IReadOnlyList<string>? endpoints)
    {
        if (endpoints is null || endpoints.Count == 0)
            return null;

        var endpoint = endpoints[0].Trim();
        if (endpoint.Length == 0)
            return null;
        if (!endpoint.Contains("://", StringComparison.Ordinal))
            endpoint = "http://" + endpoint;

        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri : null;
    }

    public void Dispose() => _httpClient.Dispose();
}
