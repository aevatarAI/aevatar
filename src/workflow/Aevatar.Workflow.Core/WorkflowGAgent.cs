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
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Foundation.Abstractions.EventModules;
using Google.Protobuf;
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
    private readonly IWorkflowDefinitionResolver? _workflowDefinitionResolver;
    private readonly IReadOnlyList<IWorkflowModuleDependencyExpander> _moduleDependencyExpanders;
    private readonly IReadOnlyList<IWorkflowModuleConfigurator> _moduleConfigurators;
    private readonly ISet<string> _knownModuleStepTypes;
    private readonly SubWorkflowOrchestrator _subWorkflowOrchestrator;

    public WorkflowGAgent(
        IActorRuntime runtime,
        IRoleAgentTypeResolver roleAgentTypeResolver,
        IEventModuleFactory eventModuleFactory,
        IEnumerable<IWorkflowModulePack> modulePacks,
        IWorkflowDefinitionResolver? workflowDefinitionResolver = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _roleAgentTypeResolver = roleAgentTypeResolver ?? throw new ArgumentNullException(nameof(roleAgentTypeResolver));
        _eventModuleFactory = eventModuleFactory ?? throw new ArgumentNullException(nameof(eventModuleFactory));
        _workflowDefinitionResolver = workflowDefinitionResolver;

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

        _knownModuleStepTypes = WorkflowPrimitiveCatalog.BuildCanonicalStepTypeSet(
            packs
                .SelectMany(x => x.Modules)
                .SelectMany(x => x.Names));

        _subWorkflowOrchestrator = new SubWorkflowOrchestrator(
            _runtime,
            _workflowDefinitionResolver,
            () => Services,
            () => Id,
            () => Logger,
            (evt, token) => PersistDomainEventAsync(evt, token),
            (events, token) => PersistDomainEventsAsync(events, token),
            (evt, direction, token) => PublishAsync(evt, direction, token),
            (actorId, evt) => SendToAsync(actorId, evt));
    }

    // ─── 生命周期 ───

    /// <inheritdoc />
    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        RebuildCompiledWorkflowCache();

        InstallCognitiveModules();
        await base.OnActivateAsync(ct);
    }

    /// <summary>
    /// 配置工作流 YAML 并立即编译、重装模块。
    /// </summary>
    public async Task ConfigureWorkflowAsync(
        string workflowYaml,
        string? workflowName,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null,
        CancellationToken ct = default)
    {
        EnsureWorkflowNameCanBind(workflowName);
        var configureWorkflowEvent = new ConfigureWorkflowEvent
        {
            WorkflowName = workflowName ?? string.Empty,
            WorkflowYaml = workflowYaml ?? string.Empty,
        };
        if (inlineWorkflowYamls != null)
        {
            foreach (var (key, value) in inlineWorkflowYamls)
                configureWorkflowEvent.InlineWorkflowYamls[key] = value;
        }

        await PersistDomainEventAsync(configureWorkflowEvent, ct);
        RebuildCompiledWorkflowCache();
        _childAgentIds.Clear();

        InstallCognitiveModules();
        await PersistWorkflowBindingAsync(workflowName ?? string.Empty, ct);
    }

    /// <summary>
    /// Reconfigures workflow YAML without the workflow-name binding check.
    /// Used by <see cref="HandleReconfigureAndExecute"/> for dynamic reconfiguration.
    /// Validation must pass before any state mutation is persisted.
    /// </summary>
    private async Task<WorkflowCompilationResult> ReconfigureWorkflowBypassingBindingAsync(string workflowYaml, CancellationToken ct = default)
    {
        WorkflowDefinition parsed;
        try
        {
            parsed = _parser.Parse(workflowYaml);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "ReconfigureWorkflowBypassingBinding: parse failed.");
            return WorkflowCompilationResult.Invalid(ex.Message);
        }

        var validationErrors = ValidateWorkflowDefinition(parsed);
        if (validationErrors.Count > 0)
            return WorkflowCompilationResult.Invalid(string.Join("; ", validationErrors));

        var workflowName = parsed.Name ?? string.Empty;

        await PersistDomainEventAsync(new ConfigureWorkflowEvent
        {
            WorkflowName = workflowName,
            WorkflowYaml = workflowYaml,
        }, ct);
        RebuildCompiledWorkflowCache();
        _childAgentIds.Clear();

        InstallCognitiveModules();
        await PersistWorkflowBindingAsync(workflowName, ct);
        return WorkflowCompilationResult.Success(parsed);
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
                Content = "Workflow is not compiled or configured.", SessionId = request.SessionId,
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
        await ConfigureWorkflowAsync(request.WorkflowYaml, request.WorkflowName, request.InlineWorkflowYamls);
    }

    /// <summary>
    /// Dynamically reconfigures this actor with new workflow YAML and starts execution.
    /// Bypasses the workflow name binding check because this is an intentional
    /// reconfiguration triggered by the <c>dynamic_workflow</c> primitive.
    /// </summary>
    [EventHandler]
    public async Task HandleReconfigureAndExecute(ReconfigureAndExecuteWorkflowEvent request)
    {
        var yaml = request.WorkflowYaml ?? string.Empty;
        if (string.IsNullOrWhiteSpace(yaml))
        {
            Logger.LogWarning("ReconfigureAndExecute: empty workflow YAML, ignoring.");
            await PublishAsync(new ChatResponseEvent { Content = "Dynamic workflow YAML is empty." }, EventDirection.Up);
            return;
        }

        var reconfigureResult = await ReconfigureWorkflowBypassingBindingAsync(yaml);
        if (!reconfigureResult.Compiled || _compiledWorkflow == null)
        {
            var reason = string.IsNullOrWhiteSpace(reconfigureResult.CompilationError)
                ? "Dynamic workflow YAML compilation failed."
                : $"Dynamic workflow YAML compilation failed: {reconfigureResult.CompilationError}";
            Logger.LogWarning("ReconfigureAndExecute: YAML compilation failed. Error={Error}", reconfigureResult.CompilationError);
            await PublishAsync(new ChatResponseEvent { Content = reason }, EventDirection.Up);
            return;
        }

        await EnsureAgentTreeAsync();

        await PublishAsync(new StartWorkflowEvent
        {
            WorkflowName = _compiledWorkflow.Name,
            Input = request.Input ?? string.Empty,
        }, EventDirection.Self);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleSubWorkflowInvokeRequested(SubWorkflowInvokeRequestedEvent request)
    {
        await _subWorkflowOrchestrator.HandleInvokeRequestedAsync(request, State, CancellationToken.None);
    }

    // ─── 工作流完成 ───

    [AllEventHandler(Priority = 50, AllowSelfHandling = true)]
    public async Task HandleWorkflowCompletionEnvelope(EventEnvelope envelope)
    {
        if (envelope.Payload?.Is(WorkflowCompletedEvent.Descriptor) != true)
            return;

        var completed = envelope.Payload.Unpack<WorkflowCompletedEvent>();
        if (await _subWorkflowOrchestrator.TryHandleCompletionAsync(
                completed,
                envelope.PublisherId,
                State,
                CancellationToken.None))
            return;

        if (!string.Equals(envelope.PublisherId, Id, StringComparison.Ordinal))
        {
            Logger.LogDebug(
                "Ignore external WorkflowCompletedEvent from publisher={PublisherId} run={RunId}.",
                envelope.PublisherId,
                completed.RunId);
            return;
        }

        await HandleWorkflowCompleted(completed);
    }

    /// <summary>处理当前工作流完成事件：汇总结果，更新统计。</summary>
    public async Task HandleWorkflowCompleted(WorkflowCompletedEvent evt)
    {
        await PersistDomainEventAsync(evt);
        await _subWorkflowOrchestrator.CleanupPendingInvocationsForRunAsync(evt.RunId, State, CancellationToken.None);
        if (evt.Success)
        {
            Logger.LogInformation(
                "Workflow {Name} completed: success={Success} run={RunId} outputLen={OutputLen}",
                evt.WorkflowName,
                evt.Success,
                evt.RunId,
                (evt.Output ?? string.Empty).Length);
        }
        else
        {
            Logger.LogError(
                "Workflow {Name} failed: run={RunId} error={Error} outputLen={OutputLen}",
                evt.WorkflowName,
                evt.RunId,
                string.IsNullOrWhiteSpace(evt.Error) ? "(none)" : evt.Error,
                (evt.Output ?? string.Empty).Length);
        }

        // 使用 AG-UI 事件发布最终结果
        await PublishAsync(new TextMessageEndEvent
        {
            Content = evt.Success ? evt.Output : $"Workflow execution failed: {evt.Error}",
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
            var actor = await _runtime.GetAsync(childActorId)
                        ?? await _runtime.CreateAsync(roleAgentType, childActorId);
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
                continue;
            }

            var workflowName = _compiledWorkflow?.Name ?? State.WorkflowName;
            throw new InvalidOperationException(
                $"Workflow '{workflowName}' requires module '{name}', but no module registration was found.");
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

    protected override WorkflowState TransitionState(WorkflowState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ConfigureWorkflowEvent>(ApplyConfigureWorkflow)
            .On<WorkflowCompletedEvent>(ApplyWorkflowCompleted)
            .On<SubWorkflowBindingUpsertedEvent>(SubWorkflowOrchestrator.ApplySubWorkflowBindingUpserted)
            .On<SubWorkflowInvocationRegisteredEvent>(SubWorkflowOrchestrator.ApplySubWorkflowInvocationRegistered)
            .On<SubWorkflowInvocationCompletedEvent>(SubWorkflowOrchestrator.ApplySubWorkflowInvocationCompleted)
            .OrCurrent();

    private WorkflowState ApplyConfigureWorkflow(WorkflowState current, ConfigureWorkflowEvent evt)
    {
        var next = current.Clone();
        next.WorkflowYaml = evt.WorkflowYaml ?? string.Empty;
        next.InlineWorkflowYamls.Clear();
        foreach (var (workflowNameKey, workflowYamlValue) in evt.InlineWorkflowYamls)
        {
            var normalizedWorkflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(workflowNameKey);
            if (string.IsNullOrWhiteSpace(normalizedWorkflowName) ||
                string.IsNullOrWhiteSpace(workflowYamlValue))
            {
                continue;
            }

            next.InlineWorkflowYamls[normalizedWorkflowName] = workflowYamlValue;
        }

        var incomingWorkflowName = string.IsNullOrWhiteSpace(evt.WorkflowName)
            ? string.Empty
            : evt.WorkflowName.Trim();
        if (!string.IsNullOrWhiteSpace(incomingWorkflowName))
            next.WorkflowName = incomingWorkflowName;

        var compileResult = EvaluateWorkflowCompilation(next.WorkflowYaml);
        next.Compiled = compileResult.Compiled;
        next.CompilationError = compileResult.CompilationError;
        next.Version = current.Version + 1;
        if (compileResult.Compiled && compileResult.Workflow != null)
            SubWorkflowOrchestrator.PruneIdleSubWorkflowBindings(next, compileResult.Workflow);
        return next;
    }

    private static WorkflowState ApplyWorkflowCompleted(WorkflowState current, WorkflowCompletedEvent evt)
    {
        var next = current.Clone();
        next.TotalExecutions++;
        if (evt.Success)
            next.SuccessfulExecutions++;
        else
            next.FailedExecutions++;
        return next;
    }

    private WorkflowCompilationResult EvaluateWorkflowCompilation(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return WorkflowCompilationResult.Invalid("workflow yaml is empty");

        try
        {
            var workflow = _parser.Parse(yaml);
            var errors = ValidateWorkflowDefinition(workflow);
            if (errors.Count > 0)
                return WorkflowCompilationResult.Invalid(string.Join("; ", errors));

            return WorkflowCompilationResult.Success(workflow);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "EvaluateWorkflowCompilation: parse/validation failed.");
            return WorkflowCompilationResult.Invalid(ex.Message);
        }
    }

    private void RebuildCompiledWorkflowCache()
    {
        if (string.IsNullOrWhiteSpace(State.WorkflowYaml))
        {
            _compiledWorkflow = null;
            return;
        }

        try
        {
            var workflow = _parser.Parse(State.WorkflowYaml);
            var errors = ValidateWorkflowDefinition(workflow);
            _compiledWorkflow = errors.Count == 0 ? workflow : null;
            if (errors.Count > 0)
            {
                Logger.LogWarning(
                    "RebuildCompiledWorkflowCache: workflow has validation errors. errors={Errors}",
                    string.Join("; ", errors));
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "RebuildCompiledWorkflowCache: parse failed.");
            _compiledWorkflow = null;
        }
    }

    private void EnsureWorkflowNameCanBind(string? workflowName)
    {
        var incomingWorkflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(workflowName);
        var currentWorkflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(State.WorkflowName);
        if (!string.IsNullOrWhiteSpace(currentWorkflowName) &&
            !string.IsNullOrWhiteSpace(incomingWorkflowName) &&
            !string.Equals(currentWorkflowName, incomingWorkflowName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"WorkflowGAgent '{Id}' is already bound to workflow '{State.WorkflowName}' and cannot switch to '{workflowName}'.");
        }
    }

    private readonly record struct WorkflowCompilationResult(bool Compiled, string CompilationError, WorkflowDefinition? Workflow)
    {
        public static WorkflowCompilationResult Success(WorkflowDefinition workflow) =>
            new(true, string.Empty, workflow);

        public static WorkflowCompilationResult Invalid(string error) =>
            new(false, error ?? string.Empty, null);
    }

    private List<string> ValidateWorkflowDefinition(WorkflowDefinition workflow)
    {
        var knownStepTypes = new HashSet<string>(_knownModuleStepTypes, StringComparer.OrdinalIgnoreCase);
        knownStepTypes.UnionWith(WorkflowPrimitiveCatalog.BuiltInCanonicalTypes);
        ExpandKnownStepTypesFromFactory(workflow, knownStepTypes);

        return WorkflowValidator.Validate(
            workflow,
            new WorkflowValidator.WorkflowValidationOptions
            {
                RequireKnownStepTypes = true,
                KnownStepTypes = knownStepTypes,
            },
            availableWorkflowNames: null);
    }

    private void ExpandKnownStepTypesFromFactory(WorkflowDefinition workflow, ISet<string> knownStepTypes)
    {
        foreach (var stepType in EnumerateReferencedStepTypes(workflow.Steps))
        {
            var canonical = WorkflowPrimitiveCatalog.ToCanonicalType(stepType);
            if (string.IsNullOrWhiteSpace(canonical) || knownStepTypes.Contains(canonical))
                continue;

            if (_eventModuleFactory.TryCreate(canonical, out _))
                knownStepTypes.Add(canonical);
        }
    }

    private static IEnumerable<string> EnumerateReferencedStepTypes(IEnumerable<StepDefinition> steps)
    {
        foreach (var step in steps)
        {
            yield return step.Type;

            foreach (var (key, value) in step.Parameters)
            {
                if (WorkflowPrimitiveCatalog.IsStepTypeParameterKey(key) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    yield return value;
                }
            }

            if (step.Children is { Count: > 0 })
            {
                foreach (var childType in EnumerateReferencedStepTypes(step.Children))
                    yield return childType;
            }
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

    private EventEnvelope CreateRoleAgentConfigureEnvelope(RoleDefinition role)
    {
        var configure = new ConfigureRoleAgentEvent
        {
            RoleName = role.Name ?? string.Empty,
            // Keep provider/model empty when workflow does not specify them,
            // so RoleGAgent resolves from globally configured default provider.
            ProviderName = string.IsNullOrWhiteSpace(role.Provider) ? string.Empty : role.Provider,
            Model = string.IsNullOrWhiteSpace(role.Model) ? string.Empty : role.Model,
            SystemPrompt = role.SystemPrompt ?? string.Empty,
            MaxTokens = role.MaxTokens ?? 0,
            MaxToolRounds = role.MaxToolRounds ?? 0,
            MaxHistoryMessages = role.MaxHistoryMessages ?? 0,
            StreamBufferCapacity = role.StreamBufferCapacity ?? 0,
            EventModules = role.EventModules ?? string.Empty,
            EventRoutes = role.EventRoutes ?? string.Empty,
        };

        if (role.Temperature.HasValue)
            configure.Temperature = role.Temperature.Value;

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(configure),
            PublisherId = Id,
            Direction = EventDirection.Self,
            CorrelationId = Guid.NewGuid().ToString("N"),
        };
    }
}
