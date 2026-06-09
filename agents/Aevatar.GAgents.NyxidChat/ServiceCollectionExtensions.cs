using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.Middleware;
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
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Abstractions.Slash;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat.LlmSelection;
using Aevatar.GAgents.NyxidChat.Slash;
using Aevatar.GAgents.NyxidChat.WorkflowDraftRun;
using Aevatar.AGUI.Contracts;
using Aevatar.Foundation.Core.TypeSystem;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNyxIdChat(this IServiceCollection services, IConfiguration? configuration = null)
    {
        // Refactor (iter34/cluster-005-mainnet-host-direct-actor-runtime):
        //   Old pattern: Mainnet Host voice bootstrap injected actor runtime/dispatch and built initialization envelopes in the endpoint.
        //   New principle: DI exposes the voice demo Application command port so Host composes the port instead of runtime internals.
        ArgumentNullException.ThrowIfNull(services);
        RuntimeHelpers.RunClassConstructor(typeof(NyxIdChatGAgent).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(AgentRunGAgent).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(ChannelWorkflowDraftRunGAgent).TypeHandle);
        services.AddAevatarAgentKindRegistry(builder => builder.ScanAssemblies(typeof(NyxIdChatGAgent).Assembly));

        services.AddCqrsCore();
        services.AddHttpClient();
        services.TryAddSingleton(provider => BindRelayOptions(configuration));
        services.TryAddSingleton<Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions>(
            provider => provider.GetRequiredService<NyxIdRelayOptions>());
        services.TryAddSingleton<NyxIdRelayTransport>();
        services.TryAddSingleton<NyxIdRelayAuthValidator>();
        services.TryAddSingleton<INyxIdRelayIngressPort, NyxIdRelayIngressPort>();
        services.TryAddSingleton<NyxIdChatLifecycleFacade>();
        AddNyxIdLifecycleCommands(services);

        // ─── Channel LLM reply run dispatch ───
        services.TryAddSingleton<IChannelLlmReplyRunDispatcher, AgentRunDispatcher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChannelSlashCommandHandler, ChannelWorkflowDraftRunSlashCommandHandler>());
        services.TryAddSingleton<ChannelSlashCommandRegistry>();
        services.TryAddSingleton<ChannelWorkflowDraftRunIntentParser>();
        services.TryAddSingleton<ChannelWorkflowDraftRunAdmission>(sp =>
            new ChannelWorkflowDraftRunAdmission(
                sp.GetRequiredService<ChannelWorkflowDraftRunIntentParser>(),
                sp.GetService<Aevatar.GAgentService.Abstractions.Ports.IScopeWorkflowQueryPort>()));
        services.TryAddSingleton<WorkflowDraftRunReplyRenderer>();
        services.TryAddSingleton<IChannelWorkflowDraftRunInteractionPort>(sp =>
            new ChannelWorkflowDraftRunInteractionPort(
                sp.GetRequiredService<Aevatar.Foundation.Abstractions.IActorRuntime>(),
                sp.GetRequiredService<Aevatar.Foundation.Abstractions.IActorDispatchPort>(),
                sp.GetRequiredService<ILogger<ChannelWorkflowDraftRunInteractionPort>>(),
                sp.GetService<TimeProvider>()));
        // Refactor (iter34/cluster-004-voice-bootstrap-application-port):
        //   Old pattern: Mainnet Host/API composed the voice demo agent bootstrap workflow directly.
        //   New principle: NyxID chat owns the actor-targeted bootstrap command port; hosts only opt into the module.
        services.TryAddSingleton<IVoiceDemoAgentCommandPort, VoiceDemoAgentCommandPort>();

        // ─── Conversation turn-runner override + reply generator ───
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
                    sp.GetRequiredService<ILogger<ChannelCardConversationTurnRunner>>());
            }));
        }
        services.TryAddSingleton<IConversationReplyGenerator, NyxIdConversationReplyGenerator>();
        services.TryAddSingleton<IAgentRunReplyGenerationExecutorPort, AgentRunReplyGenerationExecutor>();
        services.TryAddSingleton<IAgentToolReceiptRenderer, AgentToolReceiptRenderer>();
        services.TryAddSingleton<IVoiceDemoAgentCommandPort, VoiceDemoAgentCommandPort>();

        // ─── LLM-call middleware that injects channel context into LLM requests ───
        // Lives here (not in Channel.Runtime) because it implements ILLMCallMiddleware
        // (AI.Abstractions); keeping it in NyxidChat lets Channel.Runtime stay free of
        // AI / Workflow dependencies. ChannelCardActionRouting (workflow resume binding)
        // is in this package for the same reason.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILLMCallMiddleware, ChannelContextMiddleware>());

        // ─── /model slash command (issue #513 phase 5) ───
        // Registered here (not in Channel.Identity) because the handler depends
        // on Studio.Application UserConfig ports; Channel.Identity intentionally
        // does not pull Studio dependencies.
        // Catalog client uses IMemoryCache for the proxy-services TTL cache. AddMemoryCache
        // is idempotent: hosts that already registered MemoryCacheOptions keep control of
        // cache size/compaction behavior; hosts that did not register one get the default.
        services.AddMemoryCache();
        services.TryAddSingleton<INyxIdLlmServiceCatalogClient, NyxIdLlmServiceCatalogClient>();
        // These are consumed by singleton turn-runner/slash handlers. They create
        // short scopes internally for UserConfig ports instead of capturing
        // potentially scoped query/command services at construction time.
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
        AddNyxIdStreamingInteractions(services);

        return services;
    }

    private static void AddNyxIdStreamingInteractions(IServiceCollection services)
    {
        // Refactor (iter21/cluster-002-request-path-projection-session-priming):
        //   Old pattern: request handlers synchronously ensure projection/session leases and wait on live sinks.
        //   New principle: commands use accepted receipts; observation is owned by binders or attach-only sessions.
        services.TryAddSingleton(NyxIdChatInteractionFactories.CreateChatResolver);
        services.TryAddSingleton(NyxIdChatInteractionFactories.CreateApprovalResolver);
        services.TryAddSingleton(NyxIdChatInteractionFactories.CreateChatObservationLifecycle);
        services.TryAddSingleton(NyxIdChatInteractionFactories.CreateApprovalObservationLifecycle);
        services.TryAddSingleton<ICommandEnvelopeFactory<NyxIdChatCommand>, NyxIdChatCommandEnvelopeFactory>();
        services.TryAddSingleton<ICommandEnvelopeFactory<NyxIdApprovalCommand>, NyxIdApprovalCommandEnvelopeFactory>();
        services.TryAddSingleton<ICommandTargetDispatcher<NyxIdChatCommandTarget>, ActorCommandTargetDispatcher<NyxIdChatCommandTarget>>();
        services.TryAddSingleton<ICommandReceiptFactory<NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt>, NyxIdChatAcceptedReceiptFactory>();
        services.TryAddSingleton<ICommandDispatchPipeline<NyxIdChatCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError>, DefaultCommandDispatchPipeline<NyxIdChatCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError>>();
        services.TryAddSingleton<ICommandDispatchPipeline<NyxIdApprovalCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError>, DefaultCommandDispatchPipeline<NyxIdApprovalCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError>>();
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
                sp.GetRequiredService<ICommandReceiptFactory<NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt>>()));
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
                sp.GetRequiredService<ICommandReceiptFactory<NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt>>()));
        services.TryAddSingleton<IRealtimeSession<NyxIdApprovalCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>(sp =>
            sp.GetRequiredService<ICommandInteractionService<NyxIdApprovalCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus>>());
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
}
