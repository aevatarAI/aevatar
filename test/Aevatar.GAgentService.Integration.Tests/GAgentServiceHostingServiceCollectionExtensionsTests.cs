using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Hosting.DependencyInjection;
using Aevatar.GAgentService.Governance.Projection.DependencyInjection;
using Aevatar.GAgentService.Governance.Projection.ReadModels;
using Aevatar.GAgentService.Projection.ReadModels;
using Aevatar.GAgentService.Application.Schedules;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Hosting.DependencyInjection;
using Aevatar.GAgentService.Core.Ports;
using Aevatar.GAgentService.Hosting.DependencyInjection;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.GAgentService.Projection.DependencyInjection;
using Aevatar.GAgentService.Infrastructure.Adapters;
using Aevatar.GAgentService.Infrastructure.Orchestration;
using Aevatar.GAgentService.Infrastructure.Schedules;
using Aevatar.Bootstrap.Hosting;
using Aevatar.Capabilities;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.AGUI.Contracts;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Extensions.Hosting;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Aevatar.Workflow.Infrastructure.Workflows;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.GAgentService.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Aevatar.GAgentService.Hosting.Responses;

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
        services.Should().Contain(x => x.ServiceType == typeof(IScopeBindingReadinessQueryPort));
        services.Should().Contain(x => x.ServiceType == typeof(IServiceInvocationPort));
        services.Should().Contain(x => x.ServiceType == typeof(ISkillWorkflowMountPort));
        services.Should().Contain(x => x.ServiceType == typeof(IStaticGAgentStreamInvocationPort<AGUIEvent>));
        services.Should().NotContain(x => x.ServiceType == typeof(ITeamEntryMemberResolver));
        services.Should().Contain(x => x.ServiceType == typeof(IServiceGovernanceCommandPort));
        services.Should().Contain(x => x.ServiceType == typeof(IServiceGovernanceQueryPort));
        services.Should().Contain(x => x.ServiceType == typeof(IActivationCapabilityViewReader));
        services.Should().Contain(x => x.ServiceType == typeof(IInvokeAdmissionAuthorizer));
        // Refactor (iter23/cluster-003-governance-legacy-startup-eventstore-fold):
        //   Old pattern: capability registration added a startup hosted service that folded legacy events.
        //   New principle: startup must not own migration replay; only explicit runtime services are composed.
        services.Should().NotContain(x =>
            x.ServiceType == typeof(IHostedService) &&
            x.ImplementationType != null &&
            string.Equals(
                x.ImplementationType.FullName,
                "Aevatar.GAgentService.Governance.Hosting.Migration.ServiceGovernanceLegacyMigrationHostedService",
                StringComparison.Ordinal));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IHostedService) &&
            x.ImplementationType != null &&
            x.ImplementationType.FullName == "Aevatar.GAgentService.Hosting.Demo.GAgentServiceDemoBootstrapHostedService");
        // Scripting was not composed first, so the scripting bridge (adapter/hook) is absent.
        services.Count(x => x.ServiceType == typeof(IServiceImplementationAdapter)).Should().Be(2);
        services.Should().Contain(x => x.ImplementationType == typeof(StaticServiceImplementationAdapter));
        services.Should().NotContain(x => x.ImplementationType == typeof(ScriptingServiceImplementationAdapter));
        services.Should().Contain(x => x.ImplementationType == typeof(WorkflowServiceImplementationAdapter));
        services.Should().NotContain(x =>
            x.ServiceType == typeof(ICommittedStatePublicationHook) &&
            x.ImplementationType == typeof(ScriptingServiceRevisionRepublishHook));
        services.Should().NotContain(x =>
            x.ServiceType == typeof(ICommittedStatePublicationHook) &&
            x.ImplementationType == typeof(LlmRunExecutionScheduler));
        services.Should().Contain(x => x.ServiceType == typeof(LlmRunExecutionScheduler));
        services.Should().Contain(x => x.ServiceType == typeof(ILlmRunExecutionScheduler));
        services.Should().Contain(x =>
            x.ServiceType == typeof(ILlmRunExecutionQueue) &&
            x.ImplementationType == typeof(LlmRunExecutionQueue));
        services.Should().Contain(x => x.ServiceType == typeof(ILlmRunExecutionService));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IHostedService) &&
            x.ImplementationType == typeof(LlmRunExecutionWorker));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ILlmRunCore>().Should().BeOfType<MissingLlmProviderRunCore>();
        provider.GetRequiredService<ILlmRunExecutionScheduler>()
            .Should()
            .BeSameAs(provider.GetRequiredService<LlmRunExecutionScheduler>());
        provider.GetRequiredService<ILlmRunExecutionQueue>().Should().BeOfType<LlmRunExecutionQueue>();
        provider.GetRequiredService<IScopeBindingReadinessQueryPort>().Should().NotBeNull();
        provider.GetRequiredService<IServiceRolloutCommandObservationQueryReader>().Should().NotBeNull();
        provider.GetRequiredService<IGAgentRunTerminalQueryPort>().Should().NotBeNull();
    }

    [Fact]
    public void AddGAgentServiceCapability_AfterSkills_ShouldReplaceNoOpWorkflowMountPort()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        services.AddSkills(_ => { });

        services.AddGAgentServiceCapability(configuration);

        services.Where(descriptor => descriptor.ServiceType == typeof(ISkillWorkflowMountPort))
            .Should().ContainSingle()
            .Which.ImplementationType.Should().Be(typeof(SkillWorkflowMountAdapter));
    }

    [Fact]
    public void AddGAgentServiceCapability_WhenLlmProviderFactoryExists_ShouldRegisterProviderBackedRunCore()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        services.AddSingleton<ILLMProviderFactory>(new ThrowingLlmProviderFactory());
        services.AddSingleton<IAgentToolExecutionPort, UnusedAgentToolExecutionPort>();

        services.AddGAgentServiceCapability(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ILlmRunCore>().Should().BeOfType<LlmRunCore>();
    }

    [Fact]
    public void AddGAgentServiceCapability_WithoutBindingQueryPort_ShouldResolveNoopScheduledCredentialAdmissionPort()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        services.AddGAgentServiceCapability(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IScheduledDispatchCredentialAdmissionPort>()
            .Should()
            .BeOfType<NoopScheduledDispatchCredentialAdmissionPort>();
    }

    [Fact]
    public void AddGAgentServiceCapability_WithBindingQueryPort_ShouldResolveNyxIdScheduledCredentialAdmissionPort()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        services.AddSingleton<IExternalIdentityBindingQueryPort>(new UnusedExternalIdentityBindingQueryPort());

        services.AddGAgentServiceCapability(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IScheduledDispatchCredentialAdmissionPort>()
            .Should()
            .BeOfType<NyxIdScheduledDispatchCredentialAdmissionPort>();
    }

    [Fact]
    public void AddGAgentServiceCapability_WhenWorkflowAndScriptingAlreadyRegistered_ShouldReuseExistingRegistrations()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        services.AddScriptCapability(configuration);
        services.AddWorkflowCapability(configuration);

        var scriptRegistrationsBefore = services.Count(x => x.ServiceType == typeof(IScriptEvolutionProposalPort));
        var workflowRegistrationsBefore = services.Count(x => x.ServiceType == typeof(IWorkflowCatalogPort));

        services.AddGAgentServiceCapability(configuration);

        services.Count(x => x.ServiceType == typeof(IScriptEvolutionProposalPort))
            .Should()
            .Be(scriptRegistrationsBefore);
        services.Count(x => x.ServiceType == typeof(IWorkflowCatalogPort))
            .Should()
            .Be(workflowRegistrationsBefore);
    }

    [Fact]
    public void AddGAgentServiceCapability_WithoutScriptingCapability_ShouldNotRegisterScriptingBridge()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        services.AddAevatarRuntime();
        services.AddWorkflowProjectionReadModelProviders(configuration);
        services.AddGAgentServiceCapability(configuration);

        // No pull-back: the bundle must not compose the scripting capability on its own.
        services.Should().NotContain(x =>
            x.ServiceType == typeof(Aevatar.Scripting.Hosting.DependencyInjection.ServiceCollectionExtensions.ScriptCapabilityRegistrationsMarker));
        services.Should().NotContain(x => x.ServiceType == typeof(IScopeScriptQueryPort));
        services.Should().NotContain(x => x.ServiceType == typeof(IScopeScriptCommandPort));
        services.Should().NotContain(x => x.ServiceType == typeof(IScopeScriptSaveObservationPort));
        services.Should().NotContain(x => x.ImplementationType == typeof(ScriptingServiceImplementationAdapter));
        services.Should().NotContain(x =>
            x.ServiceType == typeof(ICommittedStatePublicationHook) &&
            x.ImplementationType == typeof(ScriptingServiceRevisionRepublishHook));
        services.Should().NotContain(service =>
            ServiceTypeContains(service.ServiceType, "ScriptServiceRun"));

        // Services with optional scripting dependencies still compose without it.
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IScopeBindingCommandPort>().Should().NotBeNull();
    }

    [Fact]
    public void AddGAgentServiceCapability_WithScriptingCapability_ShouldRegisterScriptingBridge()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        services.AddScriptCapability(configuration);
        services.AddGAgentServiceCapability(configuration);

        services.Should().Contain(x => x.ServiceType == typeof(IScopeScriptQueryPort));
        services.Should().Contain(x => x.ServiceType == typeof(IScopeScriptCommandPort));
        services.Should().Contain(x => x.ServiceType == typeof(IScopeScriptSaveObservationPort));
        services.Should().Contain(x => x.ImplementationType == typeof(ScriptingServiceImplementationAdapter));
        services.Should().Contain(x =>
            x.ServiceType == typeof(ICommittedStatePublicationHook) &&
            x.ImplementationType == typeof(ScriptingServiceRevisionRepublishHook));
        services.Should().Contain(service =>
            ServiceTypeContains(service.ServiceType, "ScriptServiceRun"));
        services.Count(x => x.ServiceType == typeof(IServiceImplementationAdapter)).Should().Be(3);
    }

    [Fact]
    public void AddGAgentServiceCapability_ShouldRegisterConfiguredExternalExposureRetrySettings()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GAgentService:ExternalExposure:RetryMaxAttempts"] = "7",
                ["GAgentService:ExternalExposure:RetryBaseDelaySeconds"] = "3",
                ["GAgentService:ExternalExposure:RetryMaxDelaySeconds"] = "30",
            })
            .Build();

        services.AddGAgentServiceCapability(configuration);

        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<ServiceExternalExposureRetrySettings>();
        settings.MaxAttempts.Should().Be(7);
        settings.BaseDelay.Should().Be(TimeSpan.FromSeconds(3));
        settings.MaxDelay.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void AddGAgentServiceCapability_ShouldRejectInvalidExternalExposureRetrySettings()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GAgentService:ExternalExposure:RetryMaxAttempts"] = "0",
            })
            .Build();

        services.AddGAgentServiceCapability(configuration);

        using var provider = services.BuildServiceProvider();
        var act = () => provider.GetRequiredService<ServiceExternalExposureRetrySettings>();
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("maxAttempts");
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
        endpoints.Should().Contain("/api/scopes/{scopeId}/binding");
        endpoints.Should().Contain("/api/scopes/{scopeId}/workflows:save-and-bind");
        endpoints.Should().Contain("/api/scopes/{scopeId}/workflows:explicit-request-preview");
        endpoints.Should().Contain("/api/scopes/{scopeId}/binding/revisions/{revisionId}:activate");
        endpoints.Should().Contain("/api/scopes/{scopeId}/revisions");
        endpoints.Should().Contain("/api/scopes/{scopeId}/revisions/{revisionId}");
        endpoints.Should().Contain("/api/scopes/{scopeId}/binding/revisions/{revisionId}:retire");
        endpoints.Should().Contain("/api/scopes/{scopeId}/workflow/draft-run");
        endpoints.Should().Contain("/api/scopes/{scopeId}/invoke/chat:stream");
        endpoints.Should().Contain("/api/scopes/{scopeId}/invoke/{endpointId}");
        endpoints.Should().Contain("/api/scopes/{scopeId}/teams/{teamId}/invoke/{endpointId}:stream");
        endpoints.Should().Contain("/api/scopes/{scopeId}/teams/{teamId}/invoke/{endpointId}");
        endpoints.Should().Contain("/api/scopes/{scopeId}/runs");
        endpoints.Should().Contain("/api/scopes/{scopeId}/runs/{runId}");
        endpoints.Should().Contain("/api/scopes/{scopeId}/runs/{runId}/audit");
        endpoints.Should().Contain("/api/scopes/{scopeId}/runs/{runId}:resume");
        endpoints.Should().Contain("/api/scopes/{scopeId}/runs/{runId}:signal");
        endpoints.Should().Contain("/api/scopes/{scopeId}/runs/{runId}:stop");
        endpoints.Should().Contain("/api/scopes/{scopeId}/services");
        endpoints.Should().Contain("/api/scopes/{scopeId}/services/{serviceId}/invoke/{endpointId}:stream");
        endpoints.Should().Contain("/api/scopes/{scopeId}/services/{serviceId}/revisions/{revisionId}");
        endpoints.Should().Contain("/api/scopes/{scopeId}/services/{serviceId}/revisions/{revisionId}:retire");
        endpoints.Should().Contain("/api/scopes/{scopeId}/services/{serviceId}/runs");
        endpoints.Should().Contain("/api/scopes/{scopeId}/services/{serviceId}/runs/{runId}");
        endpoints.Should().Contain("/api/scopes/{scopeId}/services/{serviceId}/runs/{runId}/audit");
    }

    [Fact]
    public async Task AddGAgentServiceCapabilityBundle_ShouldStartStandaloneWithoutMainnetWorkflowProviders()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = false;
            options.ValidateScopes = false;
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GAgentService:Demo:Enabled"] = "false",
            ["Projection:Document:Providers:InMemory:Enabled"] = "true",
            ["Projection:Document:Providers:Elasticsearch:Enabled"] = "false",
            ["Projection:Graph:Providers:InMemory:Enabled"] = "true",
            ["Projection:Graph:Providers:Neo4j:Enabled"] = "false",
            ["Projection:Policies:Environment"] = "Development",
        });

        builder.AddAevatarDefaultHost(options =>
        {
            options.AllowLocalFileSecretsStore = false;
            options.ServiceName = "Aevatar.GAgentService.StandaloneStartup.Tests";
            options.EnableConnectorBootstrap = false;
            options.EnableHealthEndpoints = false;
            options.MapRootHealthEndpoint = false;
            options.EnableOpenApiDocument = false;
            options.AutoMapCapabilities = false;
        });
        builder.Services.AddSingleton<ILLMProviderFactory, UnusedLlmProviderFactory>();
        builder.Services.AddSingleton<IAgentToolExecutionPort, UnusedAgentToolExecutionPort>();
        builder.AddGAgentServiceCapabilityBundle();
        UseRepositoryWorkflowDefinitions(builder.Services);

        await using var app = builder.Build();
        app.MapAevatarCapabilities();

        await app.StartAsync();

        app.Services.GetRequiredService<IProjectionWriteDispatcher<WorkflowCatalogCurrentStateDocument>>()
            .Should()
            .NotBeNull();
        AssertNoWorkflowCapabilitiesStartupArtifactServices(builder.Services);
    }

    [Fact]
    public async Task AddGAgentServiceCapabilityBundle_WithWorkflowPlatform_ShouldResolveExternalApprovalContinuationProjection()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = false;
            options.ValidateScopes = false;
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GAgentService:Demo:Enabled"] = "false",
            ["Projection:Document:Providers:InMemory:Enabled"] = "true",
            ["Projection:Document:Providers:Elasticsearch:Enabled"] = "false",
            ["Projection:Graph:Providers:InMemory:Enabled"] = "true",
            ["Projection:Graph:Providers:Neo4j:Enabled"] = "false",
            ["Projection:Policies:Environment"] = "Development",
        });

        builder.AddAevatarDefaultHost(options =>
        {
            options.AllowLocalFileSecretsStore = false;
            options.ServiceName = "Aevatar.GAgentService.WorkflowProjectionStartup.Tests";
            options.EnableConnectorBootstrap = false;
            options.EnableHealthEndpoints = false;
            options.MapRootHealthEndpoint = false;
            options.EnableOpenApiDocument = false;
            options.AutoMapCapabilities = false;
        });
        builder.AddAevatarPlatform(options =>
        {
            options.EnableAIFeatures = false;
            options.EnableScriptingCapability = false;
        });
        builder.Services.AddSingleton<ILLMProviderFactory, UnusedLlmProviderFactory>();
        builder.Services.AddSingleton<IAgentToolExecutionPort, UnusedAgentToolExecutionPort>();
        builder.AddGAgentServiceCapabilityBundle();
        UseRepositoryWorkflowDefinitions(builder.Services);

        await using var app = builder.Build();
        app.MapAevatarCapabilities();

        await app.StartAsync();

        app.Services.GetRequiredService<IProjectionDocumentReader<WorkflowExternalApprovalContinuationDocument, string>>()
            .Should()
            .NotBeNull();
        app.Services.GetRequiredService<IProjectionWriteDispatcher<WorkflowExternalApprovalContinuationDocument>>()
            .Should()
            .NotBeNull();
        app.Services.GetRequiredService<WorkflowExternalApprovalContinuationLookupPort>()
            .Should()
            .NotBeNull();
        app.Services.GetRequiredService<WorkflowExternalApprovalContinuationProjector>()
            .Should()
            .NotBeNull();
        AssertNoWorkflowCapabilitiesStartupArtifactServices(builder.Services);
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
    public void AddGAgentServiceProjectionReadModelProviders_ShouldFillMissingReadersWhenPartialRegistrationExists()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddGAgentServiceProjection();
        services.AddInMemoryDocumentProjectionStore<ServiceCatalogReadModel, string>(
            keySelector: static readModel => readModel.Id,
            keyFormatter: static key => key,
            defaultSortSelector: static readModel => readModel.UpdatedAt);

        services.AddGAgentServiceProjectionReadModelProviders(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IProjectionDocumentReader<ServiceRolloutCommandObservationReadModel, string>>().Should().NotBeNull();
        provider.GetRequiredService<IProjectionDocumentReader<UserConfigCurrentStateDocument, string>>().Should().NotBeNull();
        provider.GetRequiredService<IProjectionDocumentReader<WorkflowCatalogCurrentStateDocument, string>>().Should().NotBeNull();
        AssertNoWorkflowCapabilitiesStartupArtifactServices(services);
        services.Count(x => x.ServiceType == typeof(IProjectionDocumentReader<ServiceCatalogReadModel, string>)).Should().Be(1);
    }

    [Fact]
    public void AddGAgentServiceProjectionReadModelProviders_ShouldRejectPartialRegistrationFromDifferentProvider()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
                ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://localhost:9200",
                ["Projection:Document:Providers:InMemory:Enabled"] = "false",
            })
            .Build();

        services.AddGAgentServiceProjection();
        services.AddInMemoryDocumentProjectionStore<ServiceCatalogReadModel, string>(
            keySelector: static readModel => readModel.Id,
            keyFormatter: static key => key,
            defaultSortSelector: static readModel => readModel.UpdatedAt);

        var act = () => services.AddGAgentServiceProjectionReadModelProviders(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ServiceCatalogReadModel*different provider*");
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
        provider.GetRequiredService<IProjectionDocumentReader<ServiceRolloutCommandObservationReadModel, string>>().Should().NotBeNull();
        provider.GetRequiredService<IProjectionDocumentReader<GAgentRunTerminalReadModel, string>>().Should().NotBeNull();
        provider.GetRequiredService<IProjectionWriteDispatcher<WorkflowCatalogCurrentStateDocument>>().Should().NotBeNull();
        provider.GetRequiredService<IProjectionDocumentReader<WorkflowCatalogCurrentStateDocument, string>>().Should().NotBeNull();
        AssertNoWorkflowCapabilitiesStartupArtifactServices(services);
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

    private static bool ServiceTypeContains(Type serviceType, string typeName)
    {
        if (serviceType.Name.Contains(typeName, StringComparison.Ordinal))
            return true;

        return serviceType.IsGenericType &&
               serviceType.GenericTypeArguments.Any(argument =>
                   argument.Name.Contains(typeName, StringComparison.Ordinal));
    }

    private static void UseRepositoryWorkflowDefinitions(IServiceCollection services)
    {
        services.Configure<WorkflowDefinitionFileSourceOptions>(options =>
        {
            options.WorkflowDirectories.Clear();
            options.WorkflowDirectories.Add(Path.Combine(Directory.GetCurrentDirectory(), "workflows"));
        });
    }

    private static void AssertNoWorkflowCapabilitiesStartupArtifactServices(IServiceCollection services)
    {
        // Refactor (iter161-cluster-001 #1257-first):
        //   Old pattern: DI tests referenced the obsolete WorkflowCapabilitiesStartupArtifact type through nameof.
        //   New principle: tests protect against service registration by symbol name without keeping the deleted type alive.
        services.Should().NotContain(service =>
            ServiceTypeContains(service.ServiceType, "WorkflowCapabilitiesStartupArtifact"));
    }

    private sealed class ThrowingLlmProviderFactory : ILLMProviderFactory
    {
        public ILLMProvider GetProvider(string name) =>
            throw new NotSupportedException("The DI test only asserts provider-backed ILlmRunCore composition.");

        public ILLMProvider GetDefault() =>
            throw new NotSupportedException("The DI test only asserts provider-backed ILlmRunCore composition.");

        public IReadOnlyList<string> GetAvailableProviders() => [];
    }

    private sealed class UnusedLlmProviderFactory : ILLMProviderFactory
    {
        public ILLMProvider GetProvider(string name) =>
            throw new InvalidOperationException("The hosting startup test must not execute LLM requests.");

        public ILLMProvider GetDefault() =>
            throw new InvalidOperationException("The hosting startup test must not execute LLM requests.");

        public IReadOnlyList<string> GetAvailableProviders() => [];
    }

    private sealed class UnusedAgentToolExecutionPort : IAgentToolExecutionPort
    {
        public Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("The hosting composition test must not execute agent tools.");
    }

    private sealed class UnusedExternalIdentityBindingQueryPort : IExternalIdentityBindingQueryPort
    {
        public Task<BindingId?> ResolveAsync(ExternalSubjectRef externalSubject, CancellationToken ct = default) =>
            throw new NotSupportedException("The DI test only resolves the scheduled credential-admission adapter.");
    }
}
