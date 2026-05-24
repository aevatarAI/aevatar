using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Abstractions.ExternalLinks;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Extensions.Bridge;

public static class ServiceCollectionExtensions
{
    // Refactor (iter30/cluster-030-workflow-step-raw-actor-lifecycle):
    //   Old pattern: WorkflowStepTargetAgentResolver 用 agent_type/agent_id 通过 Type.GetType + AppDomain scan + IRoleAgentTypeResolver 直接 create/link actors,workflow step parameter 暴露 raw CLR lifecycle
    //   New principle: role-level agent_kind 配合 WorkflowRunGAgent runtime lifecycle;step 只用 target_role;删 agent_type/agent_id raw lifecycle 参数 + IWorkflowAgentTypeAliasProvider;Foundation 加 CreateByKindAsync;Bridge 注册 stable kind token
    // Refactor (iter26/cluster-030-telegram-connector-watchdog-blocks-actor-turn):
    //   Old pattern: TelegramBridgeGAgent.ExecuteConnectorWithWatchdogAsync 用 Task.Delay 兜底超时 + ContinueWith race + actor turn 内同步 await /getUpdates 长轮询
    //   New principle: TelegramWaitReplyGAgent owns /getUpdates polling through the existing ExternalLink stream; it sends getUpdates requests via IExternalLinkPort and handles ExternalLinkMessageReceivedEvent continuations, so long polling no longer blocks an actor turn and no new actor type is introduced.
    public static IServiceCollection AddWorkflowBridgeExtensions(this IServiceCollection services)
    {
        services.AddAevatarAgentKindRegistry(builder =>
        {
            builder.Register<TelegramBridgeGAgent>();
            builder.Register<TelegramUserBridgeGAgent>();
            builder.Register<TelegramWaitReplyGAgent>();
        });
        services.AddSingleton<IExternalLinkTransportFactory, TelegramGetUpdatesExternalLinkTransportFactory>();
        return services;
    }
}
