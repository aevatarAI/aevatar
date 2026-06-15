using System.Net;
using Aevatar.Authentication.Hosting;
using Aevatar.Bootstrap.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Hosting.Tests;

public sealed class MainnetAuthenticationDisabledTests
{
    [Fact]
    public async Task ProtectedEndpoint_WhenAuthenticationIsDisabled_ShouldReturnUnauthorizedInsteadOfServerError()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Configuration["Aevatar:Authentication:Enabled"] = "false";
        builder.AddAevatarDefaultHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
            options.EnableHealthEndpoints = false;
            options.MapRootHealthEndpoint = false;
            options.EnableOpenApiDocument = false;
            options.AutoMapCapabilities = false;
        });
        builder.AddAevatarAuthentication();

        await using var app = builder.Build();
        app.UseAevatarDefaultHost();
        app.MapGet("/protected", static () => Results.Ok(new { ok = true }))
            .RequireAuthorization();
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/protected");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await app.StopAsync();
    }
}
