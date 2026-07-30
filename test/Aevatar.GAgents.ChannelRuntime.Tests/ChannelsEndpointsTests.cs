using Aevatar.BackendConsole.Hosting;
using Aevatar.GAgents.Channel.NyxIdRelay;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelsEndpointsTests
{
    [Fact]
    public void MapChannels_RegistersPage_AsAnonymousGet_WithoutOwnCallback()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
        });

        var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;
        app.MapChannels();

        var endpoints = routeBuilder.DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        var page = endpoints.Single(route => string.Equals(route.RoutePattern.RawText, "/channels", StringComparison.Ordinal));
        page.Metadata.OfType<HttpMethodMetadata>().Single().HttpMethods.Should().Contain("GET");
        page.Metadata.OfType<IAllowAnonymous>().Should().NotBeEmpty("the page is gated by in-page OIDC, not server auth");

        // No /channels/callback: the unified console suite shares one OIDC redirect target,
        // /auto/callback, so per-page callback routes are intentionally absent.
        endpoints.Any(route => route.RoutePattern.RawText == "/channels/callback").Should().BeFalse();
    }

    [Fact]
    public void EmbeddedPage_PreservesContractMarkers()
    {
        var html = ReadEmbeddedHtml();

        html.Should().StartWith("<!doctype html>");
        html.Should().Contain("id=\"app\"");
        html.Should().NotContain("class=\"dock\"", "the design-review dock must be stripped from the shipped page");
        // OIDC uses the unified suite's shared redirect target + shared storage (one login spans all pages)
        html.Should().Contain("/auto/callback");
        html.Should().Contain("__BACKEND_CONSOLE_CONFIG__");
        // the consolidated suite brand is fixed top-left across every page
        html.Should().Contain("Aevatar Backend Console");
        // skill Tier-1 scope (the #1 silent-bot fix), event sub, and the default LLM reminder
        html.Should().Contain("im:message.p2p_msg:readonly");
        html.Should().Contain("im.message.receive_v1");
        html.Should().Contain("chrono-llm-public");
        html.Should().Contain("gpt-5.5");
        // wired to the live facade (relative), not a mock host
        html.Should().Contain("/api/channels/registrations");
        html.Should().Contain("/api/user-config/llm");
        html.Should().Contain("workflow-result-delivery/repair");
        html.Should().Contain("Repair workflow replies");
        html.Should().Contain("workflow_result_delivery_status");
        html.Should().Contain("无需修改 Lark 后台配置");
        html.Should().NotContain("workflow_result_delivery_credential");
        html.Should().NotContain("secret_reference");
        // honest status (no perpetual "查询中" for un-queryable bots) + admin all-accounts view
        html.Should().Contain("非本账户");
        html.Should().Contain("/api/channels/me");
        html.Should().Contain("所有账户");
    }

    [Fact]
    public void EmbeddedPage_RequiresCompleteLarkCredentials_AndForwardsOptionalEncryptKey()
    {
        var html = ReadEmbeddedHtml();

        html.Should().Contain(
            "requiredOk:(c)=> !!(c.app_id.trim() && c.app_secret.trim() && c.verification_token.trim())");
        html.Should().Contain("{ name:\"encrypt_key\"");
        html.Should().Contain("encrypt_key:c.encrypt_key.trim()");
    }

    [Fact]
    public void EmbeddedPage_ShowsDurableLarkRecoveryInstructions()
    {
        var html = ReadEmbeddedHtml();

        html.Should().Contain("r.webhook_url");
        html.Should().Contain("https://open.larksuite.com/app");
        html.Should().Contain("Event Subscriptions");
        html.Should().Contain("im.message.receive_v1");
        html.Should().Contain("只有收到验证通过的入站消息并变为 active 才算完成");
    }

    [Fact]
    public void EmbeddedPage_ReplacesRegistrationOnlyAfterAuthoritativeDelete()
    {
        var html = ReadEmbeddedHtml();

        html.Should().Contain("async function replaceRegistration(registration)");
        html.Should().Contain("if(!await doDelete(registration.id)) return;");
        html.Should().Contain("enterWizard((registration.platform||\"lark\").toLowerCase())");
        html.Should().NotContain("btn(\"重新接入\"");
    }

    private static string ReadEmbeddedHtml()
    {
        var assembly = typeof(ChannelsEndpoints).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("channels.html", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("channels.html resource not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task GetChannelsPage_ShouldRenderInjectedEmbeddedAsset()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:BackendConsole:OidcAuthority"] = "https://id.example.test",
                ["Aevatar:BackendConsole:OidcClientId"] = "client-example",
                ["Aevatar:BackendConsole:OidcScope"] = "openid profile",
                ["Aevatar:BackendConsole:NyxApiBaseUrl"] = "https://api.example.test",
                ["Aevatar:BackendConsole:StorageKey"] = "console:test",
            })
            .Build();
        services.AddBackendConsoleStaticAssets(configuration);
        services.AddLogging();
        var http = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        http.Response.Body = new MemoryStream();

        var assets = http.RequestServices.GetRequiredService<IBackendConsoleAssetService>();
        var result = ChannelsEndpoints.GetChannelsPage(http, assets);
        await result.ExecuteAsync(http);

        http.Response.ContentType.Should().Be("text/html; charset=utf-8");
        http.Response.Body.Position = 0;
        using var reader = new StreamReader(http.Response.Body);
        var html = await reader.ReadToEndAsync();
        html.Should().Contain("https://id.example.test");
        html.Should().Contain("client-example");
        html.Should().Contain("console:test");
        html.Should().Contain("https://api.example.test/api/v1/proxy/s/aevatar");
        html.Should().Contain("searchParams.append(\"resource\"");
        html.Should().Contain("form.append(\"resource\"");
        html.Should().NotContain("__BACKEND_CONSOLE_CONFIG__");
    }
}
