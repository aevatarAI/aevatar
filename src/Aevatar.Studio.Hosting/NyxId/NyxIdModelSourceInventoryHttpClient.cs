using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Hosting.NyxId;

internal sealed class NyxIdModelSourceInventoryHttpClient : INyxIdModelSourceInventoryPort
{
    internal const string PlatformCatalogServicesPath = "/api/v1/services";
    internal const string ScopeKeysPath = "/api/v1/keys";
    internal const int MaxResponseBodyBytes = 4 * 1024 * 1024;
    internal static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(15);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NyxIdModelSourceInventoryHttpClient> _logger;

    public NyxIdModelSourceInventoryHttpClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<NyxIdModelSourceInventoryHttpClient> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<NyxIdPlatformModelSourceInventory> GetPlatformCatalogServicesAsync(
        string bearerToken,
        CancellationToken ct)
    {
        var body = await GetAsync(
            PlatformCatalogServicesPath,
            bearerToken,
            "NyxID platform service inventory",
            ct).ConfigureAwait(false);
        return NyxIdModelSourceInventoryParser.ParsePlatformCatalogServices(body);
    }

    public async Task<NyxIdScopeModelSourceInventory> GetScopeModelSourcesAsync(
        string bearerToken,
        CancellationToken ct)
    {
        var body = await GetAsync(
            ScopeKeysPath,
            bearerToken,
            "NyxID scope key inventory",
            ct).ConfigureAwait(false);
        return NyxIdModelSourceInventoryParser.ParseScopeKeys(body);
    }

    private async Task<string> GetAsync(
        string path,
        string bearerToken,
        string operation,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);

        var apiBaseUrl = NyxIdApiEndpointResolver.ResolvePublicApiBaseUrl(_configuration);
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
            throw new InvalidOperationException("NyxID public API base URL is not configured.");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBaseUrl}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SourceTimeout);
        try
        {
            var client = _httpClientFactory.CreateClient();
            using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "{Operation} endpoint returned {StatusCode}",
                    operation,
                    response.StatusCode);
                throw RequestRejected(operation, response.StatusCode);
            }

            return await ReadBoundedBodyAsync(
                    response.Content,
                    operation,
                    response.StatusCode,
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested)
        {
            throw new NyxIdModelSourceInventoryException(
                NyxIdModelSourceInventoryFailureKind.Unavailable,
                statusCode: null,
                $"{operation} request timed out.",
                ex);
        }
    }

    private static async Task<string> ReadBoundedBodyAsync(
        HttpContent content,
        string operation,
        HttpStatusCode statusCode,
        CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaxResponseBodyBytes)
            throw ResponseTooLarge(operation, statusCode);

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = new byte[81920];
        var totalBytes = 0;
        while (true)
        {
            var maximumRead = Math.Min(buffer.Length, MaxResponseBodyBytes - totalBytes + 1);
            var bytesRead = await stream
                .ReadAsync(buffer.AsMemory(0, maximumRead), ct)
                .ConfigureAwait(false);
            if (bytesRead == 0)
                break;

            totalBytes += bytesRead;
            if (totalBytes > MaxResponseBodyBytes)
                throw ResponseTooLarge(operation, statusCode);

            body.Write(buffer, 0, bytesRead);
        }

        return Encoding.UTF8.GetString(body.GetBuffer(), 0, totalBytes);
    }

    private static NyxIdModelSourceInventoryException RequestRejected(
        string operation,
        HttpStatusCode statusCode) =>
        new(
            statusCode switch
            {
                HttpStatusCode.Unauthorized =>
                    NyxIdModelSourceInventoryFailureKind.AuthenticationRejected,
                HttpStatusCode.Forbidden => NyxIdModelSourceInventoryFailureKind.Forbidden,
                _ => NyxIdModelSourceInventoryFailureKind.Unavailable,
            },
            statusCode,
            $"{operation} request failed with status {(int)statusCode}.");

    private static NyxIdModelSourceInventoryException ResponseTooLarge(
        string operation,
        HttpStatusCode statusCode) =>
        new(
            NyxIdModelSourceInventoryFailureKind.Unavailable,
            statusCode,
            $"{operation} response exceeded {MaxResponseBodyBytes} bytes.");
}
