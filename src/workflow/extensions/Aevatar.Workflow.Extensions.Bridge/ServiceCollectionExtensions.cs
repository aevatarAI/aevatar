using Aevatar.Foundation.Core.TypeSystem;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Extensions.Bridge;

public static class ServiceCollectionExtensions
{
    // Refactor (iter30/cluster-030-workflow-step-raw-actor-lifecycle):
    //   Old pattern: WorkflowStepTargetAgentResolver 用 agent_type/agent_id 通过 Type.GetType + AppDomain scan + IRoleAgentTypeResolver 直接 create/link actors,workflow step parameter 暴露 raw CLR lifecycle
    //   New principle: role-level agent_kind 配合 WorkflowRunGAgent runtime lifecycle;step 只用 target_role;删 agent_type/agent_id raw lifecycle 参数 + IWorkflowAgentTypeAliasProvider;Foundation 加 CreateByKindAsync;Bridge 注册 stable kind token
    public static IServiceCollection AddWorkflowBridgeExtensions(this IServiceCollection services)
    {
        services.AddAevatarAgentKindRegistry(builder =>
        {
            builder.Register<TelegramBridgeGAgent>();
            builder.Register<TelegramUserBridgeGAgent>();
            builder.Register<TelegramWaitReplyGAgent>();
        });
        return services;
    }
}
