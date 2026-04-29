using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.Channel.Abstractions.Slash;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Broker;
using Aevatar.GAgents.Channel.Identity.Slash;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.Channel.Identity.DependencyInjection;

/// <summary>
/// DI extensions for the Channel.Identity module. The cluster-singleton
/// OAuth client provisioning, broker, and projection chain are wired via
/// <see cref="AddChannelIdentity"/>; the broker is self-bootstrapping (no
/// appsettings dependency).
/// </summary>
public static class IdentityServiceCollectionExtensions
{
    /// <summary>
    /// Registers the full Channel.Identity stack: per-binding projection +
    /// query / readiness ports, the cluster-singleton OAuth client
    /// projection + provider, the production NyxID broker, and the OAuth
    /// client bootstrap service. Caller must additionally call
    /// <c>MapIdentityOAuthEndpoints</c> on the endpoint route builder.
    /// </summary>
    public static IServiceCollection AddChannelIdentity(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // ─── Shared projection runtime infrastructure ───
        services.AddProjectionReadModelRuntime();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();
        services.TryAddSingleton(sp => TimeProvider.System);

        var useElasticsearch = ElasticsearchProjectionConfiguration.IsEnabled(
            configuration,
            storeName: "ChannelIdentity");

        // ─── Per-binding projection (one document per ExternalSubjectRef) ───
        services.AddProjectionMaterializationRuntimeCore<
            ExternalIdentityBindingMaterializationContext,
            ExternalIdentityBindingMaterializationRuntimeLease,
            ProjectionMaterializationScopeGAgent<ExternalIdentityBindingMaterializationContext>>(
            static scopeKey => new ExternalIdentityBindingMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new ExternalIdentityBindingMaterializationRuntimeLease(context));
        services.AddCurrentStateProjectionMaterializer<
            ExternalIdentityBindingMaterializationContext,
            ExternalIdentityBindingProjector>();
        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<ExternalIdentityBindingDocument>,
            ExternalIdentityBindingDocumentMetadataProvider>();
        services.TryAddSingleton<IExternalIdentityBindingQueryPort, ExternalIdentityBindingProjectionQueryPort>();
        services.TryAddSingleton<IProjectionReadinessPort, ExternalIdentityBindingProjectionReadinessPort>();

        // ─── Cluster-singleton OAuth client projection ───
        services.AddProjectionMaterializationRuntimeCore<
            AevatarOAuthClientMaterializationContext,
            AevatarOAuthClientMaterializationRuntimeLease,
            ProjectionMaterializationScopeGAgent<AevatarOAuthClientMaterializationContext>>(
            static scopeKey => new AevatarOAuthClientMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new AevatarOAuthClientMaterializationRuntimeLease(context));
        services.AddCurrentStateProjectionMaterializer<
            AevatarOAuthClientMaterializationContext,
            AevatarOAuthClientProjector>();
        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<AevatarOAuthClientDocument>,
            AevatarOAuthClientDocumentMetadataProvider>();
        services.TryAddSingleton<IAevatarOAuthClientProvider, AevatarOAuthClientProjectionProvider>();

        // ─── Document stores (ES vs InMemory) ───
        if (useElasticsearch)
        {
            services.AddElasticsearchDocumentProjectionStore<ExternalIdentityBindingDocument, string>(
                optionsFactory: _ => ElasticsearchProjectionConfiguration.BindOptions(configuration!),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<ExternalIdentityBindingDocument>>().Metadata,
                keySelector: static doc => doc.Id,
                keyFormatter: static key => key);
            services.AddElasticsearchDocumentProjectionStore<AevatarOAuthClientDocument, string>(
                optionsFactory: _ => ElasticsearchProjectionConfiguration.BindOptions(configuration!),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<AevatarOAuthClientDocument>>().Metadata,
                keySelector: static doc => doc.Id,
                keyFormatter: static key => key);
        }
        else
        {
            services.AddInMemoryDocumentProjectionStore<ExternalIdentityBindingDocument, string>(
                static doc => doc.Id, static key => key);
            services.AddInMemoryDocumentProjectionStore<AevatarOAuthClientDocument, string>(
                static doc => doc.Id, static key => key);
        }

        // ─── Broker (self-bootstrapping, no appsettings dependency) ───
        services.AddOptions<NyxIdBrokerOptions>();
        services.TryAddSingleton<StateTokenCodec>();
        services.AddHttpClient<NyxIdRemoteCapabilityBroker>();
        services.TryAddSingleton<INyxIdCapabilityBroker>(sp => sp.GetRequiredService<NyxIdRemoteCapabilityBroker>());
        services.TryAddSingleton<INyxIdBrokerCallbackClient>(sp => sp.GetRequiredService<NyxIdRemoteCapabilityBroker>());

        // ─── OAuth client bootstrap (self-registration via NyxID DCR) ───
        services.AddHttpClient<NyxIdDynamicClientRegistrationClient>();
        services.AddHostedService<AevatarOAuthClientBootstrapService>();

        // ─── Webhook validators ───
        services.TryAddSingleton<Endpoints.BrokerRevocationWebhookValidator>();

        // ─── Slash command handlers (issue #513 phases 1, 4, 6) ───
        // Identity owns /init, /unbind, /whoami; other modules can register
        // additional handlers (e.g. NyxidChat for /model) by adding their own
        // IChannelSlashCommandHandler implementations to DI. The registry
        // validates uniqueness at first resolution and throws fail-fast on
        // duplicate Name/Aliases so a future module can't silently shadow an
        // existing command.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChannelSlashCommandHandler, InitChannelSlashCommandHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChannelSlashCommandHandler, UnbindChannelSlashCommandHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChannelSlashCommandHandler, WhoamiChannelSlashCommandHandler>());
        services.TryAddSingleton<ChannelSlashCommandRegistry>();

        return services;
    }
}
