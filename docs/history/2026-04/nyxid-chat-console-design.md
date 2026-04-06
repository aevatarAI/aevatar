# NyxID-Chat Console IDE 设计方案（Final）

> 目标：用户通过 CLI app Console 页面的 NyxID-Chat 对话，完成 workflow YAML 编写、script 编写、service binding、script promote、draft-run、service invoke 全流程。

## 一、现状审计

### 1.1 工具注册机制

所有 `IAgentToolSource` 通过 DI 全局注册（`TryAddEnumerable`），`NyxIdChatGAgent` 构造函数接收 `IEnumerable<IAgentToolSource>`，**所有已启用的 tool source 自动对所有 agent 可用**。不存在"未注入"问题——问题在于 tool source 内部的条件发现逻辑。

### 1.2 各能力现状

| 能力 | 后端 API | AI Tool | Console UI（直连） | Console UI（AI 代理） | 差距 |
|------|---------|---------|-------------------|---------------------|------|
| **Service Invoke** | ✅ `streamInvoke` | ✅ `list_services` + `invoke_service` | ✅ dropdown 选 service 直连 | ⚠️ 工具存在但条件不满足 | ServiceInvokeOptions 需 scope 动态解析 |
| **Draft Run** | ✅ `POST /scopes/{scopeId}/workflow/draft-run` | ❌ 无工具 | ❌ `api.scope.streamDraftRun()` 已写但未调用 | ❌ 无 | 缺工具 + 前端调用 |
| **Workflow YAML 编写** | ⚠️ `IWorkflowDefinitionCatalog` 只读 | ❌ 只有 3 个读工具 | ❌ 仅 Studio 页 | ❌ 无 | 缺写端口 + 写工具 |
| **Script 编写** | ✅ 全套端口就绪 | ✅ 7 个工具覆盖全生命周期 | ✅ ScriptsStudio 页 | ✅ 工具已可用 | **无差距** |
| **Script Promote** | ✅ `IScriptToolCatalogCommandAdapter.PromoteAsync` | ✅ `script_promote` | ✅ ScriptsStudio 按钮 | ✅ 工具已可用 | **无差距** |
| **Service Binding** | ✅ `IScopeBindingCommandPort.UpsertAsync` | ❌ 无工具 | ⚠️ 各页面分散调用 | ❌ 无 | 缺查询/解绑端口 + 工具 |

### 1.3 已有工具清单

**ScriptingAgentToolSource**（7 工具，完备无需改动）：

| 工具 | 能力 | ApprovalMode | IsReadOnly |
|------|------|-------------|-----------|
| `script_list` | 列表/搜索 | NeverRequire | true |
| `script_status` | 查询状态 + 定义信息 | NeverRequire | true |
| `script_source` | 读源码 + proto | NeverRequire | true |
| `script_compile` | Roslyn 编译（创建 revision） | AlwaysRequire | false |
| `script_execute` | 沙箱执行 | AlwaysRequire | false |
| `script_promote` | 提升为正式版 | AlwaysRequire | false |
| `script_rollback` | 回滚版本 | AlwaysRequire | true (destructive) |

依赖端口：`IScriptToolCatalogQueryAdapter`、`IScriptToolCompilationAdapter`、`IScriptToolSandboxExecutionAdapter`、`IScriptToolCatalogCommandAdapter`。全部已有实现。

**WorkflowAgentToolSource**（3 工具，只读）：

| 工具 | 能力 | 依赖 |
|------|------|------|
| `workflow_status` | 查询运行状态/列表/目录/详情/时间线 | `IWorkflowExecutionQueryApplicationService` |
| `actor_inspect` | 查看 actor 快照/图/列表 | 同上 |
| `event_query` | 查询已提交事件时间线 | 同上 |

**ServiceInvokeAgentToolSource**（2 工具，条件注册）：

| 工具 | 条件 | 问题 |
|------|------|------|
| `list_services` | `TenantId` + `AppId` + `Namespace` 非空 | CLI 场景下这三个值取决于当前 scope，非静态配置 |
| `invoke_service` | 上述 + `EnableInvoke = true` | 同上 |

当 `ServiceInvokeOptions` 未完整配置时，`DiscoverToolsAsync()` 返回空列表——**工具虽注册但不可见**。

### 1.4 关键基础设施

| 组件 | 现状 | 说明 |
|------|------|------|
| `IWorkflowDefinitionCatalog` | 启动后只读 | `Register()` 明确标注 "must not be called after application startup completes" |
| `IScopeBindingCommandPort.UpsertAsync` | ✅ 可用 | 统一入口：workflow/script/GAgent 绑定 |
| `IScopeBindingQueryAdapter` | ❌ 不存在 | 无法列出/查询 binding |
| `IScopeBindingUnbindAdapter` | ❌ 不存在 | 无法解除绑定 |
| `~/.aevatar/workflows/` | ✅ 本地 YAML 存储 | `AevatarPaths.Workflows` 管理 |
| `WorkflowParser` | ✅ 完整 | YAML → `WorkflowDefinition`，含校验 |
| `AgentToolRequestContext.CurrentMetadata` | ✅ 可用 | AsyncLocal 传递 scope_id、NyxID token 等 |
| `ToolApprovalMiddleware` | ✅ 可用 | 支持 AlwaysRequire / Auto / NeverRequire |
| `ScopeWorkflowCapabilityOptions` | 固定值 | `ServiceAppId = "default"`, `ServiceNamespace = "default"` |

---

## 二、设计总览

### 2.1 原则

1. **扩展现有 ToolSource，不创建平行系统**——`WorkflowAgentToolSource` 追加写工具，`BindingAgentToolSource` 新建
2. **修复 ServiceInvoke 条件发现**——让 scope 动态解析替代静态配置
3. **最小工具面**——只增加 LLM 能有效使用的工具，合并低频操作
4. **写操作一律审批**——`ToolApprovalMiddleware` 拦截所有非只读操作
5. **前端增量增强**——tool_call 结构化渲染，不新建页面

### 2.2 架构图

```
┌─────────────────────────────────────────────────────────────────┐
│  Console UI (ScopePage.tsx)                                      │
│  ┌──────────────┐  ┌──────────────────────────────────────────┐ │
│  │ Chat 对话     │  │ Tool Result 增强渲染                     │ │
│  │ (现有)        │  │  · YamlPreviewPanel (workflow create/up) │ │
│  │               │  │  · DraftRunPanel (执行事件流)             │ │
│  │               │  │  · BindingStatusCard (bind/list)         │ │
│  │               │  │  · ServiceResultPanel (invoke_service)   │ │
│  └──────────────┘  └──────────────────────────────────────────┘ │
│  ┌──────────────────────────────────────────────────────────────┐│
│  │ Service Selector Dropdown (现有，保留直连模式)               ││
│  └──────────────────────────────────────────────────────────────┘│
└───────────────────────────┬─────────────────────────────────────┘
                            │ SSE / tool_call events
┌───────────────────────────▼─────────────────────────────────────┐
│  NyxID-Chat Agent (NyxIdChatGAgent)                              │
│  provider: "nyxid" | system-prompt: system-prompt.md + skills    │
│                                                                   │
│  IEnumerable<IAgentToolSource> (全局 DI，自动注入)               │
│  ┌─────────────────────────────────────────────────────────────┐ │
│  │ 不改动：                                                    │ │
│  │  ScriptingAgentToolSource      (7 tools) ← 已完备          │ │
│  │  NyxIdAgentToolSource          (17 tools)                   │ │
│  │  WebAgentToolSource            (3 tools)                    │ │
│  │  SkillsAgentToolSource         (dynamic)                    │ │
│  │  MCPAgentToolSource            (dynamic)                    │ │
│  │  ChronoStorageAgentToolSource  (dynamic)                    │ │
│  ├─────────────────────────────────────────────────────────────┤ │
│  │ 修复：                                                      │ │
│  │  ServiceInvokeAgentToolSource  (2 tools) ← scope 动态解析  │ │
│  ├─────────────────────────────────────────────────────────────┤ │
│  │ 扩展：                                                      │ │
│  │  WorkflowAgentToolSource       (3 existing + 5 new)        │ │
│  │    新依赖: IWorkflowDefinitionCommandAdapter                │ │
│  │    新依赖: IWorkflowDraftRunAdapter                         │ │
│  ├─────────────────────────────────────────────────────────────┤ │
│  │ 新建：                                                      │ │
│  │  BindingAgentToolSource        (4 new)                      │ │
│  │    依赖: IScopeBindingCommandPort (已有)                    │ │
│  │    依赖: IScopeBindingQueryAdapter (新建)                   │ │
│  │    依赖: IScopeBindingUnbindAdapter (新建)                  │ │
│  └─────────────────────────────────────────────────────────────┘ │
└───────────────────────────┬─────────────────────────────────────┘
                            │
┌───────────────────────────▼─────────────────────────────────────┐
│  端口 / Adapter 实现层                                           │
│                                                                   │
│  ┌─ 修复 ─────────────────────────────────────────────────────┐ │
│  │ ServiceInvokeOptions                                        │ │
│  │   → ScopeDynamicServiceInvokeOptionsResolver                │ │
│  │     从 AgentToolRequestContext.CurrentMetadata["scope_id"]  │ │
│  │     解析 TenantId / AppId / Namespace                       │ │
│  └─────────────────────────────────────────────────────────────┘ │
│                                                                   │
│  ┌─ 新建端口 ─────────────────────────────────────────────────┐ │
│  │ IWorkflowDefinitionCommandAdapter                           │ │
│  │   实现: LocalWorkflowDefinitionCommandAdapter               │ │
│  │   存储: ~/.aevatar/workflows/*.yaml (AevatarPaths)          │ │
│  │   校验: WorkflowParser (已有)                               │ │
│  │                                                             │ │
│  │ IWorkflowDraftRunAdapter                                    │ │
│  │   实现: ScopeWorkflowDraftRunAdapter                        │ │
│  │   桥接: POST /scopes/{scopeId}/workflow/draft-run (已有)    │ │
│  │                                                             │ │
│  │ IScopeBindingQueryAdapter                                   │ │
│  │   实现: ScopeBindingQueryAdapter                            │ │
│  │   桥接: IServiceGovernanceQueryPort (已有)                  │ │
│  │                                                             │ │
│  │ IScopeBindingUnbindAdapter                                  │ │
│  │   实现: ScopeBindingUnbindAdapter                           │ │
│  │   桥接: IServiceGovernanceCommandPort (已有)                │ │
│  └─────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────┘
```

---

## 三、修复项：ServiceInvoke Scope 动态解析

### 3.1 问题

`ServiceInvokeAgentToolSource.DiscoverToolsAsync()` 检查 `_options.TenantId/AppId/Namespace` 是否非空。CLI 场景下这些值取决于用户当前操作的 scope，不是静态配置。当未配置时返回空列表——工具不可见。

### 3.2 方案

引入 `IScopeContextResolver` 接口，让 tool source 在发现时动态解析 scope context：

```csharp
// Aevatar.AI.ToolProviders.ServiceInvoke/Ports/IScopeContextResolver.cs
public interface IScopeContextResolver
{
    /// 从当前请求上下文解析 scope 信息。
    /// 返回 null 表示当前上下文无 scope（工具不注册）。
    ScopeContext? Resolve();
}

public sealed record ScopeContext(
    string TenantId,
    string AppId,
    string Namespace);
```

修改 `ServiceInvokeAgentToolSource`：

```csharp
// 现有逻辑
if (string.IsNullOrEmpty(_options.TenantId) || ...)
    return Array.Empty<IAgentTool>();

// 修改为
var scope = _scopeResolver?.Resolve()
    ?? (HasStaticConfig(_options) ? StaticScope(_options) : null);
if (scope is null)
    return Array.Empty<IAgentTool>();
// 将 scope 传入工具构造
```

CLI 场景的实现：

```csharp
// 从 AgentToolRequestContext 读取 scope_id，
// 映射到 ScopeWorkflowCapabilityOptions 的固定 AppId/Namespace
public sealed class MetadataScopeContextResolver : IScopeContextResolver
{
    public ScopeContext? Resolve()
    {
        var metadata = AgentToolRequestContext.CurrentMetadata;
        if (metadata is null || !metadata.TryGetValue("scope_id", out var scopeId))
            return null;
        return new ScopeContext(
            TenantId: scopeId,
            AppId: "default",       // ScopeWorkflowCapabilityOptions.ServiceAppId
            Namespace: "default");  // ScopeWorkflowCapabilityOptions.ServiceNamespace
    }
}
```

> **注意**：`DiscoverToolsAsync` 在 agent activation 时调用一次，此时可能还没有 request context。
> 解决方案：tool source 始终注册工具（只要 resolver 或 static config 存在），工具内部在 `ExecuteAsync` 时从 `AgentToolRequestContext` 动态解析 scope。
> 修改 `ListServicesTool` 和 `InvokeServiceTool`：执行时优先从 request context 取 scope，fallback 到构造时注入的 static options。

### 3.3 影响范围

| 文件 | 改动 |
|------|------|
| `ServiceInvokeAgentToolSource.cs` | 构造函数增加 `IScopeContextResolver?`，条件判断改为"resolver 存在 OR static config 完整" |
| `ListServicesTool.cs` | `ExecuteAsync` 内增加 `AgentToolRequestContext` scope 动态解析 |
| `InvokeServiceTool.cs` | 同上 |
| `ServiceCollectionExtensions.cs` (ServiceInvoke) | 注册 `IScopeContextResolver` |
| CLI 宿主层 | 注册 `MetadataScopeContextResolver` |

---

## 四、扩展项：WorkflowAgentToolSource +5 工具

### 4.1 新增端口

#### IWorkflowDefinitionCommandAdapter

```csharp
// Aevatar.AI.ToolProviders.Workflow/Ports/IWorkflowDefinitionCommandAdapter.cs
public interface IWorkflowDefinitionCommandAdapter
{
    Task<IReadOnlyList<WorkflowDefinitionSummary>> ListDefinitionsAsync(
        CancellationToken ct = default);

    Task<WorkflowDefinitionSnapshot?> GetDefinitionAsync(
        string workflowName, CancellationToken ct = default);

    Task<WorkflowDefinitionCommandResult> CreateAsync(
        WorkflowDefinitionCreateRequest request, CancellationToken ct = default);

    Task<WorkflowDefinitionCommandResult> UpdateAsync(
        WorkflowDefinitionUpdateRequest request, CancellationToken ct = default);
}

public sealed record WorkflowDefinitionSummary(
    string WorkflowName,
    string? Description,
    int StepCount,
    int RoleCount,
    string RevisionId);

public sealed record WorkflowDefinitionSnapshot(
    string WorkflowName,
    string Yaml,
    string RevisionId,
    DateTimeOffset LastModified);

public sealed record WorkflowDefinitionCreateRequest(
    string WorkflowName,
    string Yaml);

public sealed record WorkflowDefinitionUpdateRequest(
    string WorkflowName,
    string Yaml,
    string ExpectedRevisionId);  // 乐观并发

public sealed record WorkflowDefinitionCommandResult(
    bool Success,
    string WorkflowName,
    string? RevisionId,
    string? Yaml,                // 规范化后的 YAML（成功时返回）
    IReadOnlyList<WorkflowValidationDiagnostic> Diagnostics);

public sealed record WorkflowValidationDiagnostic(
    string Severity,    // "error" | "warning" | "info"
    string Message,
    string? StepId,
    string? Field);
```

**实现：`LocalWorkflowDefinitionCommandAdapter`**

存储路径：`~/.aevatar/workflows/{sanitized_name}.yaml`（复用 `AevatarPaths.Workflows`）

```
Create 流程:
  1. 检查同名文件不存在 → 存在则返回 error diagnostic
  2. WorkflowParser.Parse(yaml) → 校验
  3. 有 error 级 diagnostic → 返回失败 + diagnostics
  4. 写入 ~/.aevatar/workflows/{name}.yaml
  5. 计算 revisionId = SHA256(yaml 内容)[0:12]
  6. 返回成功 + 规范化 YAML + revisionId

Update 流程:
  1. 读取现有文件 → 不存在则返回 error
  2. 计算当前 revisionId，对比 ExpectedRevisionId → 不匹配则返回 conflict
  3. WorkflowParser.Parse(yaml) → 校验
  4. 有 error 级 diagnostic → 返回失败 + diagnostics
  5. 覆盖写入
  6. 返回成功 + 新 revisionId
```

#### IWorkflowDraftRunAdapter

```csharp
// Aevatar.AI.ToolProviders.Workflow/Ports/IWorkflowDraftRunAdapter.cs
public interface IWorkflowDraftRunAdapter
{
    /// 发起 draft-run，返回 run receipt。
    /// 执行事件通过现有 SSE channel 流式返回到前端，工具本身只返回 receipt。
    Task<WorkflowDraftRunReceipt> StartDraftRunAsync(
        WorkflowDraftRunRequest request,
        CancellationToken ct = default);
}

public sealed record WorkflowDraftRunRequest(
    string ScopeId,
    string Yaml,
    string Prompt);

public sealed record WorkflowDraftRunReceipt(
    bool Success,
    string? RunId,
    string? ActorId,
    string? Error);
```

**实现：`ScopeWorkflowDraftRunAdapter`**

桥接已有 `POST /scopes/{scopeId}/workflow/draft-run` 端点。CLI 场景下通过 `AppApiClient.StreamDraftRunAsync()` 调用。

> **设计决策**：draft-run 工具只负责**发起**，不负责流式转发。执行事件流通过 agent 的 SSE subscription channel 自动推送到 Console 前端。工具返回 `RunId` + `ActorId`，前端根据这些 ID 渲染执行面板。

### 4.2 新增工具

| 工具 | 描述 | ApprovalMode | IsReadOnly | 参数 |
|------|------|-------------|-----------|------|
| `workflow_list_defs` | 列出已注册的 workflow 定义 | NeverRequire | true | `filter?` (string) |
| `workflow_read_def` | 读取 workflow YAML 全文 | NeverRequire | true | `workflow_name` (string, required) |
| `workflow_create_def` | 创建新 workflow 定义 | AlwaysRequire | false | `workflow_name` (string, required), `yaml` (string, required) |
| `workflow_update_def` | 更新现有 workflow | AlwaysRequire | false | `workflow_name` (string, required), `yaml` (string, required), `expected_revision` (string, required) |
| `workflow_draft_run` | 试运行 workflow（不持久化） | AlwaysRequire | false | `yaml` (string, required), `prompt` (string, required) |

### 4.3 WorkflowAgentToolSource 改动

```csharp
// 现有
public WorkflowAgentToolSource(
    IWorkflowExecutionQueryApplicationService queryService,
    WorkflowToolOptions options,
    ILogger<WorkflowAgentToolSource>? logger = null)

// 扩展后
public WorkflowAgentToolSource(
    IWorkflowExecutionQueryApplicationService queryService,
    WorkflowToolOptions options,
    IWorkflowDefinitionCommandAdapter? definitionCommand = null,   // 新增，可选
    IWorkflowDraftRunAdapter? draftRun = null,                     // 新增，可选
    ILogger<WorkflowAgentToolSource>? logger = null)
```

`DiscoverToolsAsync` 条件注册：

```csharp
public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct)
{
    var tools = new List<IAgentTool>
    {
        // 现有 3 个只读工具（始终注册）
        new WorkflowStatusTool(_queryService, _options),
        new ActorInspectTool(_queryService, _options),
        new EventQueryTool(_queryService, _options),
    };

    // 定义管理工具（可选）
    if (_definitionCommand is not null)
    {
        tools.Add(new WorkflowListDefsTool(_definitionCommand));
        tools.Add(new WorkflowReadDefTool(_definitionCommand));
        tools.Add(new WorkflowCreateDefTool(_definitionCommand, _options));
        tools.Add(new WorkflowUpdateDefTool(_definitionCommand, _options));
    }

    // Draft-run 工具（可选）
    if (_draftRun is not null)
        tools.Add(new WorkflowDraftRunTool(_draftRun, _options));

    return Task.FromResult<IReadOnlyList<IAgentTool>>(tools);
}
```

---

## 五、新建项：BindingAgentToolSource

### 5.1 新增端口

```csharp
// Aevatar.AI.ToolProviders.Binding/Ports/IScopeBindingQueryAdapter.cs
public interface IScopeBindingQueryAdapter
{
    Task<IReadOnlyList<ScopeBindingEntry>> ListAsync(
        string scopeId, CancellationToken ct = default);

    Task<ScopeBindingHealthStatus?> GetStatusAsync(
        string scopeId, string serviceId, CancellationToken ct = default);
}

public sealed record ScopeBindingEntry(
    string ServiceId,
    string DisplayName,
    ScopeBindingImplementationKind Kind,
    string? RevisionId,
    string? ExpectedActorId,
    bool IsActive);

public sealed record ScopeBindingHealthStatus(
    string ServiceId,
    bool IsHealthy,
    string? ActorId,
    string? ActiveRevision,
    string? LastError,
    DateTimeOffset? LastActiveAt);
```

```csharp
// Aevatar.AI.ToolProviders.Binding/Ports/IScopeBindingUnbindAdapter.cs
public interface IScopeBindingUnbindAdapter
{
    Task<ScopeBindingUnbindResult> UnbindAsync(
        string scopeId, string serviceId, CancellationToken ct = default);
}

public sealed record ScopeBindingUnbindResult(
    bool Success,
    string? Error);
```

**实现**：
- `IScopeBindingQueryAdapter` → 桥接 `IServiceGovernanceQueryPort`（已有，提供 service listing/status）
- `IScopeBindingUnbindAdapter` → 桥接 `IServiceGovernanceCommandPort`（已有，deactivate + remove）

### 5.2 工具

| 工具 | 描述 | ApprovalMode | IsReadOnly | IsDestructive | 参数 |
|------|------|-------------|-----------|--------------|------|
| `binding_list` | 列出当前 scope 的所有 service bindings | NeverRequire | true | false | `scope_id?` (从 context 自动取) |
| `binding_status` | 查看指定 binding 的运行状态 | NeverRequire | true | false | `service_id` (required) |
| `binding_bind` | 绑定 workflow/script/GAgent 为 service | AlwaysRequire | false | false | `kind` (enum: workflow\|scripting\|gagent), `workflow_name?`, `script_id?`, `script_revision?`, `gagent_type?`, `display_name?`, `service_id?` |
| `binding_unbind` | 解除绑定 | AlwaysRequire | false | true | `service_id` (required) |

### 5.3 ToolSource 结构

```csharp
// Aevatar.AI.ToolProviders.Binding/BindingAgentToolSource.cs
public sealed class BindingAgentToolSource : IAgentToolSource
{
    private readonly IScopeBindingCommandPort _commandPort;
    private readonly IScopeBindingQueryAdapter? _queryAdapter;
    private readonly IScopeBindingUnbindAdapter? _unbindAdapter;
    private readonly BindingToolOptions _options;

    public BindingAgentToolSource(
        IScopeBindingCommandPort commandPort,
        BindingToolOptions options,
        IScopeBindingQueryAdapter? queryAdapter = null,
        IScopeBindingUnbindAdapter? unbindAdapter = null,
        ILogger<BindingAgentToolSource>? logger = null)
    {
        _commandPort = commandPort;
        _options = options;
        _queryAdapter = queryAdapter;
        _unbindAdapter = unbindAdapter;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct)
    {
        var tools = new List<IAgentTool>();

        // bind 始终可用（commandPort 必须）
        tools.Add(new BindingBindTool(_commandPort, _options));

        // 查询工具（可选）
        if (_queryAdapter is not null)
        {
            tools.Add(new BindingListTool(_queryAdapter, _options));
            tools.Add(new BindingStatusTool(_queryAdapter, _options));
        }

        // 解绑工具（可选）
        if (_unbindAdapter is not null)
            tools.Add(new BindingUnbindTool(_unbindAdapter, _options));

        return Task.FromResult<IReadOnlyList<IAgentTool>>(tools);
    }
}
```

### 5.4 BindingBindTool `kind` 参数与请求映射

```csharp
// binding_bind 的参数 JSON Schema
{
    "kind": { "type": "string", "enum": ["workflow", "scripting", "gagent"] },
    "workflow_name": { "type": "string", "description": "Required when kind=workflow" },
    "script_id": { "type": "string", "description": "Required when kind=scripting" },
    "script_revision": { "type": "string", "description": "Optional, latest if omitted" },
    "gagent_type": { "type": "string", "description": "Required when kind=gagent, full type name" },
    "display_name": { "type": "string" },
    "service_id": { "type": "string", "description": "Auto-generated if omitted" }
}
```

映射到 `ScopeBindingUpsertRequest`：

```csharp
var request = new ScopeBindingUpsertRequest(
    ScopeId: scopeId,  // 从 AgentToolRequestContext 获取
    ImplementationKind: kind switch
    {
        "workflow" => ScopeBindingImplementationKind.Workflow,
        "scripting" => ScopeBindingImplementationKind.Scripting,
        "gagent" => ScopeBindingImplementationKind.GAgent,
    },
    Workflow: kind == "workflow" ? new(WorkflowName: args.WorkflowName) : null,
    Script: kind == "scripting" ? new(ScriptId: args.ScriptId, Revision: args.ScriptRevision) : null,
    GAgent: kind == "gagent" ? new(ActorTypeName: args.GAgentType) : null,
    DisplayName: args.DisplayName,
    ServiceId: args.ServiceId);
```

### 5.5 项目结构

```
src/Aevatar.AI.ToolProviders.Binding/
├── Aevatar.AI.ToolProviders.Binding.csproj
├── BindingAgentToolSource.cs
├── BindingToolOptions.cs
├── ServiceCollectionExtensions.cs
├── Ports/
│   ├── IScopeBindingQueryAdapter.cs
│   └── IScopeBindingUnbindAdapter.cs
├── Models/
│   ├── ScopeBindingEntry.cs
│   ├── ScopeBindingHealthStatus.cs
│   └── ScopeBindingUnbindResult.cs
└── Tools/
    ├── BindingListTool.cs
    ├── BindingStatusTool.cs
    ├── BindingBindTool.cs
    └── BindingUnbindTool.cs
```

---

## 六、DI 注册

### 6.1 Bootstrap 层改动

```csharp
// Aevatar.Bootstrap.Extensions.AI/ServiceCollectionExtensions.cs

// 新增 feature flag
public sealed class AevatarAIToolOptions
{
    // ... 现有 flags ...
    public bool EnableBindingTools { get; set; } = false;
}

// 注册方法
public static IServiceCollection RegisterBindingTools(
    this IServiceCollection services,
    Action<BindingToolOptions>? configure = null)
{
    if (configure is not null)
        services.Configure(configure);
    else
        services.AddOptions<BindingToolOptions>();

    services.TryAddEnumerable(
        ServiceDescriptor.Singleton<IAgentToolSource, BindingAgentToolSource>());
    return services;
}
```

### 6.2 CLI 宿主层

```csharp
// tools/Aevatar.Tools.Cli 的 DI 配置中追加：

// ServiceInvoke scope 动态解析
services.AddSingleton<IScopeContextResolver, MetadataScopeContextResolver>();

// Workflow 定义管理（本地实现）
services.AddSingleton<IWorkflowDefinitionCommandAdapter, LocalWorkflowDefinitionCommandAdapter>();
services.AddSingleton<IWorkflowDraftRunAdapter, ScopeWorkflowDraftRunAdapter>();

// Binding 工具
services.RegisterBindingTools();
services.AddSingleton<IScopeBindingQueryAdapter, ScopeBindingQueryAdapter>();
services.AddSingleton<IScopeBindingUnbindAdapter, ScopeBindingUnbindAdapter>();
```

---

## 七、前端增强

### 7.1 tool_call 结果渲染契约

工具返回的 JSON 统一携带 `_render_hint` 字段，前端按此分发渲染：

```typescript
type RenderHint =
    | 'yaml_preview'      // workflow_create_def / workflow_update_def
    | 'yaml_content'      // workflow_read_def
    | 'draft_run_receipt'  // workflow_draft_run
    | 'binding_list'       // binding_list
    | 'binding_status'     // binding_status
    | 'binding_result'     // binding_bind / binding_unbind
    | 'service_result'     // invoke_service
    | 'plain';             // 默认 JSON

interface WorkflowYamlPreviewResult {
    _render_hint: 'yaml_preview';
    success: boolean;
    workflow_name: string;
    yaml: string;
    revision_id: string;
    diagnostics: { severity: string; message: string; step_id?: string; field?: string }[];
}

interface DraftRunReceiptResult {
    _render_hint: 'draft_run_receipt';
    success: boolean;
    run_id: string;
    actor_id: string;
    error?: string;
    // 前端收到后，订阅 actor_id 的 SSE 事件流渲染执行过程
}

interface BindingListResult {
    _render_hint: 'binding_list';
    bindings: {
        service_id: string;
        display_name: string;
        kind: 'workflow' | 'scripting' | 'gagent';
        revision_id?: string;
        is_active: boolean;
    }[];
}
```

### 7.2 新增前端组件

```
Frontend/src/runtime/
├── tool-panels/
│   ├── YamlPreviewPanel.tsx      // Monaco readonly + diagnostics 列表
│   ├── DraftRunPanel.tsx         // 执行事件时间线（复用现有 step/event 渲染）
│   ├── BindingStatusCard.tsx     // 表格：service_id | kind | status
│   └── ServiceResultPanel.tsx   // invoke_service 返回值格式化
```

### 7.3 ScopePage.tsx 改动

在现有 tool_call 渲染逻辑中增加分发：

```typescript
function ToolCallResultRenderer({ toolName, result }: { toolName: string; result: string }) {
    try {
        const parsed = JSON.parse(result);
        switch (parsed._render_hint) {
            case 'yaml_preview':
                return <YamlPreviewPanel {...parsed} />;
            case 'draft_run_receipt':
                return <DraftRunPanel runId={parsed.run_id} actorId={parsed.actor_id} />;
            case 'binding_list':
            case 'binding_result':
                return <BindingStatusCard {...parsed} />;
            default:
                return <pre className="text-xs">{result}</pre>;
        }
    } catch {
        return <pre className="text-xs">{result}</pre>;
    }
}
```

### 7.4 DraftRunPanel 特殊处理

`workflow_draft_run` 工具返回 `run_id` + `actor_id` 后，前端需要订阅该 actor 的事件流：

```typescript
function DraftRunPanel({ runId, actorId }: { runId: string; actorId: string }) {
    const [events, setEvents] = useState<RunEvent[]>([]);
    const [status, setStatus] = useState<'running' | 'completed' | 'error'>('running');

    useEffect(() => {
        // 复用现有 SSE 订阅机制
        // 事件已通过 NyxIdChatGAgent 的 subscription channel 推送
        // 只需从 chat message stream 中过滤 actorId 相关事件
    }, [actorId]);

    return (
        <div className="border rounded p-3 mt-2">
            <div className="text-xs font-medium mb-2">
                Draft Run: {runId} ({status})
            </div>
            {events.map(e => <StepEventRow key={e.id} event={e} />)}
        </div>
    );
}
```

> **实现细节**：draft-run 的事件流是否通过 NyxIdChatGAgent 的 SSE channel 自动推送，取决于 draft-run adapter 的实现方式。如果 adapter 通过后端 API 发起 draft-run，事件流会通过独立的 SSE 连接返回，前端需要建立第二个 SSE 订阅。这是 P1 阶段的实现细节。

---

## 八、安全与稳健性

### 8.1 审批矩阵

| 操作类型 | ApprovalMode | IsDestructive | 用户感知 |
|---------|-------------|--------------|---------|
| 所有读操作 | NeverRequire | false | 静默执行 |
| `workflow_create_def` | AlwaysRequire | false | "创建 workflow X，确认？" |
| `workflow_update_def` | AlwaysRequire | false | "更新 workflow X (rev → rev')，确认？" |
| `workflow_draft_run` | AlwaysRequire | false | "运行 draft workflow，确认？" |
| `binding_bind` | AlwaysRequire | false | "绑定 X 为 service，确认？" |
| `binding_unbind` | AlwaysRequire | true | "解绑 service X，确认？⚠️ 不可逆" |
| `script_compile` | AlwaysRequire | false | 现有行为保持（可后续降级为 Auto） |
| `script_promote` | AlwaysRequire | false | "提升 script X revision Y，确认？" |
| `script_rollback` | AlwaysRequire | true | "回滚 script X，确认？⚠️" |
| `invoke_service` | AlwaysRequire | false | "调用 service X endpoint Y，确认？" |

### 8.2 乐观并发

`workflow_update_def` 要求 `expected_revision` 参数：

```
Agent 调用流程：
  1. workflow_read_def(name) → 获取当前 yaml + revision_id
  2. 修改 yaml
  3. workflow_update_def(name, new_yaml, expected_revision=revision_id)
     → 成功：返回新 revision_id
     → 冲突：返回 error + 当前最新 yaml + 当前 revision_id
     → Agent 自动基于最新版本重试
```

### 8.3 Per-Session 速率限制

```csharp
public sealed class BindingToolOptions
{
    public int MaxWriteOperationsPerMinute { get; set; } = 10;
}

public sealed class WorkflowToolOptions
{
    // 现有 ...
    public int MaxWriteOperationsPerMinute { get; set; } = 10;
    public int MaxDraftRunsPerMinute { get; set; } = 3;
    public int MaxYamlSizeChars { get; set; } = 100_000;
}
```

工具内通过简单计数器实现（tool 实例生命周期 = agent 生命周期 = session 生命周期）。

### 8.4 Scope 上下文安全

所有工具在 `ExecuteAsync` 内从 `AgentToolRequestContext.CurrentMetadata["scope_id"]` 获取 scope。**后端端口实现层再次校验 scope 权限**（复用 `ScopeEndpointAccess` 逻辑），不信任 tool context 传递的值作为唯一权限凭证。

---

## 九、工具总表（Final）

### 不改动（22 tools）

| ToolSource | 工具数 | 说明 |
|-----------|--------|------|
| ScriptingAgentToolSource | 7 | 完备，全生命周期 |
| NyxIdAgentToolSource | 17 | NyxID 账户/服务/审批/通知 |
| WebAgentToolSource | 3 | search + fetch + ask_user |
| SkillsAgentToolSource | 1+ | use_skill（动态） |
| MCPAgentToolSource | 动态 | MCP 协议工具 |
| ChronoStorageAgentToolSource | 动态 | 存储工具 |

### 修复（2 tools）

| ToolSource | 工具 | 修复内容 |
|-----------|------|---------|
| ServiceInvokeAgentToolSource | `list_services` | scope 动态解析（原条件不满足时不可见） |
| ServiceInvokeAgentToolSource | `invoke_service` | 同上 |

### 扩展（5 tools）

| ToolSource | 工具 | 新增 |
|-----------|------|------|
| WorkflowAgentToolSource | `workflow_list_defs` | ✅ |
| | `workflow_read_def` | ✅ |
| | `workflow_create_def` | ✅ |
| | `workflow_update_def` | ✅ |
| | `workflow_draft_run` | ✅ |

### 新建（4 tools）

| ToolSource | 工具 | 新建 |
|-----------|------|------|
| BindingAgentToolSource | `binding_list` | ✅ |
| | `binding_status` | ✅ |
| | `binding_bind` | ✅ |
| | `binding_unbind` | ✅ |

### 总计

| 类别 | 数量 |
|------|------|
| 现有不动 | ~32 |
| 修复条件 | 2 |
| 新增工具 | 9 |
| **Agent 工具总数** | ~43 |

---

## 十、新增端口/Adapter 总表

| 端口接口 | 项目位置 | Adapter 实现 | 桥接目标 |
|---------|---------|-------------|---------|
| `IScopeContextResolver` | `Aevatar.AI.ToolProviders.ServiceInvoke` | `MetadataScopeContextResolver` (CLI) | `AgentToolRequestContext` |
| `IWorkflowDefinitionCommandAdapter` | `Aevatar.AI.ToolProviders.Workflow` | `LocalWorkflowDefinitionCommandAdapter` | `~/.aevatar/workflows/` + `WorkflowParser` |
| `IWorkflowDraftRunAdapter` | `Aevatar.AI.ToolProviders.Workflow` | `ScopeWorkflowDraftRunAdapter` | `POST /scopes/{scopeId}/workflow/draft-run` |
| `IScopeBindingQueryAdapter` | `Aevatar.AI.ToolProviders.Binding` | `ScopeBindingQueryAdapter` | `IServiceGovernanceQueryPort` |
| `IScopeBindingUnbindAdapter` | `Aevatar.AI.ToolProviders.Binding` | `ScopeBindingUnbindAdapter` | `IServiceGovernanceCommandPort` |

---

## 十一、实现计划

### P0（核心通路）

| 任务 | 交付物 | 依赖 | 预估文件数 |
|------|--------|------|-----------|
| P0-1 | `IScopeContextResolver` + `MetadataScopeContextResolver` + ServiceInvoke 修复 | 无 | 4 |
| P0-2 | `IWorkflowDefinitionCommandAdapter` 端口 + `LocalWorkflowDefinitionCommandAdapter` | 无 | 5 |
| P0-3 | 5 个 workflow 工具 + `WorkflowAgentToolSource` 扩展 | P0-2 | 7 |
| P0-4 | `BindingAgentToolSource` 新项目 + 4 个 binding 工具 + 2 个端口 | 无 | 10 |
| P0-5 | DI 注册（Bootstrap + CLI 宿主） | P0-1~4 | 3 |

**P0-1/P0-2/P0-4 可并行**。P0-3 依赖 P0-2。P0-5 依赖全部。

### P1（前端 + Draft-Run）

| 任务 | 交付物 | 依赖 |
|------|--------|------|
| P1-1 | `IWorkflowDraftRunAdapter` + `ScopeWorkflowDraftRunAdapter` | P0 |
| P1-2 | `workflow_draft_run` 工具 | P1-1 |
| P1-3 | Console tool_call 渲染增强（4 个面板组件） | P0 |
| P1-4 | DraftRunPanel SSE 事件订阅 | P1-2 + P1-3 |

### P2（稳健性）

| 任务 | 交付物 |
|------|--------|
| P2-1 | Per-session rate limit（工具内计数器） |
| P2-2 | `script_compile` ApprovalMode 降级为 Auto |
| P2-3 | `ScriptCompilationResult.Diagnostics` 结构化（line/column/severity） |
| P2-4 | Binding query adapter 实现 + 端到端测试 |

---

## 十二、典型 E2E 场景

### 场景 A：从零创建 Workflow + 绑定 + 试运行

```
User: "创建一个客服 workflow：先意图分类，查询类调 search service，投诉类转人工"

Agent:
  [workflow_list_defs] → 确认无同名定义
  [workflow_create_def name="customer-support" yaml="...生成的 YAML..."]
    → ToolApproval → 用户确认
    → Console 渲染 YamlPreviewPanel：
      ┌─────────────────────────────────────┐
      │ ✅ customer-support (rev: a3f8e2)    │
      │ steps: intent_classify → conditional │
      │        → search_call / human_input   │
      │ diagnostics: 0 errors, 0 warnings    │
      └─────────────────────────────────────┘

User: "试运行一下，输入'我的订单到哪了'"

Agent:
  [workflow_draft_run yaml="..." prompt="我的订单到哪了"]
    → ToolApproval → 用户确认
    → Console 渲染 DraftRunPanel：
      ┌─────────────────────────────────────┐
      │ ▶ Draft Run: run-7x2k               │
      │ [✓] intent_classify → "查询类"       │
      │ [✓] search_call → "订单 #1234 在途"  │
      │ [✓] respond → "您的订单正在配送中..." │
      │ ✅ 完成 (1.2s)                       │
      └─────────────────────────────────────┘

User: "没问题，绑定到当前 scope"

Agent:
  [binding_bind kind=workflow workflow_name=customer-support display_name="客服"]
    → ToolApproval → 用户确认
    → Console 渲染 BindingStatusCard：
      ┌─────────────────────────────────────┐
      │ ✅ Bound: customer-support-svc       │
      │ kind: workflow | active: true        │
      └─────────────────────────────────────┘
```

### 场景 B：编写 Script + 编译 + 部署

```
User: "写一个 script 监听 OrderCreated 事件，累加每日订单总额到 read model"

Agent:
  [script_compile script_id="daily-order-total"
    source_files={
      "DailyOrderTotalBehavior.cs": "...生成的 C#...",
    }
    proto_files={
      "daily_order_total.proto": "...生成的 proto..."
    }]
    → ToolApproval → 用户确认
    → 返回: success=true, revision="rev-1", diagnostics=[]

User: "promote 然后绑定"

Agent:
  [script_promote script_id="daily-order-total" revision="rev-1"]
    → ToolApproval → 用户确认
  [binding_bind kind=scripting script_id=daily-order-total display_name="每日订单统计"]
    → ToolApproval → 用户确认
```

### 场景 C：通过 AI 代理调用已绑定 Service

```
User: "帮我调用 customer-support service，问一下'最近有什么优惠'"

Agent:
  [list_services] → 找到 customer-support-svc (endpoint: chat)
  [invoke_service service_id=customer-support-svc endpoint=chat input="最近有什么优惠"]
    → ToolApproval → 用户确认
    → Console 渲染 ServiceResultPanel：
      ┌─────────────────────────────────────┐
      │ 📨 customer-support-svc/chat         │
      │ Response: "目前有以下优惠活动..."     │
      └─────────────────────────────────────┘
```

---

## 十三、与 CLAUDE.md 架构约束对齐检查

| 约束 | 本方案 | 符合？ |
|------|--------|--------|
| 严格分层 Domain/Application/Infrastructure/Host | 端口在 ToolProvider 层（Application 边界），Adapter 实现在 Infrastructure/Host 层 | ✅ |
| 依赖反转 | 工具依赖端口接口，不依赖具体实现 | ✅ |
| 删除优先 | 不创建平行系统，扩展现有 ToolSource | ✅ |
| 核心语义强类型 | 所有端口参数/返回值为强类型 record，不用 `Dictionary<string, object>` | ✅ |
| 禁止中间层状态映射 | 工具无状态，rate limit 计数器随 agent 实例生命周期 | ✅ |
| 单一主干，插件扩展 | 新工具以 ToolSource 插件挂载，无平行系统 | ✅ |
| 事实源唯一 | Workflow 定义权威源 = `~/.aevatar/workflows/` 文件；Binding 权威源 = `IScopeBindingCommandPort` | ✅ |
| 变更必须可验证 | 每个端口有对应测试；工具有 integration test | ✅ |
| 写操作审批 | 所有写工具 `ApprovalMode = AlwaysRequire` | ✅ |
| 读写分离 | 查询走 QueryAdapter，写入走 CommandAdapter/CommandPort | ✅ |
