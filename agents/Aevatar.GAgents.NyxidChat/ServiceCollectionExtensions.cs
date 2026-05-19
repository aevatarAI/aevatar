using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.Middleware;
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
using Aevatar.Presentation.AGUI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNyxIdChat(this IServiceCollection services, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        RuntimeHelpers.RunClassConstructor(typeof(NyxIdChatGAgent).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(AgentRunGAgent).TypeHandle);

        services.AddHttpClient();
        services.TryAddSingleton(provider => BindRelayOptions(configuration));
        services.TryAddSingleton<Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions>(
            provider => provider.GetRequiredService<NyxIdRelayOptions>());
        services.TryAddSingleton<INyxIdRelayReplayGuard>(provider =>
        {
            var options = provider.GetRequiredService<Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions>();
            return new NyxIdRelayReplayGuard(
                TimeSpan.FromSeconds(Math.Max(1, options.CallbackReplayWindowSeconds)),
                TimeProvider.System);
        });
        services.TryAddSingleton<NyxIdRelayTransport>();
        services.TryAddSingleton<NyxIdRelayAuthValidator>();

        // ─── Channel LLM reply run dispatch ───
        services.TryAddSingleton<IChannelLlmReplyRunDispatcher, AgentRunDispatcher>();

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

        return services;
    }

    private static NyxIdRelayOptions BindRelayOptions(IConfiguration? configuration)
    {
        var options = new NyxIdRelayOptions();
        configuration?.GetSection("Aevatar:NyxId:Relay").Bind(options);
        return options;
    }
}
