using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Mainnet.Host.Api.BackendConsole;

// Mounts the single shared /auto/callback OIDC redirect target for the unified backend console suite
// (observatory / studio / schedules / channels / voice). Served anonymously as a self-contained static
// shell; the in-page JS performs the PKCE authorization-code exchange and redirects back to the
// originating console page. No data endpoints are introduced here.
internal static class AutoConsoleCallbackEndpoints
{
    private const string CallbackRoute = "/auto/callback";

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

    internal static IResult GetAutoConsoleCallbackPage(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return Results.Text(AutoConsoleCallbackPage.Html, "text/html", Encoding.UTF8);
    }
}
