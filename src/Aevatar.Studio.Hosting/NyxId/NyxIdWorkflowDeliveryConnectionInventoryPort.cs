using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ApplicationCredentialSourceKind = Aevatar.Studio.Application.Studio.Abstractions.NyxIdInventoryCredentialSourceKind;
using ApplicationCredentialStatus = Aevatar.Studio.Application.Studio.Abstractions.NyxIdInventoryCredentialStatus;
using NyxIdCredentialSourceKind = Aevatar.AI.ToolProviders.NyxId.NyxIdUserServiceCredentialSourceKind;
using NyxIdCredentialStatus = Aevatar.AI.ToolProviders.NyxId.NyxIdUserServiceCredentialStatus;
using NyxIdNodeStatus = Aevatar.AI.ToolProviders.NyxId.NyxIdUserServiceNodeStatus;

namespace Aevatar.Studio.Hosting.NyxId;

internal sealed class NyxIdWorkflowDeliveryConnectionInventoryPort : INyxIdUserServiceInventoryPort
{
    internal const string ScopeKeysPath = "/api/v1/keys";
    internal const int MaxResponseBodyBytes = 4 * 1024 * 1024;
    internal static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(15);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NyxIdWorkflowDeliveryConnectionInventoryPort> _logger;
    private readonly TimeSpan _sourceTimeout;

    public NyxIdWorkflowDeliveryConnectionInventoryPort(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<NyxIdWorkflowDeliveryConnectionInventoryPort> logger)
        : this(httpClientFactory, configuration, logger, SourceTimeout)
    {
    }

    internal NyxIdWorkflowDeliveryConnectionInventoryPort(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<NyxIdWorkflowDeliveryConnectionInventoryPort> logger,
        TimeSpan sourceTimeout)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (sourceTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(sourceTimeout));
        _sourceTimeout = sourceTimeout;
    }

    public async Task<IReadOnlyList<NyxIdUserServiceInventoryItem>> ListAsync(
        string bearerToken,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_sourceTimeout);

        try
        {
            var apiBaseUrl = NyxIdApiEndpointResolver.ResolvePublicApiBaseUrl(_configuration);
            if (string.IsNullOrWhiteSpace(apiBaseUrl))
                throw new InvalidOperationException("NyxID public API base URL is not configured.");

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBaseUrl}{ScopeKeysPath}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken.Trim());
            var client = _httpClientFactory.CreateClient();
            using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "NyxID UserService inventory endpoint returned {StatusCode}",
                    response.StatusCode);
                throw RequestRejected(response.StatusCode);
            }

            var body = await ReadBoundedBodyAsync(response.Content, timeout.Token).ConfigureAwait(false);
            var parsed = NyxIdApiAccessResponseParser.ParseUserServiceKeys(body);
            if (!parsed.Succeeded)
                throw MapFailure(parsed.Failure);

            return parsed.Value!.Services.Select(Map).ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested)
        {
            throw new NyxIdUserServiceInventoryException(
                NyxIdUserServiceInventoryFailureKind.Unavailable,
                "NyxID UserService inventory request timed out.",
                ex);
        }
        catch (NyxIdUserServiceInventoryException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new NyxIdUserServiceInventoryException(
                NyxIdUserServiceInventoryFailureKind.Unavailable,
                "NyxID UserService inventory is temporarily unavailable.",
                ex);
        }
    }

    private static async Task<string> ReadBoundedBodyAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaxResponseBodyBytes)
            throw ResponseTooLarge();

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
                throw ResponseTooLarge();

            body.Write(buffer, 0, bytesRead);
        }

        return Encoding.UTF8.GetString(body.GetBuffer(), 0, totalBytes);
    }

    private static NyxIdUserServiceInventoryItem Map(NyxIdUserServiceKey service) =>
        new(
            service.Id,
            service.Slug,
            service.CatalogServiceSlug,
            service.Label,
            service.IsActive,
            service.CredentialSource.Kind switch
            {
                NyxIdCredentialSourceKind.Personal => ApplicationCredentialSourceKind.Personal,
                NyxIdCredentialSourceKind.Organization => ApplicationCredentialSourceKind.Organization,
                _ => ApplicationCredentialSourceKind.Unspecified,
            },
            service.CredentialSource.Allowed,
            service.CredentialStatus switch
            {
                NyxIdCredentialStatus.Active => ApplicationCredentialStatus.Active,
                NyxIdCredentialStatus.Expired => ApplicationCredentialStatus.Expired,
                NyxIdCredentialStatus.Revoked => ApplicationCredentialStatus.Revoked,
                NyxIdCredentialStatus.Failed => ApplicationCredentialStatus.Failed,
                NyxIdCredentialStatus.RefreshFailed => ApplicationCredentialStatus.RefreshFailed,
                NyxIdCredentialStatus.PendingAuthorization => ApplicationCredentialStatus.PendingAuthorization,
                _ => ApplicationCredentialStatus.Unspecified,
            },
            service.NodeId,
            service.NodeStatus switch
            {
                NyxIdNodeStatus.NotBound => NyxIdInventoryNodeStatus.NotBound,
                NyxIdNodeStatus.Online => NyxIdInventoryNodeStatus.Online,
                NyxIdNodeStatus.Offline => NyxIdInventoryNodeStatus.Offline,
                NyxIdNodeStatus.Draining => NyxIdInventoryNodeStatus.Draining,
                NyxIdNodeStatus.Unknown => NyxIdInventoryNodeStatus.Unknown,
                NyxIdNodeStatus.Inaccessible => NyxIdInventoryNodeStatus.Inaccessible,
                _ => NyxIdInventoryNodeStatus.Unspecified,
            },
            service.Connected);

    private static NyxIdUserServiceInventoryException MapFailure(NyxIdApiAccessFailure? failure) =>
        new(
            failure?.Kind switch
            {
                NyxIdApiAccessFailureKind.Unauthorized =>
                    NyxIdUserServiceInventoryFailureKind.AuthenticationRejected,
                NyxIdApiAccessFailureKind.Forbidden =>
                    NyxIdUserServiceInventoryFailureKind.Forbidden,
                NyxIdApiAccessFailureKind.RateLimited =>
                    NyxIdUserServiceInventoryFailureKind.RateLimited,
                NyxIdApiAccessFailureKind.MalformedResponse =>
                    NyxIdUserServiceInventoryFailureKind.ResponseInvalid,
                _ => NyxIdUserServiceInventoryFailureKind.Unavailable,
            },
            "NyxID UserService inventory could not be read.");

    private static NyxIdUserServiceInventoryException RequestRejected(HttpStatusCode statusCode) =>
        new(
            statusCode switch
            {
                HttpStatusCode.Unauthorized => NyxIdUserServiceInventoryFailureKind.AuthenticationRejected,
                HttpStatusCode.Forbidden => NyxIdUserServiceInventoryFailureKind.Forbidden,
                HttpStatusCode.TooManyRequests => NyxIdUserServiceInventoryFailureKind.RateLimited,
                _ => NyxIdUserServiceInventoryFailureKind.Unavailable,
            },
            $"NyxID UserService inventory request failed with status {(int)statusCode}.");

    private static NyxIdUserServiceInventoryException ResponseTooLarge() =>
        new(
            NyxIdUserServiceInventoryFailureKind.Unavailable,
            $"NyxID UserService inventory response exceeded {MaxResponseBodyBytes} bytes.");
}
