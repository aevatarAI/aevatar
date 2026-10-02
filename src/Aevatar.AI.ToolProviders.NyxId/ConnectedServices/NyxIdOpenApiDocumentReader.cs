using Microsoft.Extensions.Logging;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

internal enum NyxIdOpenApiReadStage { AddressResolution, Fetch, Parse, OperationSelection }

internal enum NyxIdOpenApiDocumentSource { Gateway, Downstream }

internal enum NyxIdOpenApiReadFailure
{
    None,
    DocumentUrlMissing,
    InvalidDocumentUrl,
    GatewayOriginMismatch,
    GatewayServiceIdentityMismatch,
    EndpointUrlMissing,
    SpecOriginMismatch,
    UnsafeProxyPath,
    AuthenticationRequired,
    AccessDenied,
    NotFound,
    HttpError,
    TransportFailure,
    ResponseTooLarge,
    InvalidDocument,
    NoOperations,
    NoAdmissibleOperations,
}

/// <summary>Normalizes the two published document surfaces before parsing operation contracts.</summary>
internal sealed class NyxIdOpenApiDocumentReader(NyxIdApiClient client, ILogger logger)
{
    internal const int MaxDocumentBytes = 1024 * 1024;
    private static readonly TimeSpan FreshnessWindow = TimeSpan.FromMinutes(5);

    public async Task<IReadOnlyList<NyxIdMcpService>> ReadAsync(
        string accessToken,
        NyxIdServiceInstance instance,
        string sourceSuffix,
        CancellationToken ct)
    {
        var source = string.IsNullOrWhiteSpace(instance.OpenapiDocumentUrl)
            ? NyxIdOpenApiDocumentSource.Downstream
            : NyxIdOpenApiDocumentSource.Gateway;
        var failure = source == NyxIdOpenApiDocumentSource.Gateway
            ? ResolveGatewayServiceId(instance, out var target)
            : ResolveDownstreamPath(instance, out target);
        if (failure != NyxIdOpenApiReadFailure.None)
            return Failed(instance, source, NyxIdOpenApiReadStage.AddressResolution, failure);

        NyxIdProxyTextResponse response;
        try
        {
            response = source == NyxIdOpenApiDocumentSource.Gateway
                ? await client.GetServiceOpenApiDocumentAsync(accessToken, target, MaxDocumentBytes, ct).ConfigureAwait(false)
                : await client.ProxyRequestBoundedAsync(
                    accessToken, instance.DisplaySlug, instance.UserServiceId, target,
                    HttpMethod.Get.Method, body: null, extraHeaders: null, MaxDocumentBytes, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Failed(instance, source, NyxIdOpenApiReadStage.Fetch, NyxIdOpenApiReadFailure.TransportFailure);
        }

        if (!response.Succeeded)
        {
            failure = response.Detail is "content_length_exceeds_max_bytes" or "content_exceeds_max_bytes"
                ? NyxIdOpenApiReadFailure.ResponseTooLarge
                : response.HttpStatus switch
                {
                    401 => NyxIdOpenApiReadFailure.AuthenticationRequired,
                    403 => NyxIdOpenApiReadFailure.AccessDenied,
                    404 => NyxIdOpenApiReadFailure.NotFound,
                    0 => NyxIdOpenApiReadFailure.TransportFailure,
                    _ => NyxIdOpenApiReadFailure.HttpError,
                };
            return Failed(instance, source, NyxIdOpenApiReadStage.Fetch, failure, response.HttpStatus);
        }

        NyxIdMcpCatalogRead parsed;
        try
        {
            ct.ThrowIfCancellationRequested();
            parsed = NyxIdMcpOperationCatalog.ParseCustomOpenApi(
                response.Content, instance, sourceSuffix, DateTimeOffset.UtcNow, FreshnessWindow);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Failed(instance, source, NyxIdOpenApiReadStage.Parse, NyxIdOpenApiReadFailure.InvalidDocument, response.HttpStatus);
        }
        if (parsed.SourceUnavailable)
            return Failed(instance, source, NyxIdOpenApiReadStage.Parse, NyxIdOpenApiReadFailure.InvalidDocument, response.HttpStatus);

        foreach (var diagnostic in parsed.Discovery.Diagnostics)
        {
            logger.LogInformation(
                "NyxID OpenAPI operation diagnostic. userServiceId={UserServiceId} source={DocumentSource} code={DiagnosticCode} count={DiagnosticCount}",
                instance.UserServiceId, source, diagnostic.Code, diagnostic.Count);
        }

        if (parsed.Services.Count == 0)
        {
            return Failed(instance, source, NyxIdOpenApiReadStage.OperationSelection,
                parsed.Issues.Count == 0 ? NyxIdOpenApiReadFailure.NoOperations : NyxIdOpenApiReadFailure.NoAdmissibleOperations,
                response.HttpStatus);
        }

        logger.LogInformation(
            "NyxID OpenAPI contract read completed. userServiceId={UserServiceId} source={DocumentSource} operationCount={OperationCount}",
            instance.UserServiceId, source, parsed.Services.Sum(static service => service.Endpoints.Count));
        return parsed.Services;
    }

    private IReadOnlyList<NyxIdMcpService> Failed(
        NyxIdServiceInstance instance,
        NyxIdOpenApiDocumentSource source,
        NyxIdOpenApiReadStage stage,
        NyxIdOpenApiReadFailure failure,
        int httpStatus = 0)
    {
        // Never log document bodies, arbitrary URLs, tokens, or provider exception messages.
        logger.LogWarning(
            "NyxID OpenAPI contract read failed. stage={Stage} code={FailureCode} userServiceId={UserServiceId} catalogServiceSlug={CatalogServiceSlug} source={DocumentSource} httpStatus={HttpStatus}",
            stage, failure, instance.UserServiceId, instance.CatalogServiceSlug, source, httpStatus);
        return [];
    }

    private NyxIdOpenApiReadFailure ResolveGatewayServiceId(NyxIdServiceInstance instance, out string serviceId)
    {
        serviceId = string.Empty;
        var value = instance.OpenapiDocumentUrl.Trim();
        if (!IsSafePath(value) || !Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out var documentUri))
            return NyxIdOpenApiReadFailure.InvalidDocumentUrl;

        string path;
        if (documentUri.IsAbsoluteUri)
        {
            if (!IsHttp(documentUri) || documentUri.UserInfo.Length > 0 || documentUri.Query.Length > 0)
                return NyxIdOpenApiReadFailure.InvalidDocumentUrl;
            if (!client.IsPublicApiOrigin(documentUri))
                return NyxIdOpenApiReadFailure.GatewayOriginMismatch;
            path = documentUri.AbsolutePath;
        }
        else
        {
            if (!value.StartsWith("/", StringComparison.Ordinal) || value.Contains('?'))
                return NyxIdOpenApiReadFailure.InvalidDocumentUrl;
            path = value;
        }

        // Identity comes from inventory fields, never from a guessed slug or parsed ID.
        foreach (var candidate in new[] { instance.UserServiceId, instance.CatalogServiceId })
        {
            if (!string.IsNullOrWhiteSpace(candidate) &&
                string.Equals(path, NyxIdApiClient.BuildServiceOpenApiDocumentPath(candidate), StringComparison.Ordinal))
            {
                serviceId = candidate;
                return NyxIdOpenApiReadFailure.None;
            }
        }
        return NyxIdOpenApiReadFailure.GatewayServiceIdentityMismatch;
    }

    private static NyxIdOpenApiReadFailure ResolveDownstreamPath(NyxIdServiceInstance instance, out string path)
    {
        path = string.Empty;
        var value = instance.OpenapiSpecUrl.Trim();
        if (value.Length == 0)
            return NyxIdOpenApiReadFailure.DocumentUrlMissing;
        if (!IsSafePath(value))
            return NyxIdOpenApiReadFailure.UnsafeProxyPath;
        if (!Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out var specUri))
            return NyxIdOpenApiReadFailure.InvalidDocumentUrl;

        if (specUri.IsAbsoluteUri)
        {
            if (!IsHttp(specUri) || specUri.UserInfo.Length > 0)
                return NyxIdOpenApiReadFailure.InvalidDocumentUrl;
            if (!Uri.TryCreate(instance.EndpointUrl, UriKind.Absolute, out var endpointUri))
                return NyxIdOpenApiReadFailure.EndpointUrlMissing;
            if (!string.Equals(specUri.Scheme, endpointUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(specUri.Host, endpointUri.Host, StringComparison.OrdinalIgnoreCase) ||
                specUri.Port != endpointUri.Port)
                return NyxIdOpenApiReadFailure.SpecOriginMismatch;
            path = specUri.PathAndQuery;
        }
        else
        {
            path = value.StartsWith("/", StringComparison.Ordinal) ? value : "/" + value;
        }
        return NyxIdOpenApiReadFailure.None;
    }

    private static bool IsHttp(Uri uri) => uri.Scheme is "https" or "http";

    private static bool IsSafePath(string value)
    {
        if (value.StartsWith("//", StringComparison.Ordinal) || value.Contains('\\') || value.Contains('#') || value.Any(char.IsControl))
            return false;
        var resource = value.Split('?', 2)[0];
        var decoded = Uri.UnescapeDataString(resource);
        return !decoded.Contains('\\') && !decoded.Any(char.IsControl) &&
               !decoded.Split('/').Any(static segment => segment is "." or "..");
    }
}
