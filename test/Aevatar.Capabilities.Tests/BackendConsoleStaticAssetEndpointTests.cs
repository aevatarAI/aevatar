using System.Net;
using Aevatar.BackendConsole.Hosting;
using Aevatar.Mainnet.Host.Api.BackendConsole;
using Aevatar.Mainnet.Host.Api.Cqrs;
using Aevatar.Mainnet.Host.Api.Skills;
using Aevatar.Mainnet.Host.Api.Voice;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Capabilities.Tests;

public sealed class BackendConsoleStaticAssetEndpointTests
{
    [Theory]
    [InlineData("/admin", "Aevatar Backend Console")]
    [InlineData("/auto/callback", "正在完成登录")]
    [InlineData("/cqrs", "CQRS")]
    [InlineData("/voice", "Voice")]
    [InlineData("/workflow/skills", "Skills")]
    public async Task StaticShellEndpoints_ShouldRenderEmbeddedHtmlWithInjectedConfig(string path, string marker)
    {
        await using var app = await CreateAppAsync();
        var response = await app.GetTestClient().GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, html);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        html.Should().Contain(marker);
        html.Should().Contain("https://id.example.test");
        html.Should().Contain("client-example");
        html.Should().Contain("console:test");
        html.Should().NotContain("__BACKEND_CONSOLE_CONFIG__");
        html.Should().NotContain("https://nyx.chrono-ai.fun");
        html.Should().NotContain("https://nyx-api.chrono-ai.fun");
        html.Should().NotContain("37a93189-2734-406e-bca1-7dbdf25c5a53");
    }

    [Fact]
    public async Task AdminShell_AuditRefresh_ShouldReloadOnEntryAndGlobalRefresh()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        html.Should().Contain("if(!AUDIT_LOADING) loadAuditTrail();");
        html.Should().Contain("if((curParts()[0]||defaultModule())==='audit')");
        html.Should().Contain("toast('正在刷新审计日志');");
        html.Should().NotContain(
            "if(!AUDIT_LOADED||AUDIT_LOADING){ if(!AUDIT_LOADING) loadAuditTrail(); }");
    }

    private static async Task<WebApplication> CreateAppAsync()
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
        builder.Services.AddBackendConsoleStaticAssets(builder.Configuration);

        var app = builder.Build();
        app.MapAdminConsoleEndpoints();
        app.MapAutoConsoleCallbackEndpoints();
        app.MapCqrsObservatoryPageEndpoints();
        app.MapVoiceConsoleEndpoints();
        app.MapWorkflowSkillsEndpoints();
        await app.StartAsync();
        return app;
    }
}
