using Aevatar.BackendConsole.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Mainnet.Host.Api.AI;

internal static class AIPageEndpoints
{
    private const string PageRoute = "/ai";

    private static readonly BackendConsoleAsset PageAsset = new(
        LogicalName: "ai",
        Assembly: typeof(AIPageEndpoints).Assembly,
        ResourceSuffix: "AI.ai.html",
        ContentType: "text/html",
        InjectHostConfiguration: true,
        ConfigurationPlaceholder: "__AEVATAR_AI_CONFIG__",
        ConfigurationProfile: BackendConsoleAssetConfigurationProfile.AIAuthentication);

    public static IEndpointRouteBuilder MapAIPageEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapMethods(PageRoute, [HttpMethods.Get, HttpMethods.Head], GetAIPage)
            .WithTags("AI")
            .WithName("GetAIPage")
            .WithSummary("Aevatar AI application served from an embedded static asset.")
            .AllowAnonymous();

        return app;
    }

    internal static IResult GetAIPage(
        HttpContext http,
        [FromServices] IBackendConsoleAssetService assets)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(assets);
        return assets.Serve(PageAsset);
    }
}
