using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Aevatar.BackendConsole.Hosting;

/// <summary>
/// Serves a pre-rendered console asset with conditional-request and content-encoding
/// negotiation: matching <c>If-None-Match</c> short-circuits to 304, and Brotli/Gzip
/// variants are picked from <c>Accept-Encoding</c> q-values (ties prefer Brotli).
/// <c>Cache-Control: no-cache</c> keeps browsers revalidating so a redeploy is picked
/// up on the next load while unchanged repeats stay body-less.
/// </summary>
internal sealed class BackendConsoleAssetResult(BackendConsoleRenderedAsset asset) : IResult
{
    private readonly BackendConsoleRenderedAsset _asset = asset ?? throw new ArgumentNullException(nameof(asset));

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var response = httpContext.Response;
        response.Headers[HeaderNames.ETag] = _asset.ETag;
        response.Headers[HeaderNames.CacheControl] = "no-cache";
        response.Headers[HeaderNames.Vary] = HeaderNames.AcceptEncoding;

        if (IfNoneMatchSatisfied(httpContext.Request))
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        var (body, contentEncoding) = SelectRepresentation(httpContext.Request);
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = _asset.ContentType + "; charset=utf-8";
        if (contentEncoding is not null)
            response.Headers[HeaderNames.ContentEncoding] = contentEncoding;
        response.ContentLength = body.Length;

        if (HttpMethods.IsHead(httpContext.Request.Method))
            return;

        await response.Body.WriteAsync(body, httpContext.RequestAborted);
    }

    private bool IfNoneMatchSatisfied(HttpRequest request)
    {
        if (!EntityTagHeaderValue.TryParseList(request.Headers.IfNoneMatch, out var requestTags))
            return false;

        var assetTag = new EntityTagHeaderValue(_asset.ETag);
        return requestTags.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(assetTag, useStrongComparison: true));
    }

    private (byte[] Body, string? ContentEncoding) SelectRepresentation(HttpRequest request)
    {
        var brotliQuality = 0d;
        var gzipQuality = 0d;
        var wildcardQuality = 0d;
        var brotliListed = false;
        var gzipListed = false;

        if (StringWithQualityHeaderValue.TryParseList(request.Headers.AcceptEncoding, out var acceptedEncodings))
        {
            foreach (var accepted in acceptedEncodings)
            {
                var quality = accepted.Quality ?? 1d;
                if (accepted.Value.Equals("br", StringComparison.OrdinalIgnoreCase))
                {
                    brotliListed = true;
                    brotliQuality = quality;
                }
                else if (accepted.Value.Equals("gzip", StringComparison.OrdinalIgnoreCase))
                {
                    gzipListed = true;
                    gzipQuality = quality;
                }
                else if (accepted.Value.Equals("*", StringComparison.Ordinal))
                {
                    wildcardQuality = quality;
                }
            }
        }

        if (!brotliListed)
            brotliQuality = wildcardQuality;
        if (!gzipListed)
            gzipQuality = wildcardQuality;

        if (_asset.BrotliBytes is not null && brotliQuality > 0 && brotliQuality >= gzipQuality)
            return (_asset.BrotliBytes, "br");
        if (_asset.GzipBytes is not null && gzipQuality > 0)
            return (_asset.GzipBytes, "gzip");
        return (_asset.IdentityBytes, null);
    }
}
