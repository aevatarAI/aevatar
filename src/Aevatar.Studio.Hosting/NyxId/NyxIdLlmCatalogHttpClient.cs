using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.LlmCatalog;
using Aevatar.Foundation.Abstractions.Helpers;
using Aevatar.Studio.Application.Studio.Abstractions;
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
        var result = NyxIdLlmServiceCatalogParser.ParseServicesResult(response.Body);
        result = await MergeUserKeyRouteCandidatesAsync(result, bearerToken, ct).ConfigureAwait(false);
        result = await MergeProxyRouteCandidatesAsync(result, bearerToken, ct).ConfigureAwait(false);
        result = await ComposeUserServiceInventoryAsync(result, bearerToken, ct).ConfigureAwait(false);
        return await MergeObservedProxyModelsAsync(result, bearerToken, ct).ConfigureAwait(false);
    }

    public Task<NyxIdLlmServicesResult> GetFreshServicesAsync(string bearerToken, CancellationToken ct) =>
        GetServicesAsync(bearerToken, ct);

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
        var apiBaseUrl = ResolveNyxIdApiBaseUrl();
        return string.IsNullOrWhiteSpace(apiBaseUrl)
            ? null
            : $"{apiBaseUrl}/api/v1/llm/gateway/v1";
    }

    private async Task<NyxIdHttpResult> SendNyxIdAsync(
        HttpMethod method,
        string path,
        string bearerToken,
        string? body,
        CancellationToken ct)
    {
        var apiBaseUrl = ResolveNyxIdApiBaseUrl();
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
            throw new InvalidOperationException("NyxID public API base URL is not configured.");

        using var request = new HttpRequestMessage(method, $"{apiBaseUrl}{path}");
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

        var scrubbedBody = SecretScrubber.Scrub(response.Body);
        _logger.LogWarning(
            "{Operation} endpoint returned {StatusCode}: {Body}",
            operation,
            response.StatusCode,
            scrubbedBody.Length > 500 ? scrubbedBody[..500] : scrubbedBody);
        throw new InvalidOperationException($"{operation} request failed.");
    }

    private async Task<NyxIdLlmServicesResult> MergeProxyRouteCandidatesAsync(
        NyxIdLlmServicesResult result,
        string bearerToken,
        CancellationToken ct)
    {
        try
        {
            var response = await SendNyxIdAsync(
                HttpMethod.Get,
                NyxIdLlmCatalogRoutes.ProxyServicesPath,
                bearerToken,
                body: null,
                ct).ConfigureAwait(false);
            if ((int)response.StatusCode is < 200 or > 299)
            {
                var scrubbedBody = SecretScrubber.Scrub(response.Body);
                _logger.LogWarning(
                    "NyxID proxy services endpoint returned {StatusCode}: {Body}",
                    response.StatusCode,
                    scrubbedBody.Length > 500 ? scrubbedBody[..500] : scrubbedBody);
                return result;
            }

            return NyxIdLlmServiceCatalogParser.MergeProxyRouteCandidates(result, response.Body);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to merge NyxID proxy services into LLM route catalog");
            return result;
        }
    }

    private async Task<NyxIdLlmServicesResult> MergeUserKeyRouteCandidatesAsync(
        NyxIdLlmServicesResult result,
        string bearerToken,
        CancellationToken ct)
    {
        try
        {
            var response = await SendNyxIdAsync(
                HttpMethod.Get,
                NyxIdLlmCatalogRoutes.UserKeysPath,
                bearerToken,
                body: null,
                ct).ConfigureAwait(false);
            if ((int)response.StatusCode is < 200 or > 299)
            {
                var scrubbedBody = SecretScrubber.Scrub(response.Body);
                _logger.LogWarning(
                    "NyxID user keys endpoint returned {StatusCode}: {Body}",
                    response.StatusCode,
                    scrubbedBody.Length > 500 ? scrubbedBody[..500] : scrubbedBody);
                return result;
            }

            return NyxIdLlmServiceCatalogParser.MergeUserKeyRouteCandidates(result, response.Body);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to merge NyxID user keys into LLM route catalog");
            return result;
        }
    }

    private async Task<NyxIdLlmServicesResult> ComposeUserServiceInventoryAsync(
        NyxIdLlmServicesResult diagnostics,
        string bearerToken,
        CancellationToken ct)
    {
        var response = await SendNyxIdAsync(
            HttpMethod.Get,
            "/api/v1/user-services",
            bearerToken,
            body: null,
            ct).ConfigureAwait(false);
        EnsureSuccess(response, "NyxID user services inventory");

        var inventory = NyxIdApiAccessResponseParser.ParseUserServices(response.Body);
        if (!inventory.Succeeded)
        {
            throw new InvalidOperationException(
                $"NyxID user services inventory was rejected: {inventory.Failure?.Code ?? "unknown"}.");
        }

        return NyxIdLlmServiceCatalogParser.ComposeUserServiceInventory(diagnostics, inventory.Value!);
    }

    private async Task<NyxIdLlmServicesResult> MergeObservedProxyModelsAsync(
        NyxIdLlmServicesResult result,
        string bearerToken,
        CancellationToken ct)
    {
        foreach (var service in result.Services.Where(NyxIdLlmServiceCatalogParser.ShouldProbeProxyModels))
        {
            result = await MergeObservedProxyModelsAsync(result, service, bearerToken, ct).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<NyxIdLlmServicesResult> MergeObservedProxyModelsAsync(
        NyxIdLlmServicesResult result,
        NyxIdLlmService service,
        string bearerToken,
        CancellationToken ct)
    {
        try
        {
            if (service.Identity is not
                {
                    Authority: UserLlmIdentityAuthority.NyxIdUserServicesInventory,
                    NyxIdUserServiceId: var userServiceId,
                } identity || string.IsNullOrWhiteSpace(userServiceId))
                return result;

            var path = $"/api/v1/proxy/s/{Uri.EscapeDataString(service.ServiceSlug)}/models" +
                       $"?_nyxid_via={Uri.EscapeDataString(userServiceId.Trim())}";

            var response = await SendNyxIdAsync(
                HttpMethod.Get,
                path,
                bearerToken,
                body: null,
                ct).ConfigureAwait(false);
            if ((int)response.StatusCode is < 200 or > 299)
            {
                var scrubbedBody = SecretScrubber.Scrub(response.Body);
                _logger.LogWarning(
                    "NyxID proxy models endpoint for {ServiceSlug} returned {StatusCode}: {Body}",
                    service.ServiceSlug,
                    response.StatusCode,
                    scrubbedBody.Length > 500 ? scrubbedBody[..500] : scrubbedBody);
                return result;
            }

            var observedCatalog = NyxIdLlmServiceCatalogParser.ParseOpenAIModelsResponse(response.Body);
            return NyxIdLlmServiceCatalogParser.MergeObservedModelCatalog(result, identity, observedCatalog);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to merge NyxID proxy models for {ServiceSlug}", service.ServiceSlug);
            return result;
        }
    }

    private string? ResolveNyxIdApiBaseUrl()
    {
        return NyxIdApiEndpointResolver.ResolvePublicApiBaseUrl(_configuration);
    }

    private readonly record struct NyxIdHttpResult(HttpStatusCode StatusCode, string Body);
}
