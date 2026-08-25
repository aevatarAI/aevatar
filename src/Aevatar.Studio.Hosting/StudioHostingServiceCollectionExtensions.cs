using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.ContentArtifacts.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Hosting.DependencyInjection;
using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Delivery;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.DependencyInjection;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Application.Studio.WorkflowBoards;
using Aevatar.Studio.Hosting.Auth;
using Aevatar.Studio.Hosting.Controllers;
using Aevatar.Studio.Hosting.ContentArtifacts;
using Aevatar.Studio.Hosting.Endpoints;
using Aevatar.Studio.Hosting.WorkflowBoards;
using Aevatar.Studio.Hosting.WorkOrders;
using Aevatar.Studio.Hosting.WorkflowDeliveries;
using Aevatar.Studio.Hosting.NyxId;
using Aevatar.Studio.Application.WorkflowTemplates;
using Aevatar.Studio.Infrastructure.DependencyInjection;
using Aevatar.Studio.Infrastructure.ScopeResolution; // DefaultAppScopeResolver
using Aevatar.Studio.Projection.DependencyInjection;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Aevatar.GAgents.Channel.Identity.Abstractions;

namespace Aevatar.Studio.Hosting;

internal static class StudioHostingServiceCollectionExtensions
{
    internal static IServiceCollection AddStudioHostingCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.TryAddSingleton(configuration);
        services.Configure<StudioHostingOptions>(configuration.GetSection(StudioHostingOptions.SectionName));
        var deliverySection = configuration.GetSection(WorkflowDeliveryOptions.SectionName);
        services.AddOptions<WorkflowDeliveryOptions>()
            .Bind(
                deliverySection,
                binder => binder.ErrorOnUnknownConfiguration = true)
            // A present-but-invalid console origin would silently remove every customer link.
            .Validate(
                options => string.IsNullOrWhiteSpace(options.ConsoleWebBaseUrl) ||
                    WorkflowDeliveryConsoleLink.TryNormalizeBaseUrl(options.ConsoleWebBaseUrl, out _),
                $"{WorkflowDeliveryOptions.SectionName}:{nameof(WorkflowDeliveryOptions.ConsoleWebBaseUrl)} " +
                "must be an absolute HTTPS origin (or a loopback HTTP origin) without userinfo, query, or fragment.")
            .ValidateOnStart();
        services.Configure<UserLlmSettingsOptions>(configuration.GetSection("Aevatar:Studio:UserLlmSettings"));
        services.AddControllers()
            .AddApplicationPart(typeof(EditorController).Assembly)
            .AddJsonOptions(json =>
            {
                json.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
                json.JsonSerializerOptions.DefaultIgnoreCondition =
                    System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
                json.JsonSerializerOptions.AllowDuplicateProperties = false;
            });
        services.AddHttpContextAccessor();
        services.AddHttpClient();
        services.TryAddScoped<IAppAuthProfileResolver, NyxIdAppAuthProfileResolver>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddHostedService<WorkflowDeliveryPackageCatalogStartupProbe>();
        services.TryAddSingleton<
            IContentArtifactBackingContentPort,
            WorkflowFileContentArtifactBackingContentPort>();
        services.AddSingleton<IAppScopeResolver, DefaultAppScopeResolver>();
        services.AddStudioApplication();
        AddWorkOrderExecutionWorker(services, configuration);
        AddWorkflowDeliveryContinuationWorker(services, configuration);
        services.Configure<NyxIdLlmCatalogCacheOptions>(
            configuration.GetSection(NyxIdLlmCatalogCacheOptions.SectionName));
        services.TryAddSingleton<
            INyxIdModelSourceInventoryPort,
            NyxIdModelSourceInventoryHttpClient>();
        services.TryAddSingleton<
            INyxIdModelDiscoveryPort,
            NyxIdModelDiscoveryHttpClient>();
        services.TryAddSingleton<
            INyxIdUserServiceInventoryPort,
            NyxIdWorkflowDeliveryConnectionInventoryPort>();
        services.TryAddSingleton<NyxIdLlmCatalogHttpClient>();
        services.TryAddSingleton<IUserLlmCatalogPort>(sp => new CachedNyxIdLlmCatalogPort(
            sp.GetRequiredService<NyxIdLlmCatalogHttpClient>(),
            configuration,
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<NyxIdLlmCatalogCacheOptions>>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CachedNyxIdLlmCatalogPort>>()));
        services.AddStudioInfrastructure(configuration);
        services.AddStudioProjectionComponents(configuration);
        services.AddStudioProjectionReadModelProviders(configuration);
        services.AddNyxIdAuthorizationCatalogHosting(configuration);
        services.TryAddSingleton<INyxIdCatalogAccessLifecyclePort>(sp => new NyxIdCatalogAccessLifecyclePort(
            sp.GetRequiredService<INyxIdAuthorizationCatalogCommandPort>(),
            configuration,
            sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.AddWorkflowBoardExecutionProjectionAdapter();
        return services;
    }

    internal static IServiceCollection AddStudioBridgeServices(this IServiceCollection services)
    {
        services.AddSingleton(sp => new AppScopedWorkflowService(
            sp.GetRequiredService<IWorkflowYamlDocumentService>(),
            sp.GetRequiredService<IWorkflowDefinitionParser>(),
            sp.GetService<IStudioWorkspaceQueryPort>(),
            sp.GetService<IStudioWorkspaceCommandPort>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AppScopedWorkflowService>>()));
        services.AddSingleton<IAppScopedWorkflowCatalogueService, AppScopedWorkflowCatalogueService>();
        services.AddSingleton<PublicWorkflowTemplateService>();
        services.TryAddSingleton<
            IStudioMemberWorkflowDraftProvisioningPort,
            StudioMemberWorkflowDraftProvisioningService>();
        services.AddSingleton(sp => new AppScopedScriptService(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetService<IScopeScriptQueryPort>(),
            sp.GetService<IScopeScriptCommandPort>(),
            sp.GetService<IScopeScriptSaveObservationPort>(),
            sp.GetService<Aevatar.Scripting.Core.Ports.IScriptDefinitionSnapshotPort>(),
            sp.GetService<Aevatar.Scripting.Application.IScriptEvolutionApplicationService>(),
            sp.GetService<Aevatar.Scripting.Core.Ports.IScriptCatalogQueryPort>(),
            sp.GetService<Aevatar.Scripting.Core.Ports.IScriptEvolutionDecisionReadPort>(),
            sp.GetService<Aevatar.Scripting.Core.Ports.IScriptingActorAddressResolver>(),
            sp.GetService<IScriptRuntimeActivityQueryPort>()));
        return services;
    }

    internal static IServiceCollection AddStudioAuthoringServices(this IServiceCollection services)
    {
        // Refactor (iter21/cluster-001):
        //   Old pattern: Host registered fake authoring actors, process gates, and ChatRuntime factories.
        //   New principle: Host keeps only protocol helpers; Application/Infrastructure own authoring preview behavior.
        services.AddSingleton<ScriptEditorValidationService>();
        return services;
    }

    internal static IServiceCollection AddWorkflowBoardExecutionProjectionAdapter(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (HasWorkflowBoardExecutionDocumentReader(services))
            services.TryAddSingleton<IWorkflowBoardExecutionQueryPort, WorkflowProjectionBoardExecutionQueryPort>();

        return services;
    }

    private static bool HasWorkflowBoardExecutionDocumentReader(IServiceCollection services) =>
        services.Any(static descriptor =>
            descriptor.ServiceType == typeof(IProjectionDocumentReader<WorkflowExecutionBoardDocument, string>));

    private static void AddWorkOrderExecutionWorker(
        IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<WorkOrderExecutionWorkerOptions>(
            configuration.GetSection(WorkOrderExecutionWorkerOptions.SectionName));
        services.AddHostedService<WorkOrderExecutionWorker>();
    }

    private static void AddWorkflowDeliveryContinuationWorker(
        IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<WorkflowDeliveryContinuationWorkerOptions>(
            configuration.GetSection(WorkflowDeliveryContinuationWorkerOptions.SectionName));
        services.TryAddSingleton<WorkflowDeliveryContinuationScanner>();
        services.AddHostedService<WorkflowDeliveryContinuationWorker>();
    }
}
