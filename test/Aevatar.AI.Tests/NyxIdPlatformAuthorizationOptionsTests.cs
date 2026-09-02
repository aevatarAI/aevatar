using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aevatar.AI.Tests;

public sealed class NyxIdPlatformAuthorizationOptionsTests
{
    [Fact]
    public void AddNyxIdPlatformAuthorization_WithLegacyDisabledKillSwitch_ShouldRemainDisabled()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Aevatar:Observatory:CrossScopeEnabled"] = "false",
        });
        var services = new ServiceCollection();

        services.AddNyxIdPlatformAuthorization(configuration);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<ObservatoryAdminAuthorizationOptions>>()
            .Value.CrossScopeEnabled.Should().BeFalse();
    }

    [Fact]
    public void AddNyxIdPlatformAuthorization_WhenCanonicalSettingExists_ShouldOverrideLegacyValue()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Aevatar:Observatory:CrossScopeEnabled"] = "false",
            ["Aevatar:AdminAccess:CrossScopeEnabled"] = "true",
        });
        var services = new ServiceCollection();

        services.AddNyxIdPlatformAuthorization(configuration);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<ObservatoryAdminAuthorizationOptions>>()
            .Value.CrossScopeEnabled.Should().BeTrue();
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
