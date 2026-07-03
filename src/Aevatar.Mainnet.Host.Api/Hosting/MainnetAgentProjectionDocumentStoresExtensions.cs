using Aevatar.ChatRouting.Core;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Device;
using Aevatar.GAgents.Scheduled;
using Aevatar.GAgents.StatusDashboard;
using Aevatar.GAgents.StreamingProxy;
using Aevatar.Workflow.Projection.ReadModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.Mainnet.Host.Api.Hosting;

public static class MainnetAgentProjectionDocumentStoresExtensions
{
    // Engine labels surfaced by the read-model inventory (GET /api/cqrs/readmodels). The document
    // store/engine choice is a per-type branch in this file (ES vs InMemory), so the inventory
    // descriptors are declared right next to the store registrations that pick the engine.
    private const string ElasticsearchEngineLabel = "Elasticsearch";
    private const string InMemoryEngineLabel = "dev/InMemory";

    public static IServiceCollection AddMainnetAgentProjectionDocumentStores(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var documentProvider = ProjectionDocumentProviderConfiguration.Resolve(
            configuration,
            "MainnetAgentProjectionDocumentStores");

        if (documentProvider.ElasticsearchEnabled)
        {
            AddElasticsearchStores(services, configuration);
            services.Configure<AevatarOAuthClientEsAclOptions>(options =>
            {
                options.EnforcementMode = AevatarOAuthClientEsAclEnforcementMode.Strict;
            });
            // Replace the identity module's default Unavailable probe with a real
            // HTTP-backed probe that inspects the Elasticsearch security API using
            // the SAME endpoint/credentials the projection store uses, so the ACL
            // startup guard verifies the cluster instead of self-attesting a flag.
            services.Replace(ServiceDescriptor.Singleton<IOAuthClientEsAclProbe>(
                sp => new HttpOAuthClientEsAclProbe(
                    ProjectionDocumentProviderConfiguration.BindRequiredElasticsearchOptions(configuration),
                    sp.GetService<ILogger<HttpOAuthClientEsAclProbe>>())));
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService, AevatarOAuthClientEsAclStartupGuard>());
            // Self-heal projection-index schema drift at startup (reindex + atomic alias swap)
            // so a deploy that bumps a read-model schema doesn't 500 reads (e.g. /ws/voice).
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService, ElasticsearchProjectionIndexReconcileHostedService>());
            AddReadModelInventoryDescriptors(services, ElasticsearchEngineLabel);
        }
        else
        {
            AddInMemoryStores(services);
            AddReadModelInventoryDescriptors(services, InMemoryEngineLabel);
        }

        // Assembles the read-model inventory from the opt-in descriptors registered above; reads the
        // materialized read-model stores only (the read-write-separation invariant).
        services.TryAddSingleton<IProjectionReadModelInventoryQueryPort, ProjectionReadModelInventoryQueryPort>();

        return services;
    }

    private static void AddElasticsearchStores(IServiceCollection services, IConfiguration configuration)
    {
        TryAddElasticsearchStore<ChannelBotRegistrationDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<ConversationDeliveryCurrentStateDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<ProjectionScopeStatusDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<ExternalIdentityBindingDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<AevatarOAuthClientDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<ChatRoutePolicyCurrentStateDocument>(services, configuration, static document => document.ActorId);
        TryAddElasticsearchStore<DeviceRegistrationDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<UserAgentCatalogDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<SkillRunnerExecutionDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<UserAgentCatalogNyxCredentialDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<HealthProbeTargetDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<WorkflowExternalApprovalContinuationDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<StreamingProxyChatSessionTerminalSnapshot>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<StreamingProxyRoomParticipantsSnapshot>(services, configuration, static document => document.Id);
    }

    private static void AddInMemoryStores(IServiceCollection services)
    {
        TryAddInMemoryStore<ChannelBotRegistrationDocument>(services, static document => document.Id);
        TryAddInMemoryStore<ConversationDeliveryCurrentStateDocument>(services, static document => document.Id);
        TryAddInMemoryStore<ProjectionScopeStatusDocument>(services, static document => document.Id);
        TryAddInMemoryStore<ExternalIdentityBindingDocument>(services, static document => document.Id);
        TryAddInMemoryStore<AevatarOAuthClientDocument>(services, static document => document.Id);
        TryAddInMemoryStore<ChatRoutePolicyCurrentStateDocument>(services, static document => document.ActorId);
        TryAddInMemoryStore<DeviceRegistrationDocument>(services, static document => document.Id);
        TryAddInMemoryStore<UserAgentCatalogDocument>(services, static document => document.Id);
        TryAddInMemoryStore<SkillRunnerExecutionDocument>(services, static document => document.Id);
        TryAddInMemoryStore<UserAgentCatalogNyxCredentialDocument>(services, static document => document.Id);
        TryAddInMemoryStore<HealthProbeTargetDocument>(services, static document => document.Id);
        TryAddInMemoryStore<WorkflowExternalApprovalContinuationDocument>(services, static document => document.Id);
        TryAddInMemoryStore(
            services,
            static (StreamingProxyChatSessionTerminalSnapshot document) => document.Id,
            static document => document.UpdatedAt.ToDateTimeOffset());
        TryAddInMemoryStore(
            services,
            static (StreamingProxyRoomParticipantsSnapshot document) => document.Id,
            static document => document.UpdatedAt.ToDateTimeOffset());
    }

    // Opt-in read-model inventory descriptors, one per materialized document read-model registered above.
    // shape = Document when backed by Elasticsearch, Memory when backed by the InMemory dev store; the
    // engine label is supplied by the caller (the branch that picked the provider). actorKind is the
    // best-available GAgent/actor kind whose current state each read-model replicates.
    private static void AddReadModelInventoryDescriptors(IServiceCollection services, string engineLabel)
    {
        var shape = ReferenceEquals(engineLabel, ElasticsearchEngineLabel)
            ? ProjectionReadModelSinkShape.Document
            : ProjectionReadModelSinkShape.Memory;

        TryAddReadModelDescriptor<ChannelBotRegistrationDocument>(services, "channel-bot-registration", "ChannelBotGAgent", engineLabel, shape);
        TryAddReadModelDescriptor<ConversationDeliveryCurrentStateDocument>(services, "conversation-delivery-current-state", "ConversationGAgent", engineLabel, shape);
        TryAddReadModelDescriptor<ProjectionScopeStatusDocument>(services, "projection-scope-status", "ProjectionMaterializationScopeGAgent", engineLabel, shape);
        TryAddReadModelDescriptor<ExternalIdentityBindingDocument>(services, "external-identity-binding", "ExternalIdentityBindingGAgent", engineLabel, shape);
        TryAddReadModelDescriptor<AevatarOAuthClientDocument>(services, "aevatar-oauth-client", "AevatarOAuthClientGAgent", engineLabel, shape);
        TryAddReadModelDescriptor<ChatRoutePolicyCurrentStateDocument>(services, "chat-route-policy-current-state", "ChatRoutePolicyGAgent", engineLabel, shape);
        TryAddReadModelDescriptor<DeviceRegistrationDocument>(services, "device-registration", "DeviceGAgent", engineLabel, shape);
        TryAddReadModelDescriptor<UserAgentCatalogDocument>(services, "user-agent-catalog", "UserAgentCatalogGAgent", engineLabel, shape);
        TryAddReadModelDescriptor<SkillRunnerExecutionDocument>(services, "skill-runner-execution", "SkillRunnerGAgent", engineLabel, shape);
        TryAddReadModelDescriptor<UserAgentCatalogNyxCredentialDocument>(services, "user-agent-catalog-nyx-credential", "UserAgentCatalogGAgent", engineLabel, shape);
        TryAddReadModelDescriptor<HealthProbeTargetDocument>(services, "health-probe-target", "HealthProbeGAgent", engineLabel, shape);
        // WorkflowExternalApprovalContinuationDocument is inventoried at the workflow store-registration
        // site (it owns that read-model); registering it here too would double-count it in the inventory.
        TryAddReadModelDescriptor<StreamingProxyChatSessionTerminalSnapshot>(services, "streaming-proxy-chat-session", "StreamingProxyChatSessionGAgent", engineLabel, shape);
        TryAddReadModelDescriptor<StreamingProxyRoomParticipantsSnapshot>(services, "streaming-proxy-room-participants", "StreamingProxyRoomGAgent", engineLabel, shape);
    }

    // Registers a single inventory descriptor that delegates to the read-model's already-registered
    // document reader. The closed concrete descriptor type is the idempotence key; TryAddEnumerable
    // cannot be used for the interface factory because factory descriptors have no distinct
    // implementation type and are indistinguishable when more than one read-model is registered.
    private static void TryAddReadModelDescriptor<TReadModel>(
        IServiceCollection services,
        string name,
        string actorKind,
        string engineLabel,
        ProjectionReadModelSinkShape shape)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        if (services.Any(static descriptor =>
                descriptor.ServiceType == typeof(ProjectionDocumentReadModelDescriptor<TReadModel>)))
            return;

        services.AddSingleton<ProjectionDocumentReadModelDescriptor<TReadModel>>(sp =>
            new ProjectionDocumentReadModelDescriptor<TReadModel>(
                name,
                shape,
                engineLabel,
                actorKind,
                sp.GetRequiredService<IProjectionDocumentReader<TReadModel, string>>()));
        services.AddSingleton<IProjectionReadModelDescriptor>(sp =>
            sp.GetRequiredService<ProjectionDocumentReadModelDescriptor<TReadModel>>());
    }

    private static void TryAddElasticsearchStore<TReadModel>(
        IServiceCollection services,
        IConfiguration configuration,
        Func<TReadModel, string> keySelector)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        EnsureCompatibleDocumentReaderProvider<TReadModel>(services, ProjectionDocumentProviderKind.Elasticsearch);
        if (HasDocumentReaderForProvider<TReadModel>(services, ProjectionDocumentProviderKind.Elasticsearch))
            return;

        services.AddElasticsearchDocumentProjectionStore<TReadModel, string>(
            optionsFactory: _ => ProjectionDocumentProviderConfiguration.BindRequiredElasticsearchOptions(configuration),
            metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<TReadModel>>().Metadata,
            keySelector: keySelector,
            keyFormatter: static key => key);
    }

    private static void TryAddInMemoryStore<TReadModel>(
        IServiceCollection services,
        Func<TReadModel, string> keySelector,
        Func<TReadModel, object?>? defaultSortSelector = null)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        EnsureCompatibleDocumentReaderProvider<TReadModel>(services, ProjectionDocumentProviderKind.InMemory);
        if (HasDocumentReaderForProvider<TReadModel>(services, ProjectionDocumentProviderKind.InMemory))
            return;

        services.AddInMemoryDocumentProjectionStore<TReadModel, string>(
            keySelector: keySelector,
            keyFormatter: static key => key,
            defaultSortSelector: defaultSortSelector);
    }

    private static void EnsureCompatibleDocumentReaderProvider<TReadModel>(
        IServiceCollection services,
        ProjectionDocumentProviderKind providerKind)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        if (!HasAnyDocumentReader<TReadModel>(services))
            return;
        if (HasDocumentReaderForProvider<TReadModel>(services, providerKind))
            return;

        throw new InvalidOperationException(
            $"Projection document reader for {typeof(TReadModel).Name} is already registered with a different provider.");
    }

    private static bool HasAnyDocumentReader<TReadModel>(IServiceCollection services)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        return services.Any(static descriptor =>
            descriptor.ServiceType == typeof(IProjectionDocumentReader<TReadModel, string>));
    }

    private static bool HasDocumentReaderForProvider<TReadModel>(
        IServiceCollection services,
        ProjectionDocumentProviderKind providerKind)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        return providerKind switch
        {
            ProjectionDocumentProviderKind.Elasticsearch => services.Any(static descriptor =>
                descriptor.ServiceType == typeof(ElasticsearchProjectionDocumentStore<TReadModel, string>)),
            ProjectionDocumentProviderKind.InMemory => services.Any(static descriptor =>
                descriptor.ServiceType == typeof(InMemoryProjectionDocumentStore<TReadModel, string>)),
            _ => false,
        };
    }
}
