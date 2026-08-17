using Aevatar.BackendConsole.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Mainnet.Host.Api.BackendConsole;

internal static class DeliveryConsoleEndpoints
{
    private const string PageRoute = "/delivery";

    private static readonly BackendConsoleAsset PageAsset = new(
        LogicalName: "delivery",
        Assembly: typeof(DeliveryConsoleEndpoints).Assembly,
        ResourceSuffix: "BackendConsole.delivery.html",
        ContentType: "text/html",
        InjectHostConfiguration: true);

    public static IEndpointRouteBuilder MapDeliveryConsoleEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(PageRoute, GetDeliveryConsolePage)
            .WithTags("BackendConsole")
            .WithName("GetDeliveryConsolePage")
            .WithSummary("Workflow Delivery Center served from an embedded static asset.")
            .AllowAnonymous();

        return app;
    }

    internal static IResult GetDeliveryConsolePage(
        HttpContext http,
        [FromServices] IBackendConsoleAssetService assets)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(assets);
        return assets.Serve(PageAsset);
    }
}
