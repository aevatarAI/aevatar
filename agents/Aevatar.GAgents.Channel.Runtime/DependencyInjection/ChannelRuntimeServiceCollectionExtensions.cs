using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Maintenance;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime.Audit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// DI registration entry point for the channel runtime package.
/// </summary>
public static class ChannelRuntimeServiceCollectionExtensions
{
    // Refactor (iter36/cluster-042-channel-diagnostics-readmodel):
    //   Old pattern: Channel runtime diagnostics 用 singleton in-memory list with retention trimming;diagnostics endpoint 读 process-local list 直接(InMemoryChannelRuntimeDiagnostics 注册为 singleton + ImmutableList 字段 + ImmutableInterlocked mutation)。
    //   New principle: Channel diagnostics 改为 logs/metrics only(observability path)OR actor/projection-backed diagnostic events with readmodel query。**禁止** public endpoint 读 singleton process memory 作 diagnostic fact source。
    /// <summary>
    /// Backwards-compat overload — registers the channel runtime middlewares,
    /// default turn-runner fallback, ChannelBotRegistration projection
    /// pipeline, and pipeline composition without an <see cref="IConfiguration"/>.
    /// </summary>
    public static IServiceCollection AddChannelRuntime(this IServiceCollection services)
        => AddChannelRuntime(services, configuration: null);

    /// <summary>
    /// Registers the channel runtime middlewares, default turn-runner
    /// fallback, ChannelBotRegistration projection pipeline, and pipeline composition.
    /// </summary>
    public static IServiceCollection AddChannelRuntime(
        this IServiceCollection services, IConfiguration? configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        // ─── Retired-actor cleanup contribution ───
        services.AddAevatarAgentKindRegistry(builder => builder.ScanAssemblies(typeof(ChannelBotRegistrationGAgent).Assembly));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRetiredActorSpec, ChannelRuntimeRetiredActorSpec>());

        // ─── Core middlewares + default turn runner ───
        services.TryAddSingleton<ConversationResolverMiddleware>();
        services.TryAddSingleton<LoggingMiddleware>();
        services.TryAddSingleton<TracingMiddleware>();
        services.TryAddSingleton<IConversationTurnRunner, NullConversationTurnRunner>();
        services.TryAddSingleton<IConversationCardTurnRunner, NullConversationCardTurnRunner>();
        services.TryAddSingleton<INyxRelayTextReplyStreamRenderer, NyxRelayTextReplyStreamRenderer>();
        services.TryAddSingleton<ILarkCardReplyStreamRenderer, LarkCardReplyStreamRenderer>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IReplyOperationStepRenderer, NyxRelayTextReplyStreamRenderer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IReplyOperationStepRenderer, LarkCardReplyStreamRenderer>());

        // ─── Tombstone compaction options + materialized watermark ───
        services.AddOptions<ChannelRuntimeTombstoneCompactionOptions>();
        // Refactor (iter17/cluster-034):
        //   Old pattern: Replay-based projection scope watermark query via IEventStore (EventStoreProjectionScopeWatermarkQueryPort).
        //   New principle: Materialized ProjectionScopeStatusDocument readmodel; ProjectionScopeStatusQueryPort reads document only; never replays IEventStore.
        services.AddProjectionScopeStatusRuntimeCore();
        if (configuration != null)
        {
            services.Configure<ChannelRuntimeTombstoneCompactionOptions>(
                configuration.GetSection("ChannelRuntime:TombstoneCompaction"));
        }

        // ─── Projection pipeline shared infrastructure ───
        services.AddProjectionReadModelRuntime();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();
        services.TryAddSingleton<ProjectionActivationPlanDispatcher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            ICommittedStatePublicationHook,
            CommittedStateProjectionActivationHook>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionActivationPlanProvider,
            ChannelBotRegistrationCommittedStateProjectionActivationPlanProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionActivationPlanProvider,
            ConversationDeliveryCommittedStateProjectionActivationPlanProvider>());

        // ─── Workflow result delivery repair committed-outcome observation ───
        services.AddEventSinkProjectionRuntimeCore<
            ChannelWorkflowResultDeliveryRepairProjectionContext,
            ChannelWorkflowResultDeliveryRepairRuntimeLease,
            ChannelBotWorkflowResultDeliveryRepairOutcome,
            ProjectionSessionScopeGAgent<ChannelWorkflowResultDeliveryRepairProjectionContext>>(
            static scopeKey => new ChannelWorkflowResultDeliveryRepairProjectionContext
            {
                SessionId = scopeKey.SessionId,
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new ChannelWorkflowResultDeliveryRepairRuntimeLease(context));
        services.TryAddSingleton<
            IProjectionSessionEventCodec<ChannelBotWorkflowResultDeliveryRepairOutcome>,
            ChannelWorkflowResultDeliveryRepairOutcomeCodec>();
        services.TryAddSingleton<
            IProjectionSessionEventHub<ChannelBotWorkflowResultDeliveryRepairOutcome>,
            ProjectionSessionEventHub<ChannelBotWorkflowResultDeliveryRepairOutcome>>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ChannelWorkflowResultDeliveryRepairProjectionContext>,
            ChannelWorkflowResultDeliveryRepairOutcomeProjector>());
        services.TryAddSingleton<
            IChannelWorkflowResultDeliveryRepairObservationPort,
            ChannelWorkflowResultDeliveryRepairObservationPort>();

        // ─── Channel Bot Registration projection pipeline ───
        services.AddProjectionMaterializationRuntimeCore<
            ChannelBotRegistrationMaterializationContext,
            ChannelBotRegistrationMaterializationRuntimeLease,
            ProjectionMaterializationScopeGAgent<ChannelBotRegistrationMaterializationContext>>(
            static scopeKey => new ChannelBotRegistrationMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new ChannelBotRegistrationMaterializationRuntimeLease(context));
        services.AddCurrentStateProjectionMaterializer<
            ChannelBotRegistrationMaterializationContext,
            ChannelBotRegistrationProjector>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, ChannelBotRegisteredAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, ChannelBotUnregisteredAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, ChannelBotRegistrationRejectedAuditTranslator>());
        services.AddAuditCommittedFactMaterializer<ChannelBotRegistrationMaterializationContext>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ChannelBotRegistrationDocument>,
            ChannelBotRegistrationDocumentMetadataProvider>();
        services.TryAddSingleton<IChannelBotRegistrationQueryPort, ChannelBotRegistrationQueryPort>();
        services.TryAddSingleton<IChannelBotRegistrationQueryByNyxIdentityPort, ChannelBotRegistrationQueryPort>();
        services.TryAddSingleton<IChannelBotRegistrationRuntimeQueryPort, ChannelBotRegistrationRuntimeQueryPort>();
        services.TryAddSingleton<ChannelBotRegistrationProjectionBootstrapActivator>();
        services.AddHostedService<ChannelBotRegistrationStartupService>();

        // ─── Conversation Delivery projection pipeline ───
        services.AddProjectionMaterializationRuntimeCore<
            ConversationDeliveryMaterializationContext,
            ConversationDeliveryMaterializationRuntimeLease,
            ProjectionMaterializationScopeGAgent<ConversationDeliveryMaterializationContext>>(
            static scopeKey => new ConversationDeliveryMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new ConversationDeliveryMaterializationRuntimeLease(context));
        services.AddCurrentStateProjectionMaterializer<
            ConversationDeliveryMaterializationContext,
            ConversationDeliveryCurrentStateProjector>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ConversationDeliveryCurrentStateDocument>,
            ConversationDeliveryDocumentMetadataProvider>();
        services.TryAddSingleton<IConversationDeliveryQueryPort, ConversationDeliveryQueryPort>();

        // ─── Channel pipeline composition ───
        services.TryAddSingleton<ConversationDispatchMiddleware>();
        services.Replace(ServiceDescriptor.Singleton(_ => new MiddlewarePipelineBuilder()
            .Use<TracingMiddleware>()
            .Use<LoggingMiddleware>()
            .Use<ConversationResolverMiddleware>()
            .Use<ConversationDispatchMiddleware>()));
        services.TryAddSingleton<ChannelPipeline>(sp => sp.GetRequiredService<MiddlewarePipelineBuilder>().Build(sp));

        // ─── Tombstone compaction service ───
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ITombstoneCompactionTarget, ChannelBotRegistrationTombstoneCompactionTarget>());
        services.TryAddSingleton<ChannelRuntimeTombstoneCompactor>();
        services.AddHostedService<ChannelRuntimeTombstoneCompactionService>();

        return services;
    }

}
