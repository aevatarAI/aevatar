using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

// /channels onboarding surface. Mirrors the workflow-studio endpoint shape: an anonymous
// self-contained static shell whose in-page JS gates the app behind a nyxid bearer login,
// then calls the live channel-registration facade + user LLM config — both served by this
// same (Mainnet) host, so every in-page call is same-origin. There is intentionally NO
// /channels/callback route: NyxID rejects unregistered redirect_uris (and it's external,
// so we can't register one), so the page reuses studio's already-registered
// /workflow/studio/callback and shares its token storage.
public static class ChannelsEndpoints
{
    private const string PageRoute = "/channels";

    public static IEndpointRouteBuilder MapChannels(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(PageRoute, GetChannelsPage)
            .WithTags("Channels")
            .WithName("GetChannelsPage")
            .WithSummary("Lark bot onboarding (inline self-contained page).")
            .AllowAnonymous();

        return app;
    }

    internal static IResult GetChannelsPage(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return Results.Text(ChannelsPage.Html, "text/html", Encoding.UTF8);
    }
}
