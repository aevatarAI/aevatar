using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Mainnet.Host.Api.Voice;

// Voice console page mount. Mirrors the /workflow/studio + /workflow/observatory precedent: the page
// (and its OIDC PKCE callback target) is served anonymously as a self-contained static shell; the in-page
// JS gates the app behind a nyxid bearer login exactly like its siblings. The live wiring reuses existing
// APIs (GET /api/studio/context for the scope chip, POST .../voice-presence/enable for the one real write)
// — no new backend surface is introduced here, and no data endpoints are mapped from this file.
internal static class VoiceConsoleEndpoints
{
    private const string PageRoute = "/voice";
    private const string CallbackRoute = "/voice/callback";

    public static IEndpointRouteBuilder MapVoiceConsoleEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(PageRoute, GetVoiceConsolePage)
            .WithTags("VoiceConsole")
            .WithName("GetVoiceConsolePage")
            .WithSummary("Voice presence console (inline self-contained page).")
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

    internal static IResult GetVoiceConsolePage(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return Results.Text(VoiceConsolePage.Html, "text/html", Encoding.UTF8);
    }
}
