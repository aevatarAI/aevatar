using Aevatar.AppPlatform.Abstractions;
using Aevatar.AppPlatform.Abstractions.Ports;
using Aevatar.AppPlatform.Application.DependencyInjection;
using Aevatar.AppPlatform.Infrastructure.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.AppPlatform.Tests;

public class ConfiguredAppRegistryReaderTests
{
    [Fact]
    public async Task ResolveAsync_ShouldReturnConfiguredAppRoute()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppPlatform:Apps:0:AppId"] = "copilot",
                ["AppPlatform:Apps:0:OwnerScopeId"] = "scope-dev",
                ["AppPlatform:Apps:0:DisplayName"] = "Copilot",
                ["AppPlatform:Apps:0:Visibility"] = "public",
                ["AppPlatform:Apps:0:DefaultReleaseId"] = "prod-2026-03-25",
                ["AppPlatform:Apps:0:Routes:0:RoutePath"] = "/copilot/",
                ["AppPlatform:Apps:0:Releases:0:ReleaseId"] = "prod-2026-03-25",
                ["AppPlatform:Apps:0:Releases:0:DisplayName"] = "Production",
                ["AppPlatform:Apps:0:Releases:0:Status"] = "published",
                ["AppPlatform:Apps:0:Releases:0:Services:0:TenantId"] = "scope-dev",
                ["AppPlatform:Apps:0:Releases:0:Services:0:AppId"] = "copilot",
                ["AppPlatform:Apps:0:Releases:0:Services:0:Namespace"] = "prod",
                ["AppPlatform:Apps:0:Releases:0:Services:0:ServiceId"] = "chat-gateway",
                ["AppPlatform:Apps:0:Releases:0:Services:0:RevisionId"] = "r1",
                ["AppPlatform:Apps:0:Releases:0:Services:0:ImplementationKind"] = "workflow",
                ["AppPlatform:Apps:0:Releases:0:Services:0:Role"] = "entry",
                ["AppPlatform:Apps:0:Releases:0:Entries:0:EntryId"] = "default-chat",
                ["AppPlatform:Apps:0:Releases:0:Entries:0:ServiceId"] = "chat-gateway",
                ["AppPlatform:Apps:0:Releases:0:Entries:0:EndpointId"] = "chat",
            })
            .Build();

        services.AddAppPlatformInfrastructure(configuration);
        services.AddAppPlatformApplication();

        using var provider = services.BuildServiceProvider();
        var appQueryPort = provider.GetRequiredService<IAppDefinitionQueryPort>();
        var releaseQueryPort = provider.GetRequiredService<IAppReleaseQueryPort>();
        var routeQueryPort = provider.GetRequiredService<IAppRouteQueryPort>();

        var apps = await appQueryPort.ListAsync("scope-dev");
        var release = await releaseQueryPort.GetAsync("copilot", "prod-2026-03-25");
        var resolution = await routeQueryPort.ResolveAsync("/COPILOT/");

        apps.Should().ContainSingle();
        apps[0].AppId.Should().Be("copilot");
        apps[0].Visibility.Should().Be(AppVisibility.Public);
        release.Should().NotBeNull();
        release!.Status.Should().Be(AppReleaseStatus.Published);
        resolution.Should().NotBeNull();
        resolution!.RoutePath.Should().Be("/copilot");
        resolution.App.AppId.Should().Be("copilot");
        resolution.Release.ReleaseId.Should().Be("prod-2026-03-25");
        resolution.Entry.EntryId.Should().Be("default-chat");
        resolution.Entry.EndpointId.Should().Be("chat");
        resolution.Entry.ServiceRef.ServiceId.Should().Be("chat-gateway");
        resolution.Entry.ServiceRef.Role.Should().Be(AppServiceRole.Entry);
        resolution.Entry.ServiceRef.ImplementationKind.Should().Be(AppImplementationKind.Workflow);
    }
}
