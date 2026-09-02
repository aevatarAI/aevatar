using Aevatar.Authentication.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Aevatar.Bootstrap.Tests;

public class VoiceAccessTokenAuthenticationTests
{
    [Fact]
    public async Task AddAevatarAuthentication_WhenEnabled_ShouldExtractWhipOfferAccessTokenOnlyForVoicePaths()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });

        builder.Configuration["Aevatar:Authentication:Enabled"] = "true";
        builder.Configuration["Aevatar:Authentication:Authority"] = "https://id.example.com";

        builder.AddAevatarAuthentication();

        using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var jwtOptions = scope.ServiceProvider
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
        var scheme = new AuthenticationScheme(
            JwtBearerDefaults.AuthenticationScheme,
            JwtBearerDefaults.AuthenticationScheme,
            typeof(JwtBearerHandler));

        var whipContext = CreateMessageReceivedContext(
            app.Services,
            jwtOptions,
            scheme,
            "/whip/offer",
            new QueryString("?access_token=%20caller-jwt%20"));
        await jwtOptions.Events.MessageReceived(whipContext);

        whipContext.Token.Should().Be("caller-jwt");

        var nonVoiceContext = CreateMessageReceivedContext(
            app.Services,
            jwtOptions,
            scheme,
            "/api/other",
            new QueryString("?access_token=%20caller-jwt%20"));
        await jwtOptions.Events.MessageReceived(nonVoiceContext);

        nonVoiceContext.Token.Should().BeNull();
    }

    private static MessageReceivedContext CreateMessageReceivedContext(
        IServiceProvider services,
        JwtBearerOptions options,
        AuthenticationScheme scheme,
        string path,
        QueryString queryString)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services,
        };
        httpContext.Request.Path = path;
        httpContext.Request.QueryString = queryString;
        return new MessageReceivedContext(httpContext, scheme, options);
    }
}
