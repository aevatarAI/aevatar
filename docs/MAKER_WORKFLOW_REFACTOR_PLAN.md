# Maker-Workflow 彻底解耦重构方案（Best Practice）

## 1. 文档目的

本方案用于指导 `Maker` 与 `Workflow` 的彻底解耦重构，目标是建立稳定能力边界，消除 `Maker` 对 `Workflow` 具体实现（尤其 `WorkflowGAgent`）的直接依赖。

前提约束：

1. 不考虑兼容性。
2. 允许破坏性变更。
3. 以长期可演进（分布式 Runtime、多实现 Actor、统一能力组合）为第一优先级。

## 2. 当前问题（必须消除）

### 2.1 运行时实现耦合

`Maker.Infrastructure` 直接依赖并操作 `WorkflowGAgent`：

1. 直接创建 `CreateAsync<WorkflowGAgent>`。
2. 通过 `actor.Agent is WorkflowGAgent` 做类型判断/强转。
3. 直接调用 `workflow.ConfigureWorkflow(...)`。

这使 `Maker` 与 `Workflow` 的内部实现绑定，违反“能力依赖抽象”的架构原则。

### 2.2 编排重复与协议漂移风险

`Maker` 自己实现了一套 run 编排（projection ensure/attach/detach/release + envelope 构造 + timeout 收敛），与 `Workflow.Application` 内已有编排重复，后续协议演进时容易产生行为分叉。

### 2.3 依赖面过宽

`Maker.Infrastructure` 直接引用 `Workflow.Core/Projection/AGUIAdapter`，导致编译期耦合半径扩大，`Workflow` 内部调整会波及 `Maker`。

### 2.4 契约层未隔离

`Maker.Core` 模块依赖 `Workflow.Core` 的事件类型（如 `StepRequestEvent/StepCompletedEvent`），能力契约与能力实现混在一起，不利于独立演进。

## 3. 重构目标（终态）

### 3.1 架构目标

1. `Maker` 仅依赖 `Workflow` 能力抽象接口，不感知 Actor 具体类型。
2. `Workflow` 负责完整执行编排：Actor 解析/创建、workflow 绑定、命令封装、投影生命周期、事件输出。
3. 事件抽象独立为 `Workflow.Abstractions`，`Workflow.Core` 与 `Maker.Core` 均依赖抽象而非彼此实现。
4. Host 负责能力组合，能力内部不互相拉取实现层。

### 3.2 约束目标

1. `src/maker` 内不得出现 `WorkflowGAgent`。
2. `src/maker/*.csproj` 不得引用 `Aevatar.Workflow.Core`、`Aevatar.Workflow.Projection`、`Aevatar.Workflow.Presentation.AGUIAdapter`。
3. `Maker` 不直接依赖 `IActorRuntime` 执行 workflow run。
4. `Maker` 不直接依赖 `IWorkflowExecutionProjectionPort`。

## 4. 目标分层设计

### 4.1 新增/调整项目

1. 新增：`src/workflow/Aevatar.Workflow.Abstractions`
   - 承载 workflow 执行协议事件（protobuf + DTO）。
   - 对外提供稳定消息语义，不含编排/状态逻辑。
2. 保留并收口：`src/workflow/Aevatar.Workflow.Application.Abstractions`
   - 增加 `IWorkflowExecutionCapability`（统一执行入口）。
3. 调整：`src/workflow/Aevatar.Workflow.Application`
   - 实现 `IWorkflowExecutionCapability`，集中编排实现。
4. 调整：`src/maker/Aevatar.Maker.Infrastructure`
   - 删除具体 `WorkflowGAgent` 依赖，改为调用 `IWorkflowExecutionCapability`。
5. 调整：`src/maker/Aevatar.Maker.Core`
   - 由依赖 `Workflow.Core` 改为依赖 `Workflow.Abstractions`。

### 4.2 统一能力接口（建议）

```csharp
public sealed record WorkflowExecutionRequest(
    string Input,
    string? ActorId,
    string? WorkflowName,
    string? WorkflowYaml,
    TimeSpan? Timeout,
    bool DestroyActorAfterRun);

public sealed record WorkflowExecutionResult(
    string ActorId,
    string WorkflowName,
    string CommandId,
    bool Success,
    bool TimedOut,
    string Output,
    string? Error);

public interface IWorkflowExecutionCapability
{
    Task<WorkflowExecutionResult> ExecuteAsync(
        WorkflowExecutionRequest request,
        Func<WorkflowRunEvent, CancellationToken, ValueTask>? emitAsync = null,
        CancellationToken ct = default);
}
```

说明：

1. `actorId` 作为 opaque handle，不暴露 `WorkflowGAgent` 类型语义。
2. `emitAsync` 提供实时事件输出能力（SSE/WS/内部消费均可复用）。
3. `WorkflowExecutionResult` 仅暴露执行结果，不暴露内部投影实现细节。

## 5. 实施计划（一次性破坏式）

### Phase 1：抽象层抽离

1. 新建 `Aevatar.Workflow.Abstractions`，迁移以下事件抽象：
   - `ChatRequestEvent`
   - `StartWorkflowEvent`
   - `StepRequestEvent`
   - `StepCompletedEvent`
   - `WorkflowCompletedEvent`
2. `Workflow.Core` 改引用 `Workflow.Abstractions`。
3. `Maker.Core` 改引用 `Workflow.Abstractions`。
4. 删除 `Maker.Core -> Workflow.Core` 项目引用。

交付标准：

1. `rg "using Aevatar.Workflow.Core" src/maker/Aevatar.Maker.Core` 无命中。
2. `dotnet build` 通过。

### Phase 2：执行能力收口

1. 在 `Workflow.Application.Abstractions` 增加 `IWorkflowExecutionCapability` 与请求/结果模型。
2. 在 `Workflow.Application` 新增 `WorkflowExecutionCapability` 实现：
   - 复用 `IWorkflowRunActorPort` 解析/创建 Actor
   - 复用 `IWorkflowExecutionProjectionPort` 进行 projection 生命周期管理
   - 统一 envelope 构建与 command context 策略
   - 统一 timeout、错误映射、资源释放语义
3. 将 `WorkflowChatRunApplicationService` 的核心运行路径下沉为可复用内部编排服务，避免双实现。

交付标准：

1. Workflow 内仅存在一套 run 编排真源。
2. 能力 API 单测与集成测试通过。

### Phase 3：Maker 接入改造

1. 删除 `WorkflowMakerRunExecutionPort` 中对 `IActorRuntime`、`WorkflowGAgent`、`IWorkflowExecutionProjectionPort` 的直接使用。
2. 新实现 `MakerWorkflowExecutionPort`（命名可调整）：
   - 仅依赖 `IWorkflowExecutionCapability`
   - 只做 `MakerRunRequest <-> WorkflowExecutionRequest` 映射
3. `MakerEndpoints` 保持输入输出语义不变（或按新模型统一升级）。
4. 删除 `Maker.Infrastructure` 中对 Workflow 实现层装配调用：
   - 删除 `AddAevatarWorkflow()`
   - 删除 `AddWorkflowExecutionProjectionCQRS(...)`
   - 删除 `AddWorkflowExecutionAGUIAdapter()`
   - 删除 `AddWorkflowExecutionProjectionProjector<...>()`

交付标准：

1. `src/maker` 不再直接触达 Workflow 实现层。
2. Maker 只依赖 Workflow 抽象能力。

### Phase 4：Host 组合重构

1. 在 Host 层显式组合能力（而非 Maker 基础设施隐式拉起 Workflow 实现）：
   - `AddWorkflowCapability(...)`
   - `AddMakerCapability(...)`
2. 校准 `Aevatar.Mainnet.Host.Api` 与 `Aevatar.Maker.Host.Api` 的装配顺序与最小依赖面。

交付标准：

1. 能力组合关系清晰、可观测、可替换。
2. `Maker` 项目不再承担跨能力装配职责。

### Phase 5：治理与清理

1. 删除废弃接口/适配器/文档。
2. 新增架构守卫（见第 7 节）。
3. 更新 `README.md`、`docs/PROJECT_ARCHITECTURE.md`、`docs/CQRS_ARCHITECTURE.md`、`docs/audit-scorecard/*`。

## 6. 删除清单（重构后应不存在）

1. `Maker` 侧任何 `WorkflowGAgent` 强转与类型判断。
2. `Maker` 侧任何 `CreateAsync<WorkflowGAgent>` 调用。
3. `Maker` 侧对 `IWorkflowExecutionProjectionPort` 的直接依赖。
4. `Maker.Infrastructure` 内 Workflow 具体实现层装配逻辑。
5. `Maker.Core -> Workflow.Core` 直接项目引用。

## 7. CI 架构门禁新增项

在 `tools/ci/architecture_guards.sh` 增加：

1. 禁止 `src/maker` 出现 `WorkflowGAgent` 字面引用。
2. 禁止 `src/maker/*.csproj` 引用：
   - `Aevatar.Workflow.Core`
   - `Aevatar.Workflow.Projection`
   - `Aevatar.Workflow.Presentation.AGUIAdapter`
3. 禁止 `src/maker` 直接引用 `IWorkflowExecutionProjectionPort`。
4. 禁止 `src/maker` 直接引用 `IActorRuntime`（执行路径内）。

## 8. 测试策略

### 8.1 新增测试项目

1. `test/Aevatar.Maker.Application.Tests`
2. `test/Aevatar.Maker.Infrastructure.Tests`

### 8.2 关键用例

1. 无 `actorId` 创建并执行成功。
2. 指定 `actorId` 复用执行成功。
3. timeout 行为与资源释放一致。
4. `DestroyActorAfterRun=true` 时销毁行为正确。
5. workflow 绑定冲突时错误语义稳定。
6. projection attach/detach/release 在异常路径下不泄漏。

## 9. 风险与控制

1. 风险：重构跨度大，容易中间态编译失败。  
   控制：按 Phase 严格分步，分阶段保持 `build/test` 绿色。
2. 风险：能力收口后行为变化引发回归。  
   控制：先补行为测试，再替换实现。
3. 风险：抽象迁移影响 `Workflow.Core` 模块事件路由。  
   控制：先做抽象迁移 + 回归，再做执行面重构。

## 10. 验收标准（DoD）

1. `rg "WorkflowGAgent" src/maker` 无结果。
2. `rg "Aevatar.Workflow.Core|Aevatar.Workflow.Projection|Aevatar.Workflow.Presentation.AGUIAdapter" src/maker -g '*.csproj'` 无结果。
3. `bash tools/ci/architecture_guards.sh` 通过。
4. `dotnet build aevatar.slnx --nologo` 通过。
5. `dotnet test aevatar.slnx --nologo` 通过。
6. 文档与评分卡完成更新并可追溯到代码证据。

## 11. 执行顺序建议

1. 先做 Phase 1（抽象层抽离），最先缩小耦合半径。
2. 再做 Phase 2（Workflow 能力收口），建立单一编排真源。
3. 最后做 Phase 3/4（Maker 接入与 Host 组合），完成能力边界闭环。
