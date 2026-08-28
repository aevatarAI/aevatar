using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Mainnet.Host.Api.Scheduled;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Capabilities.Tests;

public sealed class DevelopmentNyxIdApiKeyEndpointsTests
{
    [Fact]
    public async Task UserServiceKeys_WhenConfigured_ShouldPublishStrictActiveInventory()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Configuration["Aevatar:Authentication:Enabled"] = "false";
        builder.Configuration[$"{DevelopmentNyxIdApiKeyEndpoints.ActiveUserServicesSectionName}:0:UserServiceId"] =
            "usvc-alpha";
        builder.Configuration[$"{DevelopmentNyxIdApiKeyEndpoints.ActiveUserServicesSectionName}:0:ServiceSlug"] =
            "api-example";
        builder.Configuration[$"{DevelopmentNyxIdApiKeyEndpoints.ActiveUserServicesSectionName}:0:DisplayName"] =
            "Example API";

        await using var app = builder.Build();
        app.MapDevelopmentNyxIdApiKeyEndpoints();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "agent-key");

        var response = await client.GetAsync("/api/v1/keys");
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var inventory = NyxIdApiAccessResponseParser.ParseUserServiceKeys(content);
        inventory.Succeeded.Should().BeTrue();
        inventory.Value!.Services.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new NyxIdUserServiceKey(
                "usvc-alpha",
                "api-example",
                "Example API",
                "Example API",
                true,
                NyxIdUserServiceCredentialStatus.Active,
                null,
                NyxIdUserServiceNodeStatus.NotBound,
                new NyxIdUserServiceCredentialSource(NyxIdUserServiceCredentialSourceKind.Personal),
                null,
                "api-example",
                true));
    }

    [Fact]
    public async Task UserServiceKeys_WithoutBearer_ShouldFailClosed()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Configuration["Aevatar:Authentication:Enabled"] = "false";

        await using var app = builder.Build();
        app.MapDevelopmentNyxIdApiKeyEndpoints();
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/api/v1/keys");
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var document = JsonDocument.Parse(content);
        document.RootElement.GetProperty("message").GetString().Should().Be("unauthorized");
    }
}
