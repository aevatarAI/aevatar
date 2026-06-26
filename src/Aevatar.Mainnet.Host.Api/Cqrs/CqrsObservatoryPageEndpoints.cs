using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Mainnet.Host.Api.Cqrs;

// CQRS / projection observatory page mount. Mirrors the /voice + /workflow/studio + /workflow/observatory
// precedent: the page is served anonymously as a self-contained static shell; the in-page JS gates the app
// behind a nyxid bearer login exactly like its siblings, and carries the shared suite navigation so the six
// console pages move as one. The live data surface (GET /api/cqrs/scopes) is mapped separately by
// CqrsObservatoryApiEndpoints; no data endpoint is mapped from this file.
internal static class CqrsObservatoryPageEndpoints
{
    private const string PageRoute = "/cqrs";

    public static IEndpointRouteBuilder MapCqrsObservatoryPageEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(PageRoute, GetCqrsObservatoryPage)
            .WithTags("CqrsObservatory")
            .WithName("GetCqrsObservatoryPage")
            .WithSummary("CQRS / projection observatory (inline self-contained page).")
            .AllowAnonymous();

        return app;
    }

    internal static IResult GetCqrsObservatoryPage(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return Results.Text(CqrsObservatoryPage.Html, "text/html", Encoding.UTF8);
    }
}
