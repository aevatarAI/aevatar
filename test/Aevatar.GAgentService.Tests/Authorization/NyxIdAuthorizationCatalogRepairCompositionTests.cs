using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Hosting.DependencyInjection;
using Aevatar.GAgentService.Infrastructure.Schedules.Authorization;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.Authorization;

public sealed class NyxIdAuthorizationCatalogRepairCompositionTests
{
    [Fact]
    public void InMemoryCatalogHosting_ShouldNotResolveRepairPorts()
    {
        using var provider = CreateProvider(elasticsearchEnabled: false);

        provider.GetService<INyxIdAuthorizationCatalogRepairCommandPort>()
            .Should()
            .BeNull();
        provider.GetService<INyxIdAuthorizationCatalogRepairRefreshPort>()
            .Should()
            .BeNull();
        provider.GetService<INyxIdAuthorizationCatalogVersionRegressionRepairService>()
            .Should()
            .BeNull();
    }

    [Fact]
    public void ElasticsearchCatalogHosting_ShouldResolveSeparateRepairAdapters()
    {
        using var provider = CreateProvider(elasticsearchEnabled: true);

        var ordinaryCommand =
            provider.GetRequiredService<INyxIdAuthorizationCatalogCommandPort>();
        var repairCommand =
            provider.GetRequiredService<INyxIdAuthorizationCatalogRepairCommandPort>();
        var ordinaryRefresh =
            provider.GetRequiredService<INyxIdAuthorizationCatalogRefreshPort>();
        var repairRefresh =
            provider.GetRequiredService<INyxIdAuthorizationCatalogRepairRefreshPort>();

        repairCommand.Should().NotBeSameAs(ordinaryCommand);
        repairCommand.Should().NotBeOfType<NyxIdAuthorizationCatalogCommandPort>();
        repairRefresh.Should().NotBeSameAs(ordinaryRefresh);
        repairRefresh.Should().NotBeOfType<NyxIdAuthorizationCatalogRefreshPort>();
        provider.GetRequiredService<INyxIdAuthorizationCatalogVersionRegressionRepairService>()
            .Should()
            .NotBeNull();
    }

    private static ServiceProvider CreateProvider(bool elasticsearchEnabled)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAevatarRuntime();
        services.AddNyxIdAuthorizationCatalogHosting(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Projection:Policies:Environment"] = "Development",
                    ["Projection:Document:Providers:InMemory:Enabled"] =
                        (!elasticsearchEnabled).ToString(),
                    ["Projection:Document:Providers:Elasticsearch:Enabled"] =
                        elasticsearchEnabled.ToString(),
                    ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] =
                        "http://127.0.0.1:9200",
                })
                .Build());
        return services.BuildServiceProvider();
    }
}
