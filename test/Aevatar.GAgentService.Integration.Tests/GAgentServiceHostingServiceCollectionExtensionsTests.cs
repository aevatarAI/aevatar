#pragma warning disable CS0618 // Tests exercise legacy migration utilities pending removal
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Hosting.DependencyInjection;
using Aevatar.GAgentService.Governance.Hosting.Migration;
using Aevatar.GAgentService.Governance.Projection.DependencyInjection;
using Aevatar.GAgentService.Governance.Projection.ReadModels;
using Aevatar.GAgentService.Projection.ReadModels;
using Aevatar.GAgentService.Core.Ports;
using Aevatar.GAgentService.Hosting.DependencyInjection;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.GAgentService.Projection.DependencyInjection;
using Aevatar.GAgentService.Infrastructure.Adapters;
using Aevatar.Hosting;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class GAgentServiceHostingServiceCollectionExtensionsTests
{
    [Fact]
    public void AddGAgentServiceCapability_ShouldRegisterCorePortsAndAdapters()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        services.AddGAgentServiceCapability(configuration);

        services.Should().Contain(x => x.ServiceType == typeof(IServiceCommandPort));
        services.Should().Contain(x => x.ServiceType == typeof(IServiceLifecycleQueryPort));
        services.Should().Contain(x => x.ServiceType == typeof(IServiceServingQueryPort));
        services.Should().Contain(x => x.ServiceType == typeof(IServiceInvocationPort));
        services.Should().Contain(x => x.ServiceType == typeof(IServiceGovernanceCommandPort));
        services.Should().Contain(x => x.ServiceType == typeof(IServiceGovernanceQueryPort));
        services.Should().Contain(x => x.ServiceType == typeof(IActivationCapabilityViewReader));
        services.Should().Contain(x => x.ServiceType == typeof(IInvokeAdmissionAuthorizer));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IHostedService) &&
            x.ImplementationType == typeof(ServiceGovernanceLegacyMigrationHostedService));
        services.Count(x => x.ServiceType == typeof(IServiceImplementationAdapter)).Should().Be(3);
        services.Should().Contain(x => x.ImplementationType == typeof(StaticServiceImplementationAdapter));
        services.Should().Contain(x => x.ImplementationType == typeof(ScriptingServiceImplementationAdapter));
        services.Should().Contain(x => x.ImplementationType == typeof(WorkflowServiceImplementationAdapter));
    }

    [Fact]
    public void AddGAgentServiceProjectionReadModelProviders_ShouldBeIdempotentForDefaultConfiguration()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        services.AddGAgentServiceProjectionReadModelProviders(configuration);
        var afterFirstRegistration = services.Count;
        services.AddGAgentServiceProjectionReadModelProviders(configuration);

        services.Count.Should().Be(afterFirstRegistration);
    }

    [Fact]
    public void AddGAgentServiceProjectionReadModelProviders_ShouldRejectInvalidBooleanValue()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:InMemory:Enabled"] = "maybe",
            })
            .Build();

        var act = () => services.AddGAgentServiceProjectionReadModelProviders(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Invalid boolean value*");
    }

    [Fact]
    public void AddGAgentServiceProjectionReadModelProviders_ShouldRejectMultipleEnabledProviders()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
                ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://localhost:9200",
                ["Projection:Document:Providers:InMemory:Enabled"] = "true",
            })
            .Build();

        var act = () => services.AddGAgentServiceProjectionReadModelProviders(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Exactly one document projection provider must be enabled*");
    }

    [Fact]
    public async Task AddGAgentServiceCapabilityBundle_ShouldRegisterCapabilityAndMapServiceRoutes()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = false;
            options.ValidateScopes = false;
        });

        builder.AddGAgentServiceCapabilityBundle();

        await using var app = builder.Build();
        app.MapAevatarCapabilities();

        var registrations = app.Services.GetServices<AevatarCapabilityRegistration>().ToList();
        registrations.Should().ContainSingle(x => x.Name == "gagent-service");

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText)
            .Where(x => x is not null)
            .ToList();

        endpoints.Should().Contain("/api/services/");
        endpoints.Should().Contain("/api/services/{serviceId}/revisions");
        endpoints.Should().Contain("/api/services/{serviceId}/invoke/{endpointId}");
        endpoints.Should().Contain("/api/services/{serviceId}/bindings");
        endpoints.Should().Contain("/api/services/{serviceId}/endpoint-catalog");
        endpoints.Should().Contain("/api/services/{serviceId}/policies");
    }

    [Fact]
    public void AddGAgentServiceCapabilityBundle_ShouldRejectNullBuilder()
    {
        WebApplicationBuilder? builder = null;

        var act = () => builder!.AddGAgentServiceCapabilityBundle();

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddGAgentServiceCapability_ShouldRejectNullArguments()
    {
        IServiceCollection? services = null;
        IConfiguration? configuration = null;

        var nullServicesAct = () => Aevatar.GAgentService.Hosting.DependencyInjection.ServiceCollectionExtensions.AddGAgentServiceCapability(services!, new ConfigurationBuilder().Build());
        var nullConfigurationAct = () => new ServiceCollection().AddGAgentServiceCapability(configuration!);

        nullServicesAct.Should().Throw<ArgumentNullException>();
        nullConfigurationAct.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddGAgentServiceProjectionReadModelProviders_ShouldRejectNullArguments()
    {
        IServiceCollection? services = null;
        IConfiguration? configuration = null;

        var nullServicesAct = () => Aevatar.GAgentService.Hosting.DependencyInjection.ServiceCollectionExtensions.AddGAgentServiceProjectionReadModelProviders(services!, new ConfigurationBuilder().Build());
        var nullConfigurationAct = () => new ServiceCollection().AddGAgentServiceProjectionReadModelProviders(configuration!);

        nullServicesAct.Should().Throw<ArgumentNullException>();
        nullConfigurationAct.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddGAgentServiceProjectionReadModelProviders_ShouldReturnEarlyWhenAlreadyRegistered()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddGAgentServiceProjection();
        services.AddGAgentServiceProjectionReadModelProviders(configuration);
        var afterFirstRegistration = services.Count;

        services.AddGAgentServiceProjectionReadModelProviders(configuration);

        services.Count.Should().Be(afterFirstRegistration);
    }

    [Fact]
    public void AddGAgentServiceProjectionReadModelProviders_ShouldRegisterElasticsearchStores_WhenConfigured()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
                ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://localhost:9200",
            })
            .Build();

        services.AddGAgentServiceProjection();
        services.AddGAgentServiceProjectionReadModelProviders(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IProjectionWriteDispatcher<ServiceCatalogReadModel>>().Should().NotBeNull();
        provider.GetRequiredService<IProjectionWriteDispatcher<ServiceRevisionCatalogReadModel>>().Should().NotBeNull();
        provider.GetRequiredService<IProjectionDocumentReader<ServiceCatalogReadModel, string>>().Should().NotBeNull();
        provider.GetRequiredService<IProjectionDocumentReader<ServiceRevisionCatalogReadModel, string>>().Should().NotBeNull();
    }

    [Fact]
    public void AddGAgentServiceProjectionReadModelProviders_ShouldRejectElasticsearchWithoutEndpoints()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
            })
            .Build();

        services.AddGAgentServiceProjection();
        services.AddGAgentServiceProjectionReadModelProviders(configuration);
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IProjectionDocumentReader<ServiceCatalogReadModel, string>>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Endpoints is empty*");
    }

    [Fact]
    public void AddGAgentServiceGovernanceProjectionReadModelProviders_ShouldRegisterElasticsearchStores_WhenConfigured()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
                ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://localhost:9200",
            })
            .Build();

        services.AddGAgentServiceGovernanceProjection();
        services.AddGAgentServiceGovernanceProjectionReadModelProviders(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IProjectionWriteDispatcher<ServiceConfigurationReadModel>>().Should().NotBeNull();
        provider.GetRequiredService<IProjectionDocumentReader<ServiceConfigurationReadModel, string>>().Should().NotBeNull();
    }

    [Fact]
    public void AddGAgentServiceGovernanceProjectionReadModelProviders_ShouldBeIdempotentForDefaultConfiguration()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        services.AddGAgentServiceGovernanceProjectionReadModelProviders(configuration);
        var afterFirstRegistration = services.Count;
        services.AddGAgentServiceGovernanceProjectionReadModelProviders(configuration);

        services.Count.Should().Be(afterFirstRegistration);
    }

    [Fact]
    public void AddGAgentServiceGovernanceProjectionReadModelProviders_ShouldRejectInvalidBooleanValue()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:InMemory:Enabled"] = "maybe",
            })
            .Build();

        var act = () => services.AddGAgentServiceGovernanceProjectionReadModelProviders(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Invalid boolean value*");
    }

    [Fact]
    public void AddGAgentServiceGovernanceProjectionReadModelProviders_ShouldRejectMultipleEnabledProviders()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
                ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://localhost:9200",
                ["Projection:Document:Providers:InMemory:Enabled"] = "true",
            })
            .Build();

        var act = () => services.AddGAgentServiceGovernanceProjectionReadModelProviders(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Exactly one document projection provider must be enabled*");
    }

    [Fact]
    public void AddGAgentServiceGovernanceProjectionReadModelProviders_ShouldRejectElasticsearchWithoutEndpoints()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
            })
            .Build();

        services.AddGAgentServiceGovernanceProjection();
        services.AddGAgentServiceGovernanceProjectionReadModelProviders(configuration);
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IProjectionDocumentReader<ServiceConfigurationReadModel, string>>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Endpoints is empty*");
    }

    [Fact]
    public void AddGAgentServiceGovernanceProjectionReadModelProviders_ShouldRejectNullArguments()
    {
        IServiceCollection? services = null;
        IConfiguration? configuration = null;

        var nullServicesAct = () => Aevatar.GAgentService.Governance.Hosting.DependencyInjection.ServiceCollectionExtensions.AddGAgentServiceGovernanceProjectionReadModelProviders(services!, new ConfigurationBuilder().Build());
        var nullConfigurationAct = () => new ServiceCollection().AddGAgentServiceGovernanceProjectionReadModelProviders(configuration!);

        nullServicesAct.Should().Throw<ArgumentNullException>();
        nullConfigurationAct.Should().Throw<ArgumentNullException>();
    }
}
