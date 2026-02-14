// ─────────────────────────────────────────────────────────────
// WorkflowGAgent — 工作流编排入口
//
// 用户和 Actor 系统之间的唯一桥梁。
// 持有 workflow YAML（State 中的 source of truth），
// 动态创建 RoleGAgent 子 Agent 树，
// 通过 Event Modules 驱动工作流执行。
//
// 职责：
// 1. 接收用户消息 → 触发工作流
// 2. 持有 + 验证 + 升级 workflow YAML
// 3. 创建 / 管理子 Agent 层级
// 4. 编排执行（通过 WorkflowLoopModule 等）
// 5. 聚合结果 → 流式返回
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Workflows.Core.Modules;
using Aevatar.Workflows.Core.Primitives;
using Aevatar.Workflows.Core.Validation;
using Aevatar.Foundation.Abstractions.EventModules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflows.Core;

/// <summary>
/// 工作流 GAgent。App 的 root Actor。
/// 持有 workflow YAML，动态创建 Agent 树，编排多 Agent 协作。
/// </summary>
public class WorkflowGAgent : GAgentBase<WorkflowState>
{
    // ─── 编译缓存（内存瞬态，激活时从 YAML 重建） ───
    private WorkflowDefinition? _compiledWorkflow;
    private readonly WorkflowParser _parser = new();
    private readonly List<string> _childAgentIds = [];

    // ─── 生命周期 ───

    /// <inheritdoc />
    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(State.WorkflowYaml))
            TryCompile(State.WorkflowYaml);

        InstallCognitiveModules();
        await base.OnActivateAsync(ct);
    }

    /// <summary>
    /// 配置工作流 YAML 并立即编译、重装模块。
    /// </summary>
    public void ConfigureWorkflow(string workflowYaml, string workflowName)
    {
        State.WorkflowYaml = workflowYaml ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(workflowName))
            State.WorkflowName = workflowName;

        _childAgentIds.Clear();

        if (string.IsNullOrWhiteSpace(State.WorkflowYaml))
        {
            State.Compiled = false;
            State.CompilationError = "workflow yaml is empty";
            _compiledWorkflow = null;
        }
        else
        {
            TryCompile(State.WorkflowYaml);
        }

        InstallCognitiveModules();
    }

    /// <inheritdoc />
    public override Task<string> GetDescriptionAsync()
    {
        var status = State.Compiled ? "compiled" : "invalid";
        return Task.FromResult($"WorkflowGAgent[{State.WorkflowName}] v{State.Version} ({status})");
    }

    // ─── 用户消息入口 ───

    /// <summary>处理用户聊天请求：触发工作流执行。</summary>
    [EventHandler]
    public async Task HandleChatRequest(ChatRequestEvent request)
    {
        if (_compiledWorkflow == null)
        {
            await PublishAsync(new ChatResponseEvent
            {
                Content = "工作流未编译或未配置", SessionId = request.SessionId,
            }, EventDirection.Up);
            return;
        }

        await EnsureAgentTreeAsync();

        await PublishAsync(new StartWorkflowEvent
        {
            WorkflowName = _compiledWorkflow.Name,
            RunId = Guid.NewGuid().ToString("N"),
            Input = request.Prompt,
        }, EventDirection.Self);
    }

    // ─── 工作流完成 ───

    /// <summary>处理工作流完成事件：汇总结果，更新统计。</summary>
    [EventHandler]
    public async Task HandleWorkflowCompleted(WorkflowCompletedEvent evt)
    {
        Logger.LogInformation("工作流 {Name} 完成: {Success}", evt.WorkflowName, evt.Success);
        State.TotalExecutions++;
        if (evt.Success) State.SuccessfulExecutions++;
        else State.FailedExecutions++;

        // 使用 AG-UI 事件发布最终结果
        await PublishAsync(new TextMessageEndEvent
        {
            Content = evt.Success ? evt.Output : $"工作流执行失败: {evt.Error}",
        }, EventDirection.Up);
    }

    // ─── Agent 树管理 ───

    /// <summary>确保子 Agent 树已按 workflow 定义创建。</summary>
    private async Task EnsureAgentTreeAsync()
    {
        if (_childAgentIds.Count > 0 || _compiledWorkflow == null) return;

        var runtime = Services.GetRequiredService<IActorRuntime>();
        var roleAgentTypeResolver = Services.GetRequiredService<IRoleAgentTypeResolver>();
        var roleAgentType = roleAgentTypeResolver.ResolveRoleAgentType();

        foreach (var role in _compiledWorkflow.Roles)
        {
            var actor = await runtime.CreateAsync(roleAgentType, role.Id);
            if (actor.Agent is IRoleAgent roleAgent)
            {
                roleAgent.SetRoleName(role.Name);
                await roleAgent.ConfigureAsync(new RoleAgentConfig
                {
                    SystemPrompt = role.SystemPrompt,
                    ProviderName = role.Provider ?? "deepseek",
                    Model = role.Model,
                });
            }
            else
            {
                throw new InvalidOperationException(
                    $"Role agent type '{roleAgentType.FullName}' does not implement IRoleAgent.");
            }

            await runtime.LinkAsync(Id, actor.Id);
            _childAgentIds.Add(actor.Id);
        }
        Logger.LogInformation("Agent 树创建完成: {Count} 个 role agents", _childAgentIds.Count);
    }

    // ─── Cognitive Modules 装配 ───

    /// <summary>
    /// Discovers required module types from workflow steps and installs them.
    /// Always includes workflow_loop (the orchestrator); other modules are
    /// derived from the step types declared in the compiled workflow YAML.
    /// No hardcoded module list — the workflow definition is the source of truth.
    /// </summary>
    private void InstallCognitiveModules()
    {
        var factories = Services.GetServices<IEventModuleFactory>().ToList();
        if (factories.Count == 0) return;

        // workflow_loop is always required (drives step sequencing)
        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "workflow_loop" };

        // Collect module names from workflow steps and dynamic sub-step parameters.
        if (_compiledWorkflow != null)
            CollectRequiredModuleTypes(_compiledWorkflow.Steps, needed);

        // Expand implicit transitive dependencies between modules.
        ExpandImplicitModuleDependencies(needed);

        Logger.LogInformation("Installing cognitive modules: {Modules}", string.Join(", ", needed));

        var modules = new List<IEventModule>();
        foreach (var name in needed)
        {
            foreach (var factory in factories)
            {
                if (factory.TryCreate(name, out var m) && m != null)
                {
                    if (m is WorkflowLoopModule loop && _compiledWorkflow != null)
                        loop.SetWorkflow(_compiledWorkflow);
                    modules.Add(m);
                    break;
                }
            }
        }

        if (modules.Count > 0) SetModules(modules);
    }

    /// <summary>
    /// Recursively collects required module types from workflow step definitions.
    /// Includes top-level step types plus dynamic sub-step declarations.
    /// </summary>
    private static void CollectRequiredModuleTypes(List<StepDefinition> steps, HashSet<string> types)
    {
        foreach (var step in steps)
        {
            types.Add(step.Type);

            if (!string.IsNullOrWhiteSpace(step.TargetRole))
                types.Add("llm_call");

            // Include dynamic sub-step declarations (e.g. foreach -> sub_step_type: parallel).
            foreach (var (key, value) in step.Parameters)
            {
                if (key.EndsWith("_step_type", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    types.Add(value);
                }
            }

            // foreach defaults to parallel when sub_step_type is not specified.
            if ((step.Type.Equals("foreach", StringComparison.OrdinalIgnoreCase) ||
                 step.Type.Equals("for_each", StringComparison.OrdinalIgnoreCase)) &&
                !step.Parameters.ContainsKey("sub_step_type"))
            {
                types.Add("parallel");
            }

            if (step.Children is { Count: > 0 })
                CollectRequiredModuleTypes(step.Children, types);
        }
    }

    /// <summary>
    /// Expands inferred dependencies that are required for dynamic dispatch.
    /// </summary>
    private static void ExpandImplicitModuleDependencies(HashSet<string> types)
    {
        // parallel fanout emits llm_call sub-steps.
        if (types.Contains("parallel") ||
            types.Contains("parallel_fanout") ||
            types.Contains("fan_out"))
        {
            types.Add("llm_call");
        }
    }

    // ─── 编译 + 验证 ───

    private void TryCompile(string yaml)
    {
        try
        {
            var workflow = _parser.Parse(yaml);
            var errors = WorkflowValidator.Validate(workflow);
            if (errors.Count > 0)
            {
                State.Compiled = false;
                State.CompilationError = string.Join("; ", errors);
                _compiledWorkflow = null;
                return;
            }
            _compiledWorkflow = workflow;
            State.Compiled = true;
            State.CompilationError = "";
        }
        catch (Exception ex)
        {
            State.Compiled = false;
            State.CompilationError = ex.Message;
            _compiledWorkflow = null;
        }
    }
}
