using Aevatar.Configuration;
using Microsoft.Extensions.Configuration;

namespace Aevatar.Studio.Hosting.NyxId;

internal static class NyxIdApiEndpointResolver
{
    internal static string? ResolvePublicApiBaseUrl(IConfiguration configuration) =>
        NyxIdEndpointResolver.ResolvePublicApiBaseUrl(configuration);
}
