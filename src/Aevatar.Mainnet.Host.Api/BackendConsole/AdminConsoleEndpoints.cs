using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Mainnet.Host.Api.BackendConsole;

// Serves the fused "Aevatar Backend Console" single-page admin app at /admin. The page is a real
// .html asset embedded in this assembly (BackendConsole/admin.html) rather than a C# raw-string,
// so the front-end stays a lintable, design-refreshable file. Served anonymously as a static
// shell; the in-page JS gates the app behind the shared NyxID OIDC PKCE login (token under
// aevatar-console:nyxid:pkce:token, code exchange via the shared /auto/callback), so a single
// login spans the whole backend console suite. No data endpoints are introduced here — each
// module fetches its own existing API.
internal static class AdminConsoleEndpoints
{
    private const string PageRoute = "/admin";

    private static readonly Lazy<string> PageHtml = new(LoadPageHtml);

    public static IEndpointRouteBuilder MapAdminConsoleEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(PageRoute, GetAdminConsolePage)
            .WithTags("BackendConsole")
            .WithName("GetAdminConsolePage")
            .WithSummary("Fused backend admin console (self-contained page served from an embedded .html asset).")
            .AllowAnonymous();

        return app;
    }

    internal static IResult GetAdminConsolePage(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return Results.Text(PageHtml.Value, "text/html", Encoding.UTF8);
    }

    private static string LoadPageHtml()
    {
        var assembly = typeof(AdminConsoleEndpoints).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("admin.html", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded admin console asset '{resourceName}' was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
