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
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Foundation.Abstractions.EventModules;
using Google.Protobuf.WellKnownTypes;
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
    private readonly IActorRuntime _runtime;
    private readonly IRoleAgentTypeResolver _roleAgentTypeResolver;
    private readonly IEventModuleFactory _eventModuleFactory;
    private readonly IReadOnlyList<IWorkflowModuleDependencyExpander> _moduleDependencyExpanders;
    private readonly IReadOnlyList<IWorkflowModuleConfigurator> _moduleConfigurators;

    public WorkflowGAgent(
        IActorRuntime runtime,
        IRoleAgentTypeResolver roleAgentTypeResolver,
        IEventModuleFactory eventModuleFactory,
        IEnumerable<IWorkflowModulePack> modulePacks)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _roleAgentTypeResolver = roleAgentTypeResolver ?? throw new ArgumentNullException(nameof(roleAgentTypeResolver));
        _eventModuleFactory = eventModuleFactory ?? throw new ArgumentNullException(nameof(eventModuleFactory));

        var packs = modulePacks?.ToList()
            ?? throw new ArgumentNullException(nameof(modulePacks));

        _moduleDependencyExpanders = packs
            .SelectMany(x => x.DependencyExpanders)
            .GroupBy(x => x.GetType())
            .Select(x => x.First())
            .OrderBy(x => x.Order)
            .ToList();

        _moduleConfigurators = packs
            .SelectMany(x => x.Configurators)
            .GroupBy(x => x.GetType())
            .Select(x => x.First())
            .OrderBy(x => x.Order)
            .ToList();
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
        var incomingWorkflowName = string.IsNullOrWhiteSpace(workflowName) ? string.Empty : workflowName.Trim();
        var currentWorkflowName = string.IsNullOrWhiteSpace(State.WorkflowName) ? string.Empty : State.WorkflowName.Trim();
        if (!string.IsNullOrWhiteSpace(currentWorkflowName) &&
            !string.IsNullOrWhiteSpace(incomingWorkflowName) &&
            !string.Equals(currentWorkflowName, incomingWorkflowName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"WorkflowGAgent '{Id}' is already bound to workflow '{State.WorkflowName}' and cannot switch to '{workflowName}'.");
        }

        State.WorkflowYaml = workflowYaml ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(incomingWorkflowName))
            State.WorkflowName = incomingWorkflowName;

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
        SchedulePersistWorkflowBinding(workflowName);
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
            Input = request.Prompt,
        }, EventDirection.Self);
    }

    [EventHandler]
    public async Task HandleConfigureWorkflow(ConfigureWorkflowEvent request)
    {
        ConfigureWorkflow(request.WorkflowYaml, request.WorkflowName);
        await PersistWorkflowBindingAsync(request.WorkflowName);
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

        var roleAgentType = _roleAgentTypeResolver.ResolveRoleAgentType();
        if (!typeof(IRoleAgent).IsAssignableFrom(roleAgentType))
        {
            throw new InvalidOperationException(
                $"Role agent type '{roleAgentType.FullName}' does not implement IRoleAgent.");
        }

        foreach (var role in _compiledWorkflow.Roles)
        {
            var childActorId = BuildChildActorId(role.Id);
            var actor = await _runtime.CreateAsync(roleAgentType, childActorId);
            await _runtime.LinkAsync(Id, actor.Id);

            await actor.HandleEventAsync(CreateRoleAgentConfigureEnvelope(role));

            _childAgentIds.Add(actor.Id);
        }
        Logger.LogInformation("Agent 树创建完成: {Count} 个 role agents", _childAgentIds.Count);
    }

    private string BuildChildActorId(string roleId)
    {
        if (string.IsNullOrWhiteSpace(roleId))
            throw new InvalidOperationException("Role id is required to create child actor.");

        return $"{Id}:{roleId.Trim()}";
    }

    // ─── Cognitive Modules 装配 ───

    /// <summary>
    /// Discovers required modules using dependency expanders and installs them.
    /// Module-specific setup is delegated to registered configurators.
    /// </summary>
    private void InstallCognitiveModules()
    {
        if (_moduleDependencyExpanders.Count == 0) return;

        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var expander in _moduleDependencyExpanders)
            expander.Expand(_compiledWorkflow, needed);

        Logger.LogInformation("Installing cognitive modules: {Modules}", string.Join(", ", needed));

        var modules = new List<IEventModule>();
        foreach (var name in needed)
        {
            if (_eventModuleFactory.TryCreate(name, out var m) && m != null)
            {
                ConfigureModule(m);
                modules.Add(m);
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

    private void SchedulePersistWorkflowBinding(string workflowName)
    {
        _ = PersistWorkflowBindingSafeAsync(workflowName);
    }

    private async Task PersistWorkflowBindingSafeAsync(string workflowName)
    {
        try
        {
            await PersistWorkflowBindingAsync(workflowName);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to persist workflow binding metadata for actor {ActorId}", Id);
        }
    }

    private async Task PersistWorkflowBindingAsync(string workflowName, CancellationToken ct = default)
    {
        if (ManifestStore == null || string.IsNullOrWhiteSpace(Id))
            return;

        var manifest = await ManifestStore.LoadAsync(Id, ct) ?? new AgentManifest { AgentId = Id };
        var agentTypeName = GetType().AssemblyQualifiedName ?? GetType().FullName ?? GetType().Name;
        manifest.AgentTypeName = agentTypeName;

        if (!string.IsNullOrWhiteSpace(workflowName))
            manifest.Metadata[WorkflowManifestMetadataKeys.WorkflowName] = workflowName.Trim();

        await ManifestStore.SaveAsync(Id, manifest, ct);
    }

    private EventEnvelope CreateRoleAgentConfigureEnvelope(RoleDefinition role) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new ConfigureRoleAgentEvent
            {
                RoleName = role.Name ?? string.Empty,
                // Keep provider/model empty when workflow does not specify them,
                // so RoleGAgent can resolve from the globally configured default provider.
                ProviderName = string.IsNullOrWhiteSpace(role.Provider) ? string.Empty : role.Provider,
                Model = string.IsNullOrWhiteSpace(role.Model) ? string.Empty : role.Model,
                SystemPrompt = role.SystemPrompt ?? string.Empty,
            }),
            PublisherId = Id,
            Direction = EventDirection.Self,
            CorrelationId = Guid.NewGuid().ToString("N"),
        };
}
