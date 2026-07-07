using Aevatar.BackendConsole.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Mainnet.Host.Api.Cqrs;

internal static class CqrsObservatoryPageEndpoints
{
    private const string PageRoute = "/cqrs";

    private static readonly BackendConsoleAsset PageAsset = new(
        LogicalName: "cqrs-observatory",
        Assembly: typeof(CqrsObservatoryPageEndpoints).Assembly,
        ResourceSuffix: "Cqrs.cqrs-observatory.html",
        ContentType: "text/html",
        InjectHostConfiguration: true);

    public static IEndpointRouteBuilder MapCqrsObservatoryPageEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(PageRoute, GetCqrsObservatoryPage)
            .WithTags("CqrsObservatory")
            .WithName("GetCqrsObservatoryPage")
            .WithSummary("CQRS / projection observatory served from an embedded static asset.")
            .AllowAnonymous();

        return app;
    }

    internal static IResult GetCqrsObservatoryPage(
        HttpContext http,
        [FromServices] IBackendConsoleAssetService assets)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(assets);
        return assets.Serve(PageAsset);
    }
}
