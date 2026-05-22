using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Abstractions.TypeSystem;

namespace Aevatar.Workflow.Extensions.Bridge;

/// <summary>
/// Telegram user-account bridge agent.
/// Uses the same protocol as <see cref="TelegramBridgeGAgent"/>, but defaults to connector name "telegram_user".
/// </summary>
[GAgent("workflow.telegram-user-bridge")]
// Refactor (iter30/cluster-030-workflow-step-raw-actor-lifecycle):
//   Old pattern: WorkflowStepTargetAgentResolver 用 agent_type/agent_id 通过 Type.GetType + AppDomain scan + IRoleAgentTypeResolver 直接 create/link actors,workflow step parameter 暴露 raw CLR lifecycle
//   New principle: role-level agent_kind 配合 WorkflowRunGAgent runtime lifecycle;step 只用 target_role;删 agent_type/agent_id raw lifecycle 参数 + IWorkflowAgentTypeAliasProvider;Foundation 加 CreateByKindAsync;Bridge 注册 stable kind token
public sealed class TelegramUserBridgeGAgent : TelegramBridgeGAgent
{
    protected override string DefaultConnectorName => "telegram_user";

    public TelegramUserBridgeGAgent(
        IActorRuntime runtime,
        IConnectorRegistry connectorRegistry)
        : base(runtime, connectorRegistry)
    {
    }
}
