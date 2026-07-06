using Aevatar.BackendConsole.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Mainnet.Host.Api.Voice;

internal static class VoiceConsoleEndpoints
{
    private const string PageRoute = "/voice";
    private const string CallbackRoute = "/voice/callback";

    private static readonly BackendConsoleAsset PageAsset = new(
        LogicalName: "voice-console",
        Assembly: typeof(VoiceConsoleEndpoints).Assembly,
        ResourceSuffix: "Voice.voice-console.html",
        ContentType: "text/html",
        InjectHostConfiguration: true);

    public static IEndpointRouteBuilder MapVoiceConsoleEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(PageRoute, GetVoiceConsolePage)
            .WithTags("VoiceConsole")
            .WithName("GetVoiceConsolePage")
            .WithSummary("Voice presence console served from an embedded static asset.")
            .AllowAnonymous();

        // OIDC PKCE redirect target consumed by the page JS; same self-contained shell + login gate,
        // reusing the voice storageKey so no new NyxID redirect_uri registration is required beyond /voice/callback.
        app.MapGet(CallbackRoute, GetVoiceConsolePage)
            .WithTags("VoiceConsole")
            .WithName("GetVoiceConsoleCallback")
            .WithSummary("OIDC PKCE redirect target consumed by the voice console page JS.")
            .AllowAnonymous();

        return app;
    }

    internal static IResult GetVoiceConsolePage(
        HttpContext http,
        [FromServices] IBackendConsoleAssetService assets)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(assets);
        return assets.Serve(PageAsset);
    }
}
