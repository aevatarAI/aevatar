using Aevatar.BackendConsole.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Mainnet.Host.Api.BackendConsole;

internal static class AutoConsoleCallbackEndpoints
{
    private const string CallbackRoute = "/auto/callback";

    private static readonly BackendConsoleAsset PageAsset = new(
        LogicalName: "auto-callback",
        Assembly: typeof(AutoConsoleCallbackEndpoints).Assembly,
        ResourceSuffix: "BackendConsole.auto-callback.html",
        ContentType: "text/html",
        InjectHostConfiguration: true);

    public static IEndpointRouteBuilder MapAutoConsoleCallbackEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(CallbackRoute, GetAutoConsoleCallbackPage)
            .WithTags("BackendConsole")
            .WithName("GetAutoConsoleCallback")
            .WithSummary("Shared OIDC PKCE redirect target for the unified backend console suite.")
            .AllowAnonymous();

        return app;
    }

    internal static IResult GetAutoConsoleCallbackPage(
        HttpContext http,
        [FromServices] IBackendConsoleAssetService assets)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(assets);
        return assets.Serve(PageAsset);
    }
}
