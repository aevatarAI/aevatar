using System.Text.Json;
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
    private const string RealtimeServiceSlugPlaceholder = "__VOICE_REALTIME_SERVICE_SLUG__";
    private const string DefaultRealtimeServiceSlug = "openai-realtime";

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
        [FromServices] IBackendConsoleAssetService assets,
        [FromServices] IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(configuration);

        var configuredSlug = configuration["Aevatar:VoicePresence:OpenAI:Nyxid:ServiceSlug"]?.Trim();
        var serviceSlug = string.IsNullOrWhiteSpace(configuredSlug)
            ? DefaultRealtimeServiceSlug
            : configuredSlug;
        var content = assets.Render(PageAsset).Replace(
            RealtimeServiceSlugPlaceholder,
            JsonSerializer.Serialize(serviceSlug),
            StringComparison.Ordinal);
        return Results.Text(content, PageAsset.ContentType);
    }
}
