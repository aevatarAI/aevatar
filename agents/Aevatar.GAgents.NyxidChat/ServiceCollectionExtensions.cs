using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.DependencyInjection;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using Aevatar.CQRS.Core.DependencyInjection;
using Aevatar.CQRS.Core.Interactions;
using Aevatar.CQRS.Core.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.AI.ToolProviders.Lark;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Abstractions.Slash;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using Aevatar.GAgents.NyxidChat.LlmSelection;
using Aevatar.GAgents.NyxidChat.Slash;
using Aevatar.GAgents.NyxidChat.Voice;
using Aevatar.GAgents.NyxidChat.WorkflowDraftRun;
using Aevatar.GAgents.NyxidChat.WorkflowRunDelivery;
using Aevatar.AGUI.Contracts;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNyxIdChat(this IServiceCollection services, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        RuntimeHelpers.RunClassConstructor(typeof(NyxIdChatConversationGAgent).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(NyxIdChatTurnGAgent).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(NyxIdChatGAgent).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(NyxIdVoiceGAgent).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(AgentRunGAgent).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(ChannelWorkflowDraftRunGAgent).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(WorkflowRunDeliveryGAgent).TypeHandle);
        services.AddAevatarAgentKindRegistry(builder => builder.ScanAssemblies(typeof(NyxIdChatGAgent).Assembly));

        services.AddCqrsCore();
        services.AddAgentToolExecution();
        services.AddToolSetRegistry();
        if (configuration is null)
            services.AddNyxIdApiAccess();
        else
            services.AddNyxIdApiAccess(configuration);
        var assistantActionsOptions = BindAssistantActionsOptions(configuration);
        services.TryAddSingleton(assistantActionsOptions);
        services.TryAddSingleton(BindCanaryEffectFaultOptions(configuration));
        services.TryAddSingleton<INyxIdChatCanaryEffectFaultAuthorizationPolicy,
            NyxIdChatCanaryEffectFaultAuthorizationPolicy>();
        if (assistantActionsOptions.Enabled)
        {
            services.TryAddSingleton<NyxIdAssistantActionRegistrySnapshot>();
            services.TryAddSingleton<INyxIdAssistantActionRegistrySource,
                NyxIdAssistantActionRegistryHttpSource>();
            // Transient on purpose: a failed startup pins a disabled fallback
            // that background recovery may upgrade, so consumers must observe
            // the snapshot's current registry instead of a captured instance.
            services.TryAddTransient<NyxIdAssistantActionRegistry>(sp =>
                sp.GetRequiredService<NyxIdAssistantActionRegistrySnapshot>().GetRequired());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService,
                NyxIdAssistantActionRegistryStartupService>());
        }
        else
        {
            services.TryAddSingleton(NyxIdAssistantActionRegistry.CreateDisabled());
        }
        services.TryAddSingleton(provider => BindRelayOptions(configuration));
        services.TryAddSingleton<Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions>(
            provider => provider.GetRequiredService<NyxIdRelayOptions>());
        services.TryAddSingleton<NyxIdRelayTransport>();
        services.TryAddSingleton<NyxIdRelayAuthValidator>();
        services.TryAddSingleton<INyxIdRelayIngressPort, NyxIdRelayIngressPort>();
        services.TryAddSingleton<INyxIdChatAgentProfileResolver, DisabledNyxIdChatAgentProfileResolver>();
        services.TryAddSingleton<SkillFrontmatterParser>();
        services.TryAddSingleton<StreamingAgentProfileTurnClassifier>();
        services.TryAddSingleton<IAgentProfileTurnClassifier>(sp =>
            sp.GetRequiredService<StreamingAgentProfileTurnClassifier>());
        services.TryAddSingleton<StreamingAgentProfileConnectedOperationSelector>();
        services.TryAddSingleton<IAgentProfileConnectedOperationSelector>(sp =>
            sp.GetRequiredService<StreamingAgentProfileConnectedOperationSelector>());
        services.TryAddSingleton<INyxIdChatTurnIntentClassifier, NyxIdChatTurnIntentClassifier>();
        services.TryAddSingleton(sp => new AgentTurnToolCatalogMaterializer(
            sp.GetRequiredService<IToolSetRegistry>(),
            sp.GetRequiredService<IAgentProfileTurnClassifier>(),
            sp.GetService<IExactRemoteSkillFetcher>(),
            sp.GetRequiredService<SkillFrontmatterParser>(),
            sp.GetService<ILogger<AgentTurnToolCatalogMaterializer>>(),
            toolDiscoveryService: sp.GetRequiredService<IAgentToolDiscoveryService>(),
            connectedOperationSelector:
                sp.GetRequiredService<IAgentProfileConnectedOperationSelector>()));
        services.TryAddSingleton<IAgentProfileTurnToolCatalogPlanner>(sp =>
            sp.GetRequiredService<AgentTurnToolCatalogMaterializer>());
        services.TryAddSingleton<IChannelRelayTailTextSender, MissingChannelRelayTailTextSender>();
        services.TryAddSingleton<IChannelRelayProxyResponseClassifier, MissingChannelRelayProxyResponseClassifier>();
        services.TryAddSingleton<NyxIdChatLifecycleFacade>();
        services.TryAddSingleton<INyxIdChatControlCommandPort, NyxIdChatControlCommandPort>();
        services.TryAddSingleton<INyxIdVoiceAgentCommandService, NyxIdVoiceAgentCommandService>();
        AddNyxIdLifecycleCommands(services);

        // ─── Channel LLM reply run dispatch ───
        services.TryAddSingleton<IChannelLlmReplyRunDispatcher, AgentRunDispatcher>();
        services.TryAddSingleton<IAgentRunToolApprovalDecisionDispatcher, AgentRunToolApprovalDecisionDispatcher>();
        services.TryAddSingleton<IChannelWorkflowAuthorizedScopeResolver>(sp =>
            new ChannelWorkflowAuthorizedScopeResolver(
                sp.GetService<Aevatar.GAgents.Channel.Identity.Abstractions.IOwnerScopeResolver>(),
                sp.GetService<ILogger<ChannelWorkflowAuthorizedScopeResolver>>()));
        // The two-generic overload keeps the concrete implementation type visible to
        // TryAddEnumerable; a Func<IServiceProvider, IChannelSlashCommandHandler> factory
        // would be indistinguishable from every other handler registered for this service.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IChannelSlashCommandHandler, ChannelWorkflowDraftRunSlashCommandHandler>(sp =>
                new ChannelWorkflowDraftRunSlashCommandHandler(
                    sp.GetService<Aevatar.GAgentService.Abstractions.Ports.IScopeWorkflowQueryPort>(),
                    sp.GetRequiredService<IChannelWorkflowAuthorizedScopeResolver>())));
        services.TryAddSingleton<ChannelSlashCommandRegistry>();
        services.TryAddSingleton<ChannelWorkflowDraftRunIntentParser>();
        services.TryAddSingleton<ChannelWorkflowDraftRunAdmission>(sp =>
            new ChannelWorkflowDraftRunAdmission(
                sp.GetRequiredService<ChannelWorkflowDraftRunIntentParser>(),
                sp.GetService<Aevatar.GAgentService.Abstractions.Ports.IScopeWorkflowQueryPort>(),
                sp.GetRequiredService<IChannelWorkflowAuthorizedScopeResolver>()));
        services.TryAddSingleton<WorkflowDraftRunReplyRenderer>();
        services.TryAddSingleton<IChannelWorkflowDraftRunInteractionPort>(sp =>
            new ChannelWorkflowDraftRunInteractionPort(
                sp.GetRequiredService<Aevatar.Foundation.Abstractions.IActorRuntime>(),
                sp.GetRequiredService<Aevatar.Foundation.Abstractions.IActorDispatchPort>(),
                sp.GetRequiredService<ILogger<ChannelWorkflowDraftRunInteractionPort>>(),
                sp.GetService<Aevatar.Workflow.Application.Abstractions.Runs.IWorkflowChatRunInteractionPort>(),
                sp.GetService<TimeProvider>(),
                sp.GetService<ILarkNyxClient>(),
                sp.GetService<Aevatar.Workflow.Application.Abstractions.Runs.IFileArtifactIngressPort>(),
                sp.GetService<ILarkOutboundClientFactory>()));
        // ─── Conversation turn-runner override + reply generator ───
        // Lets the turn runner resolve the bot's own Lark open_id on demand so its group-chat
        // admission gate can tell whether an inbound @-mention addressed the bot.
        services.TryAddSingleton<ILarkBotIdentityResolver, LarkBotIdentityResolver>();
        services.Replace(ServiceDescriptor.Singleton<IConversationTurnRunner, ChannelConversationTurnRunner>());
        // The CardKit runner depends on Aevatar.AI.ToolProviders.Lark services. AddNyxIdChat()
        // does not transitively register them — production hosts also call AddLarkTools() —
        // so resolve via factory and gracefully fall back to the no-op runner when Lark
        // tooling is absent. This keeps CardKit dormant for hosts that opt out of Lark
        // instead of failing DI validation at startup.
        var existingCardRunner = services.LastOrDefault(static descriptor =>
            descriptor.ServiceType == typeof(IConversationCardTurnRunner));
        if (existingCardRunner is null ||
            existingCardRunner.ImplementationType == typeof(NullConversationCardTurnRunner))
        {
            services.Replace(ServiceDescriptor.Singleton<IConversationCardTurnRunner>(sp =>
            {
                var cardKit = sp.GetService<ILarkCardKitClient>();
                var lark = sp.GetService<ILarkNyxClient>();
                if (cardKit is null || lark is null)
                    return new NullConversationCardTurnRunner();
                return new ChannelCardConversationTurnRunner(
                    cardKit,
                    lark,
                    // Routes each card reply through the inbound bot's own NyxID proxy slug (carried on
                    // the reply activity's TransportExtras) instead of the process-wide default, so a DM
                    // to one bot is answered by that bot's app and not a sibling under the same account.
                    sp.GetService<ILarkOutboundClientFactory>(),
                    // Inbound turns bind the card through their single-use channel reply authority.
                    // Proactive turns still use the scoped Lark proxy client above.
                    sp.GetRequiredService<NyxIdApiClient>(),
                    sp.GetRequiredService<ILogger<ChannelCardConversationTurnRunner>>());
            }));
        }
        services.TryAddSingleton<BuiltInPromptFloorProvider>();
        services.TryAddSingleton<IBuiltInPromptFloorProvider>(sp => sp.GetRequiredService<BuiltInPromptFloorProvider>());
        services.TryAddSingleton<IUserMemoryPromptContextProvider>(sp =>
            new UserMemoryPromptContextProvider(
                sp.GetService<IUserMemoryQueryPort>(),
                sp.GetRequiredService<ILogger<UserMemoryPromptContextProvider>>()));
        services.TryAddSingleton<ChannelRemoteSkillAccessTokenResolver>(sp =>
            new ChannelRemoteSkillAccessTokenResolver(
                sp.GetService<INyxIdSkillCapabilityIssuer>(),
                sp.GetService<ILogger<ChannelRemoteSkillAccessTokenResolver>>()));
        services.TryAddSingleton<IRemoteSkillAccessTokenResolver>(sp =>
            sp.GetRequiredService<ChannelRemoteSkillAccessTokenResolver>());
        services.TryAddSingleton<IConversationReplyGenerator>(sp =>
            new NyxIdConversationReplyGenerator(
                sp.GetRequiredService<ILLMProviderFactory>(),
                sp.GetRequiredService<IBuiltInPromptFloorProvider>(),
                ResolveChannelToolSources(sp),
                sp.GetServices<IAgentRunMiddleware>(),
                sp.GetServices<ILLMCallMiddleware>(),
                sp.GetService<LocalSkillCatalog>(),
                sp.GetService<IRemoteSkillFetcher>(),
                sp.GetService<NyxIdRelayOptions>(),
                sp.GetService<INyxIdUserLlmPreferencesStore>(),
                sp.GetService<IUserMemoryPromptContextProvider>(),
                larkClient: sp.GetService<ILarkNyxClient>(),
                fileIngressPort: sp.GetService<Aevatar.Workflow.Application.Abstractions.Runs.IFileArtifactIngressPort>(),
                fileArtifactReadPort: sp.GetService<Aevatar.Workflow.Application.Abstractions.Runs.IFileArtifactReadPort>(),
                logger: sp.GetService<ILogger<NyxIdConversationReplyGenerator>>(),
                overlayProvider: sp.GetService<ISystemSkillOverlayProvider>(),
                larkOutboundClientFactory: sp.GetService<ILarkOutboundClientFactory>(),
                toolExecutionPort: sp.GetRequiredService<IAgentToolExecutionPort>(),
                remoteSkillAccessTokenResolver: sp.GetService<IRemoteSkillAccessTokenResolver>(),
                nyxIdChatToolSources: ResolveNyxIdChatToolSources(sp),
                contentArtifactQueryPort: sp.GetService<IContentArtifactQueryPort>()));
        services.TryAddSingleton<ChannelNyxIdConnectedServiceInventoryToolSource>();
        services.TryAddSingleton<IAgentRunReplyGenerationExecutorPort, AgentRunReplyGenerationExecutor>();
        services.TryAddSingleton<INyxIdActionPostconditionPort>(sp =>
            new NyxIdActionPostconditionPort(
                sp.GetService<INyxIdAuthorizationCatalogQueryPort>(),
                sp.GetService<INyxIdActionEvidenceReadPort>(),
                sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<INyxIdChatDelegationCredentialLifecyclePort,
            NyxIdChatDelegationCredentialLifecyclePort>();
        services.TryAddSingleton<INyxIdChatTurnOperationExecutor, NyxIdChatTurnOperationExecutor>();
        services.TryAddSingleton<INyxIdChatToolVerificationPort, NyxIdChatToolVerificationPort>();
        services.TryAddSingleton<INyxIdChatTurnOperationReconciliationPort,
            AdmittedNyxIdChatTurnOperationReconciliationPort>();
        services.TryAddSingleton<INyxIdChatTurnOperationDispatchPort,
            NyxIdChatTurnOperationDispatchPort>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAgentToolReceiptRenderer, AgentToolReceiptRenderer>();
        services.TryAddSingleton<ILarkCardReplyStreamRenderer, LarkCardReplyStreamRenderer>();
        services.TryAddSingleton<IWorkflowRunBackgroundDeliveryRegistrationPort, WorkflowRunBackgroundDeliveryRegistrationPort>();
        services.TryAddSingleton<IWorkflowResultDeliveryCredentialResolver, SecretVaultWorkflowResultDeliveryCredentialResolver>();
        // ─── LLM-call middleware that injects channel context into LLM requests ───
        // Lives here (not in Channel.Runtime) because it implements ILLMCallMiddleware
        // (AI.Abstractions); keeping it in NyxidChat lets Channel.Runtime stay free of
        // AI / Workflow dependencies. ChannelCardActionRouting (workflow resume binding)
        // is in this package for the same reason.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILLMCallMiddleware, ChannelContextMiddleware>());

        // ─── /model slash command (issue #513 phase 5) ───
        // Registered here (not in Channel.Identity) because the handler depends
        // on channel-scoped Studio application abstractions; Channel.Identity
        // intentionally does not pull those contracts.
        // Catalog client uses IMemoryCache for the proxy-services TTL cache. AddMemoryCache
        // is idempotent: hosts that already registered MemoryCacheOptions keep control of
        // cache size/compaction behavior; hosts that did not register one get the default.
        services.AddMemoryCache();
        services.TryAddSingleton<INyxIdLlmServiceCatalogClient, NyxIdLlmServiceCatalogClient>();
        // These are consumed by singleton turn-runner/slash handlers. They create
        // short scopes internally for the channel preference port instead of
        // capturing potentially scoped implementations at construction time.
        services.TryAddSingleton<IUserLlmOptionsService, DefaultUserLlmOptionsService>();
        services.TryAddSingleton<IUserLlmSelectionService, DefaultUserLlmSelectionService>();
        services.TryAddSingleton<IUserLlmOptionsRenderer<MessageContent>, TextUserLlmOptionsRenderer>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChannelSlashCommandHandler, ModelChannelSlashCommandHandler>());

        services.AddEventSinkProjectionRuntimeCore<
            NyxIdChatSessionProjectionContext,
            NyxIdChatSessionRuntimeLease,
            AGUIEvent,
            ProjectionSessionScopeGAgent<NyxIdChatSessionProjectionContext>>(
            static scopeKey => new NyxIdChatSessionProjectionContext
            {
                SessionId = scopeKey.SessionId,
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new NyxIdChatSessionRuntimeLease(context));
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();
        services.TryAddSingleton<IProjectionSessionEventCodec<AGUIEvent>, NyxIdChatSessionEventCodec>();
        services.TryAddSingleton<IProjectionSessionEventHub<AGUIEvent>, ProjectionSessionEventHub<AGUIEvent>>();
        services.TryAddSingleton<INyxIdChatSessionProjectionPort, NyxIdChatSessionProjectionPort>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<NyxIdChatSessionProjectionContext>,
            NyxIdChatSessionEventProjector>());
        services.TryAddSingleton<ProjectionActivationPlanDispatcher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            ICommittedStatePublicationHook,
            CommittedStateProjectionActivationHook>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionActivationPlanProvider,
            NyxIdChatCommittedStateProjectionActivationPlanProvider>());
        AddNyxIdStreamingInteractions(services);

        return services;
    }

    private static IEnumerable<IAgentToolSource> ResolveChannelToolSources(IServiceProvider serviceProvider)
    {
        var workspace = serviceProvider.GetRequiredService<IToolSetRegistry>()
            .Resolve(ToolSetNames.WorkspaceDefault);
        var sources = workspace.IsSuccess
            ? workspace.Sources
            : serviceProvider.GetServices<IAgentToolSource>();
        foreach (var source in sources)
            yield return source;

        var inventory = serviceProvider.GetService<ChannelNyxIdConnectedServiceInventoryToolSource>();
        if (inventory is not null)
            yield return inventory;
    }

    private static IReadOnlyList<IAgentToolSource> ResolveNyxIdChatToolSources(
        IServiceProvider serviceProvider)
    {
        var result = serviceProvider
            .GetRequiredService<IToolSetRegistry>()
            .Resolve(ToolSetNames.NyxIdChatDefault);
        return result.IsSuccess ? result.Sources : [];
    }

    private static void AddNyxIdStreamingInteractions(IServiceCollection services)
    {
        // Refactor (iter21/cluster-002-request-path-projection-session-priming):
        //   Old pattern: request handlers synchronously ensure projection/session leases and wait on live sinks.
        //   New principle: commands use accepted receipts; observation is owned by binders or attach-only sessions.
        services.TryAddSingleton(NyxIdChatInteractionFactories.CreateChatResolver);
        services.TryAddSingleton(NyxIdChatInteractionFactories.CreateApprovalResolver);
        services.TryAddSingleton(NyxIdChatInteractionFactories.CreateActionContinuationResolver);
        services.TryAddSingleton(NyxIdChatInteractionFactories.CreateChatObservationLifecycle);
        services.TryAddSingleton(NyxIdChatInteractionFactories.CreateApprovalObservationLifecycle);
        services.TryAddSingleton(NyxIdChatInteractionFactories.CreateActionContinuationObservationLifecycle);
        services.TryAddSingleton<
            ICommandObservationScopeLeasePreparation<NyxIdChatCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError>>(
            sp => new NyxIdChatObservationScopeLeasePreparation<NyxIdChatCommand>(
                sp.GetRequiredService<IProjectionScopeActivationService<NyxIdChatSessionRuntimeLease>>(),
                sp.GetRequiredService<IProjectionScopeReleaseService<NyxIdChatSessionRuntimeLease>>(),
                static command => command.TurnId));
        services.TryAddSingleton<
            ICommandObservationScopeLeasePreparation<NyxIdApprovalCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError>>(
            sp => new NyxIdChatObservationScopeLeasePreparation<NyxIdApprovalCommand>(
                sp.GetRequiredService<IProjectionScopeActivationService<NyxIdChatSessionRuntimeLease>>(),
                sp.GetRequiredService<IProjectionScopeReleaseService<NyxIdChatSessionRuntimeLease>>(),
                static command => command.TurnId));
        services.TryAddSingleton<
            ICommandObservationScopeLeasePreparation<NyxIdActionContinuationCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError>>(
            sp => new NyxIdChatObservationScopeLeasePreparation<NyxIdActionContinuationCommand>(
                sp.GetRequiredService<IProjectionScopeActivationService<NyxIdChatSessionRuntimeLease>>(),
                sp.GetRequiredService<IProjectionScopeReleaseService<NyxIdChatSessionRuntimeLease>>(),
                static command => command.ContinuationTurnId));
        services.TryAddSingleton<ICommandEnvelopeFactory<NyxIdChatCommand>, NyxIdChatCommandEnvelopeFactory>();
        services.TryAddSingleton<ICommandEnvelopeFactory<NyxIdApprovalCommand>, NyxIdApprovalCommandEnvelopeFactory>();
        services.TryAddSingleton<ICommandEnvelopeFactory<NyxIdActionContinuationCommand>,
            NyxIdActionContinuationCommandEnvelopeFactory>();
        services.TryAddSingleton<ICommandTargetDispatcher<NyxIdChatCommandTarget>, ActorCommandTargetDispatcher<NyxIdChatCommandTarget>>();
        services.TryAddSingleton<ICommandReceiptFactory<NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt>, NyxIdChatAcceptedReceiptFactory>();
        services.TryAddSingleton<ICommandDispatchPipeline<NyxIdChatCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError>, DefaultCommandDispatchPipeline<NyxIdChatCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError>>();
        services.TryAddSingleton<ICommandDispatchPipeline<NyxIdApprovalCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError>, DefaultCommandDispatchPipeline<NyxIdApprovalCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError>>();
        services.TryAddSingleton<ICommandDispatchPipeline<NyxIdActionContinuationCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError>, DefaultCommandDispatchPipeline<NyxIdActionContinuationCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError>>();
        services.TryAddSingleton<ICommandCompletionPolicy<AGUIEvent, NyxIdChatCompletionStatus>, NyxIdChatCompletionPolicy>();
        services.TryAddSingleton<ICommandFinalizeEmitter<NyxIdChatAcceptedReceipt, NyxIdChatCompletionStatus, AGUIEvent>, NyxIdChatFinalizeEmitter>();
        services.TryAddSingleton<ICommandDurableCompletionResolver<NyxIdChatAcceptedReceipt, NyxIdChatCompletionStatus>, NyxIdChatDurableCompletionResolver>();
        services.TryAddSingleton<IEventFrameMapper<AGUIEvent, AGUIEvent>, IdentityEventFrameMapper<AGUIEvent>>();
        services.TryAddSingleton<IEventOutputStream<AGUIEvent, AGUIEvent>, DefaultEventOutputStream<AGUIEvent, AGUIEvent>>();
        services.TryAddSingleton<ICommandInteractionService<NyxIdChatCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>(sp =>
            new DefaultCommandInteractionService<NyxIdChatCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, AGUIEvent, NyxIdChatCompletionStatus>(
                sp.GetRequiredService<ICommandDispatchPipeline<NyxIdChatCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError>>(),
                sp.GetRequiredService<IEventOutputStream<AGUIEvent, AGUIEvent>>(),
                sp.GetRequiredService<ICommandCompletionPolicy<AGUIEvent, NyxIdChatCompletionStatus>>(),
                sp.GetRequiredService<ICommandFinalizeEmitter<NyxIdChatAcceptedReceipt, NyxIdChatCompletionStatus, AGUIEvent>>(),
                sp.GetRequiredService<ICommandDurableCompletionResolver<NyxIdChatAcceptedReceipt, NyxIdChatCompletionStatus>>(),
                sp.GetService<ILogger<DefaultCommandInteractionService<NyxIdChatCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, AGUIEvent, NyxIdChatCompletionStatus>>>(),
                sp.GetRequiredService<ICommandObservationLifecycle<NyxIdChatCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError>>(),
                sp.GetRequiredService<ICommandReceiptFactory<NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt>>(),
                sp.GetRequiredService<ICommandObservationScopeLeasePreparation<NyxIdChatCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError>>(),
                probeDurableCompletionWhileLive: true));
        services.TryAddSingleton<IRealtimeSession<NyxIdChatCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>(sp =>
            sp.GetRequiredService<ICommandInteractionService<NyxIdChatCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>());
        services.TryAddSingleton<ICommandInteractionService<NyxIdApprovalCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>(sp =>
            new DefaultCommandInteractionService<NyxIdApprovalCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, AGUIEvent, NyxIdChatCompletionStatus>(
                sp.GetRequiredService<ICommandDispatchPipeline<NyxIdApprovalCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError>>(),
                sp.GetRequiredService<IEventOutputStream<AGUIEvent, AGUIEvent>>(),
                sp.GetRequiredService<ICommandCompletionPolicy<AGUIEvent, NyxIdChatCompletionStatus>>(),
                sp.GetRequiredService<ICommandFinalizeEmitter<NyxIdChatAcceptedReceipt, NyxIdChatCompletionStatus, AGUIEvent>>(),
                sp.GetRequiredService<ICommandDurableCompletionResolver<NyxIdChatAcceptedReceipt, NyxIdChatCompletionStatus>>(),
                sp.GetService<ILogger<DefaultCommandInteractionService<NyxIdApprovalCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, AGUIEvent, NyxIdChatCompletionStatus>>>(),
                sp.GetRequiredService<ICommandObservationLifecycle<NyxIdApprovalCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError>>(),
                sp.GetRequiredService<ICommandReceiptFactory<NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt>>(),
                sp.GetRequiredService<ICommandObservationScopeLeasePreparation<NyxIdApprovalCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError>>()));
        services.TryAddSingleton<IRealtimeSession<NyxIdApprovalCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>(sp =>
            sp.GetRequiredService<ICommandInteractionService<NyxIdApprovalCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>());
        services.TryAddSingleton<ICommandInteractionService<NyxIdActionContinuationCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>(sp =>
            new DefaultCommandInteractionService<NyxIdActionContinuationCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, AGUIEvent, NyxIdChatCompletionStatus>(
                sp.GetRequiredService<ICommandDispatchPipeline<NyxIdActionContinuationCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError>>(),
                sp.GetRequiredService<IEventOutputStream<AGUIEvent, AGUIEvent>>(),
                sp.GetRequiredService<ICommandCompletionPolicy<AGUIEvent, NyxIdChatCompletionStatus>>(),
                sp.GetRequiredService<ICommandFinalizeEmitter<NyxIdChatAcceptedReceipt, NyxIdChatCompletionStatus, AGUIEvent>>(),
                sp.GetRequiredService<ICommandDurableCompletionResolver<NyxIdChatAcceptedReceipt, NyxIdChatCompletionStatus>>(),
                sp.GetService<ILogger<DefaultCommandInteractionService<NyxIdActionContinuationCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, AGUIEvent, NyxIdChatCompletionStatus>>>(),
                sp.GetRequiredService<ICommandObservationLifecycle<NyxIdActionContinuationCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError>>(),
                sp.GetRequiredService<ICommandReceiptFactory<NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt>>(),
                sp.GetRequiredService<ICommandObservationScopeLeasePreparation<NyxIdActionContinuationCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError>>(),
                probeDurableCompletionWhileLive: true));
        services.TryAddSingleton<IRealtimeSession<NyxIdActionContinuationCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>(sp =>
            sp.GetRequiredService<ICommandInteractionService<NyxIdActionContinuationCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>());
    }

    private static void AddNyxIdLifecycleCommands(IServiceCollection services)
    {
        services.TryAddSingleton<ICommandTargetResolver<NyxIdChatConversationCreateCommand, NyxIdChatConversationCreateCommandTarget, NyxIdChatLifecycleCommandStartError>, NyxIdChatConversationCreateCommandTargetResolver>();
        services.TryAddSingleton<ICommandTargetResolver<NyxIdChatConversationDeleteCommand, NyxIdChatConversationDeleteCommandTarget, NyxIdChatLifecycleCommandStartError>, NyxIdChatConversationDeleteCommandTargetResolver>();
        services.TryAddSingleton<ICommandEnvelopeFactory<NyxIdChatConversationCreateCommand>, NyxIdChatLifecycleCommandEnvelopeFactory>();
        services.TryAddSingleton<ICommandEnvelopeFactory<NyxIdChatConversationDeleteCommand>, NyxIdChatLifecycleCommandEnvelopeFactory>();
        services.TryAddSingleton<ICommandTargetDispatcher<NyxIdChatConversationCreateCommandTarget>, ActorCommandTargetDispatcher<NyxIdChatConversationCreateCommandTarget>>();
        services.TryAddSingleton<ICommandTargetDispatcher<NyxIdChatConversationDeleteCommandTarget>, ActorCommandTargetDispatcher<NyxIdChatConversationDeleteCommandTarget>>();
        services.TryAddSingleton<ICommandReceiptFactory<NyxIdChatConversationCreateCommandTarget, NyxIdChatLifecycleCommandReceipt>, NyxIdChatCreateLifecycleCommandReceiptFactory>();
        services.TryAddSingleton<ICommandReceiptFactory<NyxIdChatConversationDeleteCommandTarget, NyxIdChatLifecycleCommandReceipt>, NyxIdChatDeleteLifecycleCommandReceiptFactory>();
        services.TryAddSingleton<ICommandDispatchPipeline<NyxIdChatConversationCreateCommand, NyxIdChatConversationCreateCommandTarget, NyxIdChatLifecycleCommandReceipt, NyxIdChatLifecycleCommandStartError>, DefaultCommandDispatchPipeline<NyxIdChatConversationCreateCommand, NyxIdChatConversationCreateCommandTarget, NyxIdChatLifecycleCommandReceipt, NyxIdChatLifecycleCommandStartError>>();
        services.TryAddSingleton<ICommandDispatchPipeline<NyxIdChatConversationDeleteCommand, NyxIdChatConversationDeleteCommandTarget, NyxIdChatLifecycleCommandReceipt, NyxIdChatLifecycleCommandStartError>, DefaultCommandDispatchPipeline<NyxIdChatConversationDeleteCommand, NyxIdChatConversationDeleteCommandTarget, NyxIdChatLifecycleCommandReceipt, NyxIdChatLifecycleCommandStartError>>();
        // Refactor (iter77/cluster-077-cqrs-command-outcome-stream-rpc):
        //   Old pattern: NyxIdChat create awaited actor outcome via stream-RPC primitive (DispatchAndAwaitOutcomeAsync)
        //   New principle (narrow scope): NyxIdChat create returns honest accepted ACK; terminal facts via committed events
        services.TryAddSingleton<ICommandDispatchService<NyxIdChatConversationCreateCommand, NyxIdChatLifecycleCommandReceipt, NyxIdChatLifecycleCommandStartError>, DefaultCommandDispatchService<NyxIdChatConversationCreateCommand, NyxIdChatConversationCreateCommandTarget, NyxIdChatLifecycleCommandReceipt, NyxIdChatLifecycleCommandStartError>>();
        services.TryAddSingleton<ICommandDispatchService<NyxIdChatConversationDeleteCommand, NyxIdChatLifecycleCommandReceipt, NyxIdChatLifecycleCommandStartError>, DefaultCommandDispatchService<NyxIdChatConversationDeleteCommand, NyxIdChatConversationDeleteCommandTarget, NyxIdChatLifecycleCommandReceipt, NyxIdChatLifecycleCommandStartError>>();
    }

    private static NyxIdRelayOptions BindRelayOptions(IConfiguration? configuration)
    {
        var options = new NyxIdRelayOptions();
        configuration?.GetSection("Aevatar:NyxId:Relay").Bind(options);
        return options;
    }

    private static NyxIdAssistantActionsOptions BindAssistantActionsOptions(
        IConfiguration? configuration)
    {
        var options = new NyxIdAssistantActionsOptions();
        configuration?.GetSection(NyxIdAssistantActionsOptions.ConfigSection).Bind(options);
        return options;
    }

    private static NyxIdChatCanaryEffectFaultOptions BindCanaryEffectFaultOptions(
        IConfiguration? configuration)
    {
        var enabled = configuration?.GetValue<bool>(
            $"{NyxIdChatCanaryEffectFaultOptions.ConfigSection}:Enabled") == true;
        return new NyxIdChatCanaryEffectFaultOptions { Enabled = enabled };
    }

}
