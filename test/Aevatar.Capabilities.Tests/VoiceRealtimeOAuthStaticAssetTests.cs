using Aevatar.BackendConsole.Hosting;
using Aevatar.Mainnet.Host.Api.BackendConsole;
using Aevatar.Mainnet.Host.Api.Voice;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Capabilities.Tests;

public sealed class VoiceRealtimeOAuthStaticAssetTests
{
    [Fact]
    public async Task AdminShell_ShouldForwardFeatureScopedLoginRequest()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        html.Should().Contain(
            "beginLogin(msg.resources,msg.tokenPurpose,msg.authFlow,msg.popupName,msg.authRequestId,ev.source)");
        html.Should().Contain(":voice-realtime:token",
            "global logout must clear the feature-scoped Voice token too");
    }

    [Fact]
    public async Task SharedCallback_ShouldExchangePendingResourcesIntoSeparateVoiceToken()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/auto/callback");

        html.Should().Contain("pending.resources");
        html.Should().Contain("token.oauth_resources = requestedResources");
        html.Should().Contain("tokenPurpose === VOICE_TOKEN_PURPOSE ? VOICE_TOKEN_KEY : TOKEN_KEY",
            "feature authorization must not overwrite the baseline console token");
        html.Should().Contain("localStorage.setItem(tokenKey, JSON.stringify(token))",
            "feature-scoped token exchange must write through the selected storage key");
        html.Should().Contain("localStorage.setItem(TOKEN_KEY, JSON.stringify(token))",
            "ordinary finalized login must still update the baseline console token");
        html.Should().Contain("resourcesCover(tokenResponseResources(token), requestedResources)");
    }

    [Theory]
    [InlineData(null, "openai-realtime")]
    [InlineData("custom-realtime", "custom-realtime")]
    public async Task VoiceShell_ShouldInjectConfiguredRealtimeServiceSlug(
        string? configuredSlug,
        string expectedSlug)
    {
        await using var app = await CreateAppAsync(configuredSlug);
        var html = await app.GetTestClient().GetStringAsync("/voice");

        html.Should().Contain($"const VOICE_REALTIME_SERVICE_SLUG = \"{expectedSlug}\"");
        html.Should().Contain("CFG.resources.find(resource=>",
            "Voice must reuse the canonical prefix of the already-injected Aevatar resource");
        html.Should().Contain("baseline.slice(0,-\"aevatar\".length)");
        html.Should().Contain(
            "\"resources\":[\"https://api.example.test/api/v1/proxy/s/aevatar\",\"https://api.example.test/api/v1/proxy/s/ornn-api\"]");
        html.Should().Contain("\"nyxidApi\":\"https://api.example.test\"");
        html.Should().NotContain("const base=String(CFG.authority||\"\")");
        html.Should().NotContain("__VOICE_REALTIME_SERVICE_SLUG__");
    }

    private static async Task<WebApplication> CreateAppAsync(string? realtimeServiceSlug = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Configuration["Aevatar:BackendConsole:OidcAuthority"] = "https://id.example.test";
        builder.Configuration["Aevatar:BackendConsole:OidcClientId"] = "client-example";
        builder.Configuration["Aevatar:BackendConsole:OidcScope"] = "openid profile";
        builder.Configuration["Aevatar:BackendConsole:NyxApiBaseUrl"] = "https://api.example.test";
        builder.Configuration["Aevatar:BackendConsole:StorageKey"] = "console:test";
        builder.Configuration["Aevatar:BackendConsole:DefaultReturnPath"] = "/admin";
        if (!string.IsNullOrWhiteSpace(realtimeServiceSlug))
            builder.Configuration["Aevatar:VoicePresence:OpenAI:Nyxid:ServiceSlug"] = realtimeServiceSlug;
        builder.Services.AddBackendConsoleStaticAssets(builder.Configuration);

        var app = builder.Build();
        app.MapAdminConsoleEndpoints();
        app.MapAutoConsoleCallbackEndpoints();
        app.MapVoiceConsoleEndpoints();
        await app.StartAsync();
        return app;
    }
}
