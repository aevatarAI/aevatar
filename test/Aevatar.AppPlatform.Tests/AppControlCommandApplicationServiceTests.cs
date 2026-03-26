using Aevatar.AppPlatform.Abstractions;
using Aevatar.AppPlatform.Abstractions.Ports;
using Aevatar.AppPlatform.Application.DependencyInjection;
using Aevatar.AppPlatform.Infrastructure.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.AppPlatform.Tests;

public class AppControlCommandApplicationServiceTests
{
    [Fact]
    public async Task Mutations_ShouldFlowThroughQueryPorts()
    {
        using var provider = BuildServiceProvider();
        var commandPort = provider.GetRequiredService<IAppControlCommandPort>();
        var appQueryPort = provider.GetRequiredService<IAppDefinitionQueryPort>();
        var releaseQueryPort = provider.GetRequiredService<IAppReleaseQueryPort>();
        var routeQueryPort = provider.GetRequiredService<IAppRouteQueryPort>();
        var resourceQueryPort = provider.GetRequiredService<IAppResourceQueryPort>();
        var functionQueryPort = provider.GetRequiredService<IAppFunctionQueryPort>();

        await commandPort.CreateAppAsync(new AppDefinitionSnapshot
        {
            AppId = "copilot",
            OwnerScopeId = "scope-dev",
            DisplayName = "Copilot",
            Description = "AI copilot app",
            Visibility = AppVisibility.Private,
        });

        await commandPort.UpsertReleaseAsync(new AppReleaseSnapshot
        {
            AppId = "copilot",
            ReleaseId = "prod",
            DisplayName = "Production",
            Status = AppReleaseStatus.Draft,
            ServiceRefs =
            {
                new AppServiceRef
                {
                    TenantId = "scope-dev",
                    AppId = "copilot",
                    Namespace = "prod",
                    ServiceId = "chat-gateway",
                    RevisionId = "r1",
                    ImplementationKind = AppImplementationKind.Workflow,
                    Role = AppServiceRole.Entry,
                },
            },
        });

        await commandPort.UpsertFunctionAsync(
            "copilot",
            "prod",
            new AppEntryRef
            {
                EntryId = "default-chat",
                ServiceId = "chat-gateway",
                EndpointId = "chat",
            });

        await commandPort.ReplaceReleaseResourcesAsync(new AppReleaseResourcesSnapshot
        {
            AppId = "copilot",
            ReleaseId = "prod",
            ConnectorRefs =
            {
                new AppConnectorRef
                {
                    ResourceId = "knowledge-storage",
                    ConnectorName = "chrono_storage",
                },
            },
            SecretRefs =
            {
                new AppSecretRef
                {
                    ResourceId = "knowledge-storage-key",
                    SecretName = "chrono_storage_api_key",
                },
            },
        });

        await commandPort.UpsertRouteAsync(new AppRouteSnapshot
        {
            AppId = "copilot",
            ReleaseId = "prod",
            EntryId = "default-chat",
            RoutePath = "/copilot/",
        });

        await commandPort.PublishReleaseAsync("copilot", "prod");
        await commandPort.SetDefaultReleaseAsync("copilot", "prod");

        var app = await appQueryPort.GetAsync("copilot");
        var release = await releaseQueryPort.GetAsync("copilot", "prod");
        var resources = await resourceQueryPort.GetReleaseResourcesAsync("copilot", "prod");
        var routes = await routeQueryPort.ListAsync("copilot");
        var functions = await functionQueryPort.ListAsync("copilot", "prod");

        app.Should().NotBeNull();
        app!.DefaultReleaseId.Should().Be("prod");
        app.RoutePaths.Should().ContainSingle().Which.Should().Be("/copilot");

        release.Should().NotBeNull();
        release!.Status.Should().Be(AppReleaseStatus.Published);

        resources.Should().NotBeNull();
        resources!.ConnectorRefs.Should().ContainSingle();
        resources.ConnectorRefs[0].ConnectorName.Should().Be("chrono_storage");
        resources.SecretRefs.Should().ContainSingle();
        resources.SecretRefs[0].SecretName.Should().Be("chrono_storage_api_key");

        routes.Should().ContainSingle();
        routes[0].RoutePath.Should().Be("/copilot");
        routes[0].ReleaseId.Should().Be("prod");
        routes[0].EntryId.Should().Be("default-chat");

        functions.Should().ContainSingle();
        functions[0].FunctionId.Should().Be("default-chat");
        functions[0].ServiceId.Should().Be("chat-gateway");
        functions[0].EndpointId.Should().Be("chat");
    }

    [Fact]
    public async Task DeleteFunctionAsync_ShouldRejectWhenRouteStillReferencesFunction()
    {
        using var provider = BuildServiceProvider();
        var commandPort = provider.GetRequiredService<IAppControlCommandPort>();

        await commandPort.CreateAppAsync(new AppDefinitionSnapshot
        {
            AppId = "copilot",
            OwnerScopeId = "scope-dev",
            DisplayName = "Copilot",
            Visibility = AppVisibility.Private,
        });

        await commandPort.UpsertReleaseAsync(new AppReleaseSnapshot
        {
            AppId = "copilot",
            ReleaseId = "prod",
            DisplayName = "Production",
            Status = AppReleaseStatus.Draft,
            ServiceRefs =
            {
                new AppServiceRef
                {
                    ServiceId = "chat-gateway",
                    Role = AppServiceRole.Entry,
                    ImplementationKind = AppImplementationKind.Workflow,
                },
            },
        });

        await commandPort.UpsertFunctionAsync(
            "copilot",
            "prod",
            new AppEntryRef
            {
                EntryId = "default-chat",
                ServiceId = "chat-gateway",
                EndpointId = "chat",
            });

        await commandPort.UpsertRouteAsync(new AppRouteSnapshot
        {
            AppId = "copilot",
            ReleaseId = "prod",
            EntryId = "default-chat",
            RoutePath = "/copilot",
        });

        var act = async () => await commandPort.DeleteFunctionAsync("copilot", "prod", "default-chat");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*still referenced by at least one route*");
    }

    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection()
            .Build();

        services.AddAppPlatformInfrastructure(configuration);
        services.AddAppPlatformApplication();
        return services.BuildServiceProvider();
    }
}
