using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.DependencyInjection;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.GAgents.Channel.Identity.Audit;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Configuration.BackendConsole;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.GAgents.Channel.Abstractions.Slash;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Broker;
using Aevatar.GAgents.Channel.Identity.Slash;
using Google.Protobuf;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgents.Channel.Identity.DependencyInjection;

/// <summary>
/// DI extensions for the Channel.Identity module.
/// </summary>
public static class IdentityServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Channel.Identity stack: per-binding projection +
    /// query port, the cluster-singleton OAuth client
    /// projection + provider, the production NyxID broker, the OAuth
    /// client bootstrap service, and the slash-command handlers.
    /// Document stores are wired by the host composition root. Caller must
    /// additionally call <c>MapIdentityOAuthEndpoints</c> on the endpoint
    /// route builder.
    /// </summary>
    public static IServiceCollection AddChannelIdentity(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        // Refactor (iter27/cluster-028-identity-oauth-endpoint):
        //   Old pattern: IdentityOAuthEndpoints + AevatarOAuthClientBootstrapService 直接构造 EventEnvelope 投递,然后在 endpoint 内同步等 projection readiness / rebuild observation / readmodel polling (3-15s timeout + 50-250ms polling),违反 ACK 协议 + query-time projection priming
        //   New principle: 加 module-local CQRS dispatch adapters(ChannelIdentityOAuthCommandDispatch);endpoint inject typed ICommandDispatchService<...>,返回 accepted/pending + status URL,不再等 projection;删 IProjectionReadinessPort/ExternalIdentityBindingProjectionPort/AevatarOAuthClientProjectionPort/AevatarOAuthClientRebuildCoordinator/ProjectionWaitTimeout 等
        ArgumentNullException.ThrowIfNull(services);

        // Guard against accidental double-registration. Most calls below use
        // TryAdd*, but AddHttpClient / AddHostedService / AddOptions /
        // AddProjection* helpers are NOT idempotent — calling this method
        // twice would register the bootstrap hosted service twice (duplicate
        // provisioning dispatches) and replace named-client config silently. The
        // sentinel keys off the bootstrap service since it is unique to this
        // module.
        if (services.Any(static d => d.ImplementationType == typeof(AevatarOAuthClientBootstrapService)))
            return services;

        services.AddAevatarAgentKindRegistry(builder => builder.ScanAssemblies(typeof(ExternalIdentityBindingGAgent).Assembly));

        // ─── Shared projection runtime infrastructure ───
        services.AddProjectionReadModelRuntime();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();
        services.TryAddSingleton(sp => TimeProvider.System);
        services.TryAddSingleton<ProjectionActivationPlanDispatcher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            ICommittedStatePublicationHook,
            CommittedStateProjectionActivationHook>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionActivationPlanProvider,
            ChannelIdentityCommittedStateProjectionActivationPlanProvider>());

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
        services.TryAddSingleton<ExternalIdentityBindingProjectionQueryPort>();
        services.TryAddSingleton<IExternalIdentityBindingQueryPort>(sp =>
            sp.GetRequiredService<ExternalIdentityBindingProjectionQueryPort>());
        services.TryAddSingleton<IOwnerScopeResolver>(sp =>
            sp.GetRequiredService<ExternalIdentityBindingProjectionQueryPort>());

        // ─── Per-NyxID-user managed Codex credential projection ───
        services.AddProjectionMaterializationRuntimeCore<
            ManagedCodexCredentialMaterializationContext,
            ManagedCodexCredentialMaterializationRuntimeLease,
            ProjectionMaterializationScopeGAgent<ManagedCodexCredentialMaterializationContext>>(
            static scopeKey => new ManagedCodexCredentialMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new ManagedCodexCredentialMaterializationRuntimeLease(context));
        services.AddCurrentStateProjectionMaterializer<
            ManagedCodexCredentialMaterializationContext,
            ManagedCodexCredentialProjector>();
        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<ManagedCodexCredentialDocument>,
            ManagedCodexCredentialDocumentMetadataProvider>();
        services.TryAddSingleton<
            IManagedCodexCredentialQueryPort,
            ManagedCodexCredentialProjectionQueryPort>();
        services.TryAddSingleton<
            IManagedCodexCredentialCommandPort,
            ManagedCodexCredentialCommandPort>();
        services.AddEventSinkProjectionRuntimeCore<
            ManagedCodexCredentialReadinessProjectionContext,
            ManagedCodexCredentialReadinessRuntimeLease,
            ManagedCodexCredentialSnapshot,
            ProjectionSessionScopeGAgent<ManagedCodexCredentialReadinessProjectionContext>>(
            static scopeKey => new ManagedCodexCredentialReadinessProjectionContext
            {
                SessionId = scopeKey.SessionId,
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new ManagedCodexCredentialReadinessRuntimeLease(context));
        services.TryAddSingleton<
            IProjectionSessionEventCodec<ManagedCodexCredentialSnapshot>,
            ManagedCodexCredentialSnapshotCodec>();
        services.TryAddSingleton<
            IProjectionSessionEventHub<ManagedCodexCredentialSnapshot>,
            ProjectionSessionEventHub<ManagedCodexCredentialSnapshot>>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ManagedCodexCredentialReadinessProjectionContext>,
            ManagedCodexCredentialReadinessProjector>());
        services.TryAddSingleton<
            IManagedCodexCredentialReadinessObservationPort,
            ManagedCodexCredentialReadinessObservationPort>();

        // ─── Committed-fact audit for external-identity bindings ───
        // Subject-bearing: the actor id embeds the raw external subject, so the
        // translators hash the origin actor id through the host-provided
        // IAuditActorIdentityHasher (AddAuditTrailCore). When no hasher is
        // configured the translators skip rather than leak (fail-open).
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator, ExternalIdentityBoundAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator, ExternalIdentityBindingReplacedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator, ExternalIdentityBindingRevokedAuditTranslator>());
        services.AddAuditCommittedFactMaterializer<ExternalIdentityBindingMaterializationContext>();

        // ─── Cluster-singleton OAuth client projection ───
        // Refactor (iter97/cluster-526): Old pattern: AevatarOAuthClientDocument
        // carries state-token HMAC bytes but ES startup did not require an
        // explicit ACL check. New principle: when ChannelIdentity uses ES,
        // AevatarOAuthClientEsAclStartupGuard always inspects the configured
        // enforcement policy. Warn reports an unconfirmed grant without blocking
        // startup; Strict fails closed unless a live probe confirms the restricted
        // grant and the operator separately attests that it matches grain/event-store
        // internal read access.
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

        // ─── Committed-fact audit (platform governance trail) ───
        // The OAuth client actor is a cluster singleton with a fixed well-known
        // id (no external subject), so its committed events can be audited on the
        // projection-artifact plane without leaking any raw identity. The HMAC
        // key rotation translator records key ids only, never key material.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator, AevatarOAuthClientProvisionedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator, AevatarOAuthClientHmacKeyRotatedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator, AevatarOAuthClientBrokerCapabilityObservedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator, AevatarOAuthClientDriftReconciledAuditTranslator>());
        services.AddAuditCommittedFactMaterializer<AevatarOAuthClientMaterializationContext>();
        var aclOptions = services.AddOptions<AevatarOAuthClientEsAclOptions>();
        if (configuration is not null)
            aclOptions.Bind(configuration.GetSection(AevatarOAuthClientEsAclOptions.SectionName));
        // Default ES ACL probe: reports Unavailable when there is no cluster to
        // inspect. Warn mode logs it; Strict mode fails closed. A host that runs
        // the Elasticsearch projection store replaces this with a real HTTP-backed
        // probe using the same endpoint/credentials as the projection store.
        services.TryAddSingleton<IOAuthClientEsAclProbe, UnavailableOAuthClientEsAclProbe>();

        // Endpoint filter for the operator /rebuild path — rejects unauthenticated
        // callers before model binding/DI resolution kicks in.
        services.TryAddTransient<Endpoints.IdentityOAuthEndpoints.RebuildAuthEndpointFilter>();

        services.AddIdentityOAuthCommandDispatch<CommitBindingCommand, ExternalIdentityBindingGAgent>(
            static command => new ChannelIdentityOAuthCommandTarget(
                command.ExternalSubject.ToActorId(),
                "channel-identity.oauth-callback"));
        services.AddIdentityOAuthCommandDispatch<RevokeBindingCommand, ExternalIdentityBindingGAgent>(
            static command => new ChannelIdentityOAuthCommandTarget(
                command.ExternalSubject.ToActorId(),
                "channel-identity.broker-revocation"));
        services.AddIdentityOAuthCommandDispatch<ReplaceBindingCommand, ExternalIdentityBindingGAgent>(
            static command => new ChannelIdentityOAuthCommandTarget(
                command.ExternalSubject.ToActorId(),
                "channel-identity.oauth-replace"));
        services.AddIdentityOAuthCommandDispatch<RebuildBindingProjectionCommand, ExternalIdentityBindingGAgent>(
            static command => new ChannelIdentityOAuthCommandTarget(
                command.ExternalSubject.ToActorId(),
                "channel-identity.projection-rebuild"));
        services.AddIdentityOAuthCommandDispatch<ObserveBrokerCapabilityCommand, AevatarOAuthClientGAgent>(
            static _ => new ChannelIdentityOAuthCommandTarget(
                AevatarOAuthClientGAgent.WellKnownId,
                "channel-identity.oauth-callback"));
        services.AddIdentityOAuthCommandDispatch<EnsureAevatarOAuthClientProvisionedCommand, AevatarOAuthClientGAgent>(
            static _ => new ChannelIdentityOAuthCommandTarget(
                AevatarOAuthClientGAgent.WellKnownId,
                "channel-identity.oauth-bootstrap"));
        services.AddIdentityOAuthCommandDispatch<ProvisionAevatarOAuthClientCommand, AevatarOAuthClientGAgent>(
            static _ => new ChannelIdentityOAuthCommandTarget(
                AevatarOAuthClientGAgent.WellKnownId,
                "channel-identity.oauth-rebuild"));
        services.AddIdentityOAuthCommandDispatch<RebuildAevatarOAuthClientProjectionCommand, AevatarOAuthClientGAgent>(
            static _ => new ChannelIdentityOAuthCommandTarget(
                AevatarOAuthClientGAgent.WellKnownId,
                "channel-identity.oauth-projection-rebuild"));
        services.AddIdentityOAuthCommandDispatch<RotateAevatarOAuthClientHmacKeyCommand, AevatarOAuthClientGAgent>(
            static _ => new ChannelIdentityOAuthCommandTarget(
                AevatarOAuthClientGAgent.WellKnownId,
                "channel-identity.oauth-hmac-rotate"));

        // ─── Broker ───
        // Register broker as a *singleton* and inject IHttpClientFactory so
        // each call resolves a fresh HttpClient backed by the factory's
        // rotating handler pool. The earlier shape — AddHttpClient<T>()
        // (transient) re-exposed via TryAddSingleton<I> — pinned the first
        // resolved HttpClient + HttpMessageHandler inside the singleton and
        // silently defeated the 2-min handler rotation, so long-running
        // silos would never pick up DNS / TLS-cert changes.
        services.AddOptions<NyxIdBrokerOptions>().ValidateOnStart();
        if (configuration is not null)
        {
            services.Configure<NyxIdBrokerOptions>(options =>
            {
                options.ResourceServerBaseUrl =
                    (configuration[NyxIdBrokerOptions.ResourceServerBaseUrlConfigurationKey] ?? string.Empty)
                    .Trim()
                    .TrimEnd('/');
            });
        }
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<NyxIdBrokerOptions>, NyxIdBrokerOptionsValidator>());
        services.TryAddSingleton<StateTokenCodec>();
        services.AddHttpClient(NyxIdRemoteCapabilityBroker.HttpClientName);
        services.TryAddSingleton<NyxIdRemoteCapabilityBroker>();
        services.TryAddSingleton<INyxIdCapabilityBroker>(sp => sp.GetRequiredService<NyxIdRemoteCapabilityBroker>());
        services.TryAddSingleton<INyxIdConnectedServiceInventoryCapabilityIssuer>(sp =>
            sp.GetRequiredService<NyxIdRemoteCapabilityBroker>());
        services.TryAddSingleton<INyxIdSkillCapabilityIssuer>(sp =>
            sp.GetRequiredService<NyxIdRemoteCapabilityBroker>());
        services.TryAddSingleton<INyxIdBrokerCallbackClient>(sp => sp.GetRequiredService<NyxIdRemoteCapabilityBroker>());
        services.TryAddSingleton<INyxIdBindingRetirementPort>(sp => sp.GetRequiredService<NyxIdRemoteCapabilityBroker>());

        // ─── Binding revocation reconciler (observed-invalid_grant self-heal) ───
        // Event-sources a local revoke when a turn observes BindingRevokedException
        // (NyxID invalid_grant). Lives here so the RevokeBindingCommand + dispatch
        // stay in the identity layer; NyxidChat depends only on the abstraction.
        services.TryAddSingleton<IBindingRevocationReconciler, BindingRevocationReconciler>();

        // ─── OAuth client bootstrap ───
        // The configured browser PKCE client id is the deployment authority.
        // The actor materializes that desired configuration into cluster state
        // so every broker consumer observes one coherent client snapshot.
        services.AddHttpClient<NyxIdDynamicClientRegistrationClient>();
        var oauthClientOptions = services.AddOptions<AevatarOAuthClientOptions>();
        var bootstrapOptions = services.AddOptions<AevatarOAuthClientBootstrapOptions>();
        if (configuration is not null)
        {
            bootstrapOptions.Bind(configuration.GetSection(AevatarOAuthClientBootstrapOptions.SectionName));
            oauthClientOptions.Configure(options =>
                options.ClientId = BackendConsoleOidcClientIdResolver.Resolve(configuration));
        }
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

    private static IServiceCollection AddIdentityOAuthCommandDispatch<TCommand, TAgent>(
        this IServiceCollection services,
        Func<TCommand, ChannelIdentityOAuthCommandTarget> resolveTarget)
        where TCommand : class, IMessage
        where TAgent : IAgent
    {
        // Refactor (iter27/cluster-028-identity-oauth-endpoint):
        //   Old pattern: IdentityOAuthEndpoints + AevatarOAuthClientBootstrapService 直接构造 EventEnvelope 投递,然后在 endpoint 内同步等 projection readiness / rebuild observation / readmodel polling (3-15s timeout + 50-250ms polling),违反 ACK 协议 + query-time projection priming
        //   New principle: 加 module-local CQRS dispatch adapters(ChannelIdentityOAuthCommandDispatch);endpoint inject typed ICommandDispatchService<...>,返回 accepted/pending + status URL,不再等 projection;删 IProjectionReadinessPort/ExternalIdentityBindingProjectionPort/AevatarOAuthClientProjectionPort/AevatarOAuthClientRebuildCoordinator/ProjectionWaitTimeout 等
        services.TryAddSingleton(new ChannelIdentityOAuthCommandRoute<TCommand>(resolveTarget));
        services.TryAddSingleton<
            ICommandDispatchService<TCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>,
            ChannelIdentityOAuthCommandDispatch<TCommand, TAgent>>();
        return services;
    }
}
