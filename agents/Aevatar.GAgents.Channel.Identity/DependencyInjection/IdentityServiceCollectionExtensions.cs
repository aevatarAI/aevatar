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
/// DI extensions for the Channel.Identity module. Split into two methods
/// so the host composition root owns the document-store wiring (ES vs
/// InMemory) and the agent module only registers actors / projectors /
/// broker / slash-commands (PR #521 review glm-5.1 on the agent-level
/// projection-provider coupling).
/// </summary>
public static class IdentityServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Channel.Identity stack: per-binding projection +
    /// query / readiness ports, the cluster-singleton OAuth client
    /// projection + provider, the production NyxID broker, the OAuth
    /// client bootstrap service, and the slash-command handlers.
    /// Document stores are NOT wired here — the host composition root
    /// chooses ES vs InMemory and calls
    /// <see cref="AddChannelIdentityProjectionStores"/> alongside this
    /// method (or wires its own custom store). Caller must additionally
    /// call <c>MapIdentityOAuthEndpoints</c> on the endpoint route builder.
    /// </summary>
    public static IServiceCollection AddChannelIdentity(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Guard against accidental double-registration. Most calls below use
        // TryAdd*, but AddHttpClient / AddHostedService / AddOptions /
        // AddProjection* helpers are NOT idempotent — calling this method
        // twice would register the bootstrap hosted service twice (two DCR
        // attempts on startup) and replace named-client config silently. The
        // sentinel keys off the bootstrap service since it is unique to this
        // module.
        if (services.Any(static d => d.ImplementationType == typeof(AevatarOAuthClientBootstrapService)))
            return services;

        // ─── Shared projection runtime infrastructure ───
        services.AddProjectionReadModelRuntime();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();
        services.TryAddSingleton(sp => TimeProvider.System);

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

        // ─── Broker (self-bootstrapping, no appsettings dependency) ───
        // Register broker as a *singleton* and inject IHttpClientFactory so
        // each call resolves a fresh HttpClient backed by the factory's
        // rotating handler pool. The earlier shape — AddHttpClient<T>()
        // (transient) re-exposed via TryAddSingleton<I> — pinned the first
        // resolved HttpClient + HttpMessageHandler inside the singleton and
        // silently defeated the 2-min handler rotation, so long-running
        // silos would never pick up DNS / TLS-cert changes.
        services.AddOptions<NyxIdBrokerOptions>();
        services.TryAddSingleton<StateTokenCodec>();
        services.AddHttpClient(NyxIdRemoteCapabilityBroker.HttpClientName);
        services.TryAddSingleton<NyxIdRemoteCapabilityBroker>();
        services.TryAddSingleton<INyxIdCapabilityBroker>(sp => sp.GetRequiredService<NyxIdRemoteCapabilityBroker>());
        services.TryAddSingleton<INyxIdBrokerCallbackClient>(sp => sp.GetRequiredService<NyxIdRemoteCapabilityBroker>());

        // ─── OAuth client bootstrap (self-registration via NyxID DCR) ───
        // DCR registrar stays as AddHttpClient<T>() (transient): the
        // AevatarOAuthClientGAgent resolves it per command via Services.GetService<>()
        // so the typed-client pattern's per-resolution handler rotation works
        // as designed.
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

    /// <summary>
    /// Wires the per-binding + cluster-singleton projection document stores
    /// for Channel.Identity. Picks Elasticsearch when configuration enables
    /// it under the <c>ChannelIdentity</c> store name, else falls back to
    /// in-memory (suitable for tests / single-host dev). Hosts that want a
    /// custom store implementation skip this method and register their own
    /// <see cref="IProjectionDocumentStore{TDocument,TKey}"/> directly.
    /// </summary>
    /// <remarks>
    /// Lives in this same project as <see cref="AddChannelIdentity"/> for
    /// discoverability, but split out so the host (Mainnet, demo, CLI) is
    /// the explicit owner of the ES vs InMemory choice — the agent module
    /// never makes that decision on the host's behalf.
    /// </remarks>
    public static IServiceCollection AddChannelIdentityProjectionStores(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var useElasticsearch = ElasticsearchProjectionConfiguration.IsEnabled(
            configuration,
            storeName: "ChannelIdentity");

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

        return services;
    }
}
