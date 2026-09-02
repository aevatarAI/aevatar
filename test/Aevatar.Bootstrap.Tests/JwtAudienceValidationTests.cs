using Aevatar.Authentication.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Aevatar.Bootstrap.Tests;

public sealed class JwtAudienceValidationTests
{
    [Fact]
    public async Task AddAevatarAuthentication_WhenDisabledOutsideDevelopment_ShouldStillUseJwtBearerAuthentication()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });

        builder.Configuration["Aevatar:Authentication:Enabled"] = "false";
        builder.Configuration["Aevatar:Authentication:Authority"] = "https://id.example.com";
        builder.Configuration["Aevatar:Authentication:Audience"] = "aevatar-api";

        builder.AddAevatarAuthentication();

        using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var schemeProvider = scope.ServiceProvider.GetRequiredService<IAuthenticationSchemeProvider>();

        (await schemeProvider.GetDefaultAuthenticateSchemeAsync())!.Name.Should().Be("Bearer");
        (await schemeProvider.GetDefaultChallengeSchemeAsync())!.Name.Should().Be("Bearer");
    }

    [Fact]
    public void AddAevatarAuthentication_WhenAudienceIsMissingInDevelopment_ShouldAllowExplicitOptOut()
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
        var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        options.TokenValidationParameters.ValidateAudience.Should().BeFalse();
    }

    [Fact]
    public void AddAevatarAuthentication_WhenAudienceIsMissingOutsideDevelopment_ShouldFailClosed()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });

        builder.Configuration["Aevatar:Authentication:Enabled"] = "true";
        builder.Configuration["Aevatar:Authentication:Authority"] = "https://id.example.com";

        var act = () => builder.AddAevatarAuthentication();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage(
                "Aevatar:Authentication:Audience is required when authentication is enabled outside Development.");
    }
}
