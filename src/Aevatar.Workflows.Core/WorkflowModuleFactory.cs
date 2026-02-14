// ─────────────────────────────────────────────────────────────
// WorkflowModuleFactory — 认知事件模块工厂
// 注册所有核心工作流原语的 IEventModule 实现
// ─────────────────────────────────────────────────────────────

using Aevatar.Workflows.Core.Modules;
using Aevatar.Foundation.Abstractions.EventModules;

namespace Aevatar.Workflows.Core;

/// <summary>
/// 认知模块工厂。按名字创建工作流相关的 Event Module。
/// 覆盖所有核心原语：llm_call / vote / fan_out / parallel /
/// conditional / assign / while / workflow_call / transform /
/// tool_call / connector_call / checkpoint / retrieve_facts。
/// </summary>
public sealed class WorkflowModuleFactory : IEventModuleFactory
{
    /// <inheritdoc />
    public bool TryCreate(string name, out IEventModule? module)
    {
        module = name switch
        {
            // ─── 流程控制 ───
            "workflow_loop"                     => new WorkflowLoopModule(),
            "conditional"                       => new ConditionalModule(),
            "while" or "loop"                   => new WhileModule(),
            "workflow_call" or "sub_workflow"    => new WorkflowCallModule(),
            "checkpoint"                        => new CheckpointModule(),
            "assign"                            => new AssignModule(),

            // ─── 并行 / 共识 ───
            "parallel_fanout" or "parallel" or "fan_out" => new ParallelFanOutModule(),
            "vote_consensus" or "vote"                    => new VoteConsensusModule(),

            // ─── 迭代 ───
            "foreach" or "for_each"                       => new ForEachModule(),

            // ─── 执行 ───
            "llm_call"                          => new LLMCallModule(),
            "tool_call"                         => new ToolCallModule(),
            "connector_call" or "bridge_call"  => new ConnectorCallModule(),

            // ─── 数据变换 ───
            "transform"                         => new TransformModule(),
            "retrieve_facts"                    => new RetrieveFactsModule(),

            _ => null,
        };

        return module != null;
    }
}
