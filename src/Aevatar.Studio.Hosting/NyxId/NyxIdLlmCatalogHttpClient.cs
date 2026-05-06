using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Hosting.NyxId;

public sealed class NyxIdLlmCatalogHttpClient : IUserLlmCatalogPort
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NyxIdLlmCatalogHttpClient> _logger;

    public NyxIdLlmCatalogHttpClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<NyxIdLlmCatalogHttpClient> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<NyxIdLlmServicesResult> GetServicesAsync(string bearerToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);

        var response = await SendNyxIdAsync(
            HttpMethod.Get,
            "/api/v1/llm/services",
            bearerToken,
            body: null,
            ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response = await SendNyxIdAsync(
                HttpMethod.Get,
                "/api/v1/llm/status",
                bearerToken,
                body: null,
                ct).ConfigureAwait(false);
        }

        EnsureSuccess(response, "NyxID LLM services");
        return NyxIdLlmServiceCatalogParser.ParseServicesResult(response.Body);
    }

    public async Task<NyxIdLlmService> ProvisionAsync(
        string bearerToken,
        string provisionEndpointId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        var normalizedEndpoint = NyxIdLlmServiceCatalogParser.NormalizeProvisionEndpointId(provisionEndpointId);

        var response = await SendNyxIdAsync(
            HttpMethod.Post,
            $"/api/v1/llm/services/{Uri.EscapeDataString(normalizedEndpoint)}",
            bearerToken,
            "{}",
            ct).ConfigureAwait(false);

        EnsureSuccess(response, "NyxID LLM service provisioning");
        return NyxIdLlmServiceCatalogParser.ParseProvisionedService(response.Body);
    }

    public string? ResolveGatewayUrl()
    {
        var authorityBase = ResolveNyxIdAuthorityBase();
        return string.IsNullOrWhiteSpace(authorityBase)
            ? null
            : $"{authorityBase}/api/v1/llm/gateway/v1";
    }

    private async Task<NyxIdHttpResult> SendNyxIdAsync(
        HttpMethod method,
        string path,
        string bearerToken,
        string? body,
        CancellationToken ct)
    {
        var authorityBase = ResolveNyxIdAuthorityBase();
        if (string.IsNullOrWhiteSpace(authorityBase))
            throw new InvalidOperationException("NyxID authority is not configured.");

        using var request = new HttpRequestMessage(method, $"{authorityBase}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new NyxIdHttpResult(response.StatusCode, responseBody);
    }

    private void EnsureSuccess(NyxIdHttpResult response, string operation)
    {
        if ((int)response.StatusCode is >= 200 and <= 299)
            return;

        _logger.LogWarning(
            "{Operation} endpoint returned {StatusCode}: {Body}",
            operation,
            response.StatusCode,
            response.Body.Length > 500 ? response.Body[..500] : response.Body);
        throw new InvalidOperationException($"{operation} request failed.");
    }

    private string? ResolveNyxIdAuthorityBase()
    {
        var authority = _configuration["Cli:App:NyxId:Authority"]
            ?? _configuration["Aevatar:NyxId:Authority"]
            ?? _configuration["Aevatar:Authentication:Authority"];

        if (string.IsNullOrWhiteSpace(authority))
            return null;

        var trimmed = authority.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out _))
            return null;

        const string gatewaySuffix = "/api/v1/llm/gateway/v1";
        return trimmed.EndsWith(gatewaySuffix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^gatewaySuffix.Length]
            : trimmed;
    }

    private readonly record struct NyxIdHttpResult(HttpStatusCode StatusCode, string Body);
}
