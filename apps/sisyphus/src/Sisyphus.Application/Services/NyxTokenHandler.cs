using System.Net.Http.Headers;

namespace Sisyphus.Application.Services;

/// <summary>
/// DelegatingHandler that uses NyxIdTokenService for Bearer token auth.
/// Each HTTP call site gets its own handler instance, but the underlying
/// token cache is shared via the singleton NyxIdTokenService.
/// </summary>
internal sealed class NyxTokenHandler(NyxIdTokenService tokenService)
    : DelegatingHandler(new HttpClientHandler())
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await tokenService.GetAccessTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }
}
