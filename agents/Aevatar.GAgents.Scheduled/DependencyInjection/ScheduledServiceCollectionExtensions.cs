using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.GAgents.Scheduled.Audit;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Maintenance;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Studio.Application.Provisioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// DI registration entry point for the scheduled-agent / user-agent-catalog package.
/// </summary>
public static class ScheduledServiceCollectionExtensions
{
    /// <summary>
    /// Registers the User Agent Catalog projection pipeline (materialization runtime,
    /// catalog + Nyx credential projectors, query ports, document metadata, startup
    /// service).
    /// </summary>
    public static IServiceCollection AddScheduledAgents(
        this IServiceCollection services, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // ─── Retired-actor cleanup contribution ───
        services.AddAevatarAgentKindRegistry(builder => builder.ScanAssemblies(typeof(UserAgentCatalogGAgent).Assembly));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRetiredActorSpec, ScheduledRetiredActorSpec>());
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IStudioScheduledCredentialMaterializer, StudioScheduledCredentialMaterializer>();
        services.TryAddSingleton<ProjectionActivationPlanDispatcher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            ICommittedStatePublicationHook,
            CommittedStateProjectionActivationHook>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionActivationPlanProvider,
            UserAgentCatalogCommittedStateProjectionActivationPlanProvider>());

        // ─── User Agent Catalog projection pipeline ───
        services.AddProjectionMaterializationRuntimeCore<
            UserAgentCatalogMaterializationContext,
            UserAgentCatalogMaterializationRuntimeLease,
            ProjectionMaterializationScopeGAgent<UserAgentCatalogMaterializationContext>>(
            static scopeKey => new UserAgentCatalogMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new UserAgentCatalogMaterializationRuntimeLease(context));
        services.AddCurrentStateProjectionMaterializer<
            UserAgentCatalogMaterializationContext,
            UserAgentCatalogProjector>();
        services.AddCurrentStateProjectionMaterializer<
            UserAgentCatalogMaterializationContext,
            SkillRunnerExecutionProjector>();
        services.AddCurrentStateProjectionMaterializer<
            UserAgentCatalogMaterializationContext,
            UserAgentCatalogNyxCredentialProjector>();
        services.AddCurrentStateProjectionMaterializer<
            UserAgentCatalogMaterializationContext,
            UserAgentApiKeyRevocationProjector>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<UserAgentCatalogDocument>,
            UserAgentCatalogDocumentMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<SkillRunnerExecutionDocument>,
            SkillRunnerExecutionDocumentMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<UserAgentCatalogNyxCredentialDocument>,
            UserAgentCatalogNyxCredentialDocumentMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<UserAgentApiKeyRevocationDocument>,
            UserAgentApiKeyRevocationDocumentMetadataProvider>();
        services.TryAddSingleton<IUserAgentCatalogQueryPort, UserAgentCatalogQueryPort>();
        services.TryAddSingleton<ISkillRunnerExecutionQueryPort, SkillRunnerExecutionQueryPort>();
        // Internal-only credential-bearing reader for outbound delivery (issue #466 §D).
        // Architecture rule: NEVER inject IUserAgentDeliveryTargetReader into an
        // IAgentTool implementation; LLM tools see only the caller-scoped public port
        // (which excludes NyxApiKey by DTO shape).
        services.TryAddSingleton<IUserAgentDeliveryTargetReader, UserAgentDeliveryTargetReader>();
        services.TryAddSingleton<ISkillRunnerOutboundDeliveryPort, ChannelNativeSkillRunnerOutboundDeliveryPort>();
        services.TryAddSingleton<UserAgentCatalogProjectionBootstrapActivator>();
        services.TryAddSingleton<IUserAgentCatalogCommandPort, UserAgentCatalogCommandPort>();
        services.AddEventSinkProjectionRuntimeCore<
            UserAgentCatalogCredentialRepairProjectionContext,
            UserAgentCatalogCredentialRepairRuntimeLease,
            UserAgentCatalogCredentialRepairOutcome,
            ProjectionSessionScopeGAgent<UserAgentCatalogCredentialRepairProjectionContext>>(
            static scopeKey => new UserAgentCatalogCredentialRepairProjectionContext
            {
                SessionId = scopeKey.SessionId,
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new UserAgentCatalogCredentialRepairRuntimeLease(context));
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();
        services.TryAddSingleton<
            IProjectionSessionEventCodec<UserAgentCatalogCredentialRepairOutcome>,
            UserAgentCatalogCredentialRepairOutcomeCodec>();
        services.TryAddSingleton<
            IProjectionSessionEventHub<UserAgentCatalogCredentialRepairOutcome>,
            ProjectionSessionEventHub<UserAgentCatalogCredentialRepairOutcome>>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<UserAgentCatalogCredentialRepairProjectionContext>,
            UserAgentCatalogCredentialRepairOutcomeProjector>());
        services.TryAddSingleton<
            IUserAgentCatalogCredentialRepairObservationPort,
            UserAgentCatalogCredentialRepairObservationPort>();
        services.TryAddSingleton<IUserAgentCatalogCredentialRepairPort, UserAgentCatalogCredentialRepairPort>();
        services.TryAddSingleton<IScheduledWorkflowAgentCreationPort, ScheduledWorkflowAgentCreationPort>();
        services.TryAddSingleton<ISkillRunnerCronSchedulePort, SkillRunnerCronSchedulePort>();
        services.TryAddSingleton<ISkillRunnerCommandPort, SkillRunnerCommandPort>();
        // Caller-scope resolver chain (issue #466 §B). Channel resolver runs first so
        // a request with channel metadata produces the per-sender scope rather than
        // the looser nyxid-scoped tuple from the underlying NyxID session.
        services.TryAddSingleton<INyxIdCurrentUserResolver, NyxIdCurrentUserResolver>();
        services.TryAddSingleton<ChannelMetadataCallerScopeResolver>();
        services.TryAddSingleton<NyxIdNativeCallerScopeResolver>();
        services.TryAddSingleton<ICallerScopeResolver>(sp => new CompositeCallerScopeResolver(
            new ICallerScopeResolver[]
            {
                sp.GetRequiredService<ChannelMetadataCallerScopeResolver>(),
                sp.GetRequiredService<NyxIdNativeCallerScopeResolver>(),
            },
            sp.GetService<Microsoft.Extensions.Logging.ILogger<CompositeCallerScopeResolver>>()));
        services.AddHostedService<UserAgentApiKeyRevocationReadModelKeyMigrationService>();
        services.AddHostedService<UserAgentCatalogStartupService>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ITombstoneCompactionTarget, UserAgentCatalogTombstoneCompactionTarget>());

        // ─── Committed-fact audit translators (lifecycle / authorization) ───
        // Both the user-agent catalog and skill-runner committed state events route
        // through UserAgentCatalogMaterializationContext, so a single audit
        // materializer for that context covers both event families.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator, UserAgentCatalogUpsertedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator, UserAgentCatalogTombstonedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator, UserAgentCatalogSharedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator, UserAgentCatalogUnsharedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator, SkillRunnerInitializedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator, SkillRunnerEnabledAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator, SkillRunnerDisabledAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator, SkillRunnerOneShotRetiredAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator, SkillRunnerExternalTriggerAdmittedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator, SkillRunnerExternalTriggerRejectedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator, SkillRunnerExecutionCompletedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator, SkillRunnerExecutionFailedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuditCommittedEventTranslator, SkillRunnerExecutionRejectedAuditTranslator>());
        services.AddAuditCommittedFactMaterializer<UserAgentCatalogMaterializationContext>();

        return services;
    }

}
