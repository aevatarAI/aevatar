using Microsoft.AspNetCore.Mvc;

namespace Aevatar.AppPlatform.Hosting.Endpoints;

internal static class AppPlatformEndpointModels
{
    internal sealed record AppListQuery(
        [FromQuery(Name = "ownerScopeId")] string? OwnerScopeId);

    internal sealed record ResolveRouteQuery(
        [FromQuery(Name = "routePath")] string? RoutePath);
}
