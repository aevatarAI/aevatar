using Aevatar.Tools.Cli.Studio.Application.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Tools.Cli.Hosting;

internal sealed class NyxIdStudioBackendRequestAuthSnapshotProvider : IStudioBackendRequestAuthSnapshotProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly NyxIdInternalRequestCredentials _internalCredentials;
    private readonly NyxIdAppTokenService _tokenService;

    public NyxIdStudioBackendRequestAuthSnapshotProvider(
        IHttpContextAccessor httpContextAccessor,
        NyxIdInternalRequestCredentials internalCredentials,
        NyxIdAppTokenService tokenService)
    {
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        _internalCredentials = internalCredentials ?? throw new ArgumentNullException(nameof(internalCredentials));
        _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
    }

    public async Task<StudioBackendRequestAuthSnapshot?> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null)
        {
            return null;
        }

        var localOrigin = httpContext.Request.Host.HasValue
            ? $"{httpContext.Request.Scheme}://{httpContext.Request.Host.Value}"
            : null;
        var isAuthenticated = httpContext.User.Identity?.IsAuthenticated == true;
        var bearerToken = await _tokenService.GetAccessTokenAsync(httpContext, cancellationToken);

        return new StudioBackendRequestAuthSnapshot(
            localOrigin,
            BearerToken: bearerToken,
            InternalAuthHeaderName: isAuthenticated ? NyxIdInternalRequestCredentials.HeaderName : null,
            InternalAuthToken: isAuthenticated ? _internalCredentials.Token : null);
    }
}
