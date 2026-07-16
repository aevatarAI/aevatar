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
/// <c>POST _security/user/_has_privileges</c> confirms whether the configured service
/// identity can read the index and whether Elasticsearch security is enabled. That API does
/// not prove that other users or wildcard roles cannot read the index, so a successful probe
/// remains <see cref="EsAclProbeStatus.Unverifiable"/> until an effective-privilege audit is
/// available. A cluster whose security is disabled is <see cref="EsAclProbeStatus.Unrestricted"/>.
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
            return await ProbeHasPrivilegesAsync(cancellationToken);
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
        return EsAclProbeResult.Unverifiable(
            $"Elasticsearch security is enabled and the configured identity has read={hasRead} " +
            $"on '{IndexName}', but _has_privileges does not prove that wildcard roles or other identities are denied.");
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
