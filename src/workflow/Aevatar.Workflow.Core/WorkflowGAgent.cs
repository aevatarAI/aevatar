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
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Foundation.Abstractions.EventModules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core;

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
    private readonly Dictionary<string, string> _roleActorIdsByRoleId = new(StringComparer.OrdinalIgnoreCase);
    private readonly IActorRuntime _runtime;
    private readonly IRoleAgentTypeResolver _roleAgentTypeResolver;
    private readonly IReadOnlyList<IEventModuleFactory> _eventModuleFactories;
    private readonly IReadOnlyList<IWorkflowModuleDependencyExpander> _moduleDependencyExpanders;
    private readonly IReadOnlyList<IWorkflowModuleConfigurator> _moduleConfigurators;

    public WorkflowGAgent(
        IActorRuntime runtime,
        IRoleAgentTypeResolver roleAgentTypeResolver,
        IEnumerable<IEventModuleFactory> eventModuleFactories,
        IEnumerable<IWorkflowModuleDependencyExpander> moduleDependencyExpanders,
        IEnumerable<IWorkflowModuleConfigurator> moduleConfigurators)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _roleAgentTypeResolver = roleAgentTypeResolver ?? throw new ArgumentNullException(nameof(roleAgentTypeResolver));
        _eventModuleFactories = eventModuleFactories?.ToList()
            ?? throw new ArgumentNullException(nameof(eventModuleFactories));
        _moduleDependencyExpanders = moduleDependencyExpanders?.OrderBy(x => x.Order).ToList()
            ?? throw new ArgumentNullException(nameof(moduleDependencyExpanders));
        _moduleConfigurators = moduleConfigurators?.OrderBy(x => x.Order).ToList()
            ?? throw new ArgumentNullException(nameof(moduleConfigurators));
    }

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
        _roleActorIdsByRoleId.Clear();

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
        if (!ValidateBeforeRun(request, out var validationError))
        {
            await PublishAsync(new ChatResponseEvent
            {
                Content = validationError ?? "工作流执行校验失败",
                MessageId = request.MessageId,
            }, EventDirection.Up);
            return;
        }

        if (_compiledWorkflow == null)
        {
            await PublishAsync(new ChatResponseEvent
            {
                Content = "工作流未编译或未配置", MessageId = request.MessageId,
            }, EventDirection.Up);
            return;
        }

        if (GetType() == typeof(WorkflowGAgent))
        {
            Logger.LogWarning(
                "WorkflowGAgent {ActorId} is handling ChatRequestEvent directly. This path is kept for compatibility and will be deprecated; prefer per-run WorkflowExecutionGAgent.",
                Id);
        }

        await EnsureAgentTreeAsync();
        var runId = ResolveRunId(request);

        await PublishAsync(new StartWorkflowEvent
        {
            WorkflowName = _compiledWorkflow.Name,
            RunId = runId,
            Input = request.Prompt,
        }, EventDirection.Self);
    }

    // ─── 工作流完成 ───

    /// <summary>处理工作流完成事件：汇总结果，更新统计。</summary>
    [EventHandler]
    public virtual async Task HandleWorkflowCompleted(WorkflowCompletedEvent evt)
    {
        Logger.LogInformation("工作流 {Name} 完成: {Success}", evt.WorkflowName, evt.Success);
        State.TotalExecutions++;
        if (evt.Success) State.SuccessfulExecutions++;
        else State.FailedExecutions++;

        // 使用 AG-UI 事件发布最终结果
        await PublishAsync(new TextMessageEndEvent
        {
            Content = evt.Success ? evt.Output : $"工作流执行失败: {evt.Error}",
            MessageId = evt.RunId,
        }, EventDirection.Up);
    }

    // ─── Agent 树管理 ───

    /// <summary>确保子 Agent 树已按 workflow 定义创建。</summary>
    private async Task EnsureAgentTreeAsync()
    {
        if (_childAgentIds.Count > 0 || _compiledWorkflow == null) return;

        var roleAgentType = _roleAgentTypeResolver.ResolveRoleAgentType();

        foreach (var role in _compiledWorkflow.Roles)
        {
            var roleActorId = ResolveRoleActorId(role.Id);
            var actor = await _runtime.CreateAsync(roleAgentType, roleActorId);
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

            await _runtime.LinkAsync(Id, actor.Id);
            _childAgentIds.Add(actor.Id);
            _roleActorIdsByRoleId[role.Id] = actor.Id;
        }
        Logger.LogInformation("Agent 树创建完成: {Count} 个 role agents", _childAgentIds.Count);
    }

    // ─── Cognitive Modules 装配 ───

    /// <summary>
    /// Discovers required modules using dependency expanders and installs them.
    /// Module-specific setup is delegated to registered configurators.
    /// </summary>
    private void InstallCognitiveModules()
    {
        if (_eventModuleFactories.Count == 0) return;

        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var expander in _moduleDependencyExpanders)
            expander.Expand(_compiledWorkflow, needed);

        Logger.LogInformation("Installing cognitive modules: {Modules}", string.Join(", ", needed));

        var modules = new List<IEventModule>();
        foreach (var name in needed)
        {
            foreach (var factory in _eventModuleFactories)
            {
                if (factory.TryCreate(name, out var m) && m != null)
                {
                    ConfigureModule(m);
                    modules.Add(m);
                    break;
                }
            }
        }

        if (modules.Count > 0) SetModules(modules);
    }

    private void ConfigureModule(IEventModule module)
    {
        if (_compiledWorkflow == null)
            return;

        foreach (var configurator in _moduleConfigurators)
            configurator.Configure(module, _compiledWorkflow);
    }

    /// <summary>
    /// Allows subclasses to inject pre-run validation.
    /// </summary>
    protected virtual bool ValidateBeforeRun(ChatRequestEvent request, out string? validationError)
    {
        validationError = null;
        return true;
    }

    /// <summary>
    /// Resolves the physical actor ID for a logical workflow role ID.
    /// </summary>
    /// <param name="roleId">Logical role ID in workflow definition.</param>
    /// <returns>Physical actor ID used by runtime routing.</returns>
    public string ResolveTargetRoleActorId(string roleId)
    {
        if (string.IsNullOrWhiteSpace(roleId))
            return roleId;

        return _roleActorIdsByRoleId.TryGetValue(roleId, out var actorId)
            ? actorId
            : roleId;
    }

    /// <summary>
    /// Returns current workflow definition snapshot for execution-agent bootstrap.
    /// </summary>
    public WorkflowDefinitionSnapshot GetWorkflowDefinitionSnapshot() =>
        new(State.WorkflowYaml, State.WorkflowName);

    /// <summary>
    /// Allows subclasses to customize physical actor IDs for role agents.
    /// </summary>
    protected virtual string ResolveRoleActorId(string roleId) => roleId;

    protected virtual string ResolveRunId(ChatRequestEvent request)
    {
        if (request.Metadata.TryGetValue(ChatRequestMetadataKeys.RunId, out var runIdFromMetadata) &&
            !string.IsNullOrWhiteSpace(runIdFromMetadata))
            return runIdFromMetadata;

        return Guid.NewGuid().ToString("N");
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
