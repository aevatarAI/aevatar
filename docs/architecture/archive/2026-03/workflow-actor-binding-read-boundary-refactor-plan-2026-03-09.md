# Workflow Actor Binding 读取边界彻底重构计划（2026-03-09）

## 1. 文档元信息

- 状态：Implemented
- 版本：R1
- 日期：2026-03-09
- 关联文档：
  - `docs/architecture/workflow-run-actorized-target-architecture-2026-03-08.md`
  - `docs/architecture/workflow-run-actorized-state-boundary-blueprint-2026-03-08.md`
  - `docs/architecture/workflow-foundation-workflow-engine-boundary-refactor-plan-2026-03-08.md`
- 适用范围：
  - `src/workflow/Aevatar.Workflow.Application`
  - `src/workflow/Aevatar.Workflow.Infrastructure`
  - `src/workflow/Aevatar.Workflow.Projection`
  - `src/Aevatar.Foundation.*`
- 文档定位：
  - 本文记录 actor binding 读取边界重构的最终落地方案
  - workflow 主链路已经移除 `IActorStateProbe / ActorStateSnapshot` 一类 generic raw-state 依赖
  - 本文默认“不保留兼容层”，以读写边界清晰为第一目标

补充口径：

- workflow command path 运行在 stream-backed actor message runtime 上。
- binding 读取修复的是 runtime message path 的边界问题，不改变 Event Sourcing 事实层的语义。

## 2. 问题定义

本轮重构已经修复 “Orleans 下 existing actor inspection 失效” 与 “默认 workflow-name 启动隐式创建 definition actor” 两个生产问题，并进一步消除了当时暴露出来的长期架构风险：

1. binding 读取统一收敛为 workflow 专用 `IWorkflowActorBindingReader`。
2. command path 读取当前 binding 时统一走 actor-owned workflow query/reply。
3. `Foundation` 不再向 workflow 主链路暴露 generic actor raw-state 读取能力。
4. `Application / Infrastructure` 不再依赖 write model 原始 state payload。

这会带来三个直接问题：

- CQRS 边界被削弱：业务读取可能绕过 projection/read model。
- Actor 边界被削弱：外层代码开始依赖 actor 内部持久态形状。
- 抽象容易被滥用：一个 workflow 修复路径，演变成全局 generic raw-state read channel。

## 3. 最终决议

### 3.1 必须坚持的原则

1. `actorId` 只表达稳定身份，不表达可变 binding 事实。
2. workflow binding 必须通过 workflow 专用契约读取，不能通过 Foundation 通用 raw-state 接口猜测。
3. query/read 场景优先走 projection/read model。
4. command path 若需要“当前权威 binding”，必须走 actor 自身的 workflow 专用 query/reply 协议，而不是通用状态导出。
5. `Foundation` 可以暴露 runtime liveness/type/activation 能力，但不应向业务层暴露 generic state payload 读取能力。

### 3.2 generic raw-state probe 的处理结果

最终实现中，这类 probe 已从 workflow 主链路和 Foundation 注册面移除，不再保留“临时可用”的灰色地带。

## 4. 目标架构

### 4.1 新增窄接口：`IWorkflowActorBindingReader`

Application 层只依赖 workflow 专用 binding 读取抽象，例如：

```csharp
public interface IWorkflowActorBindingReader
{
    Task<WorkflowActorBindingSnapshot?> GetAsync(
        string actorId,
        CancellationToken ct = default);
}
```

返回值只包含 workflow command path 真正需要的最小字段：

- `ActorId`
- `ActorKind`
- `WorkflowName`
- `DefinitionActorId`
- `RunId`
- `WorkflowDefinitionRevision` 或等价版本信息
- 必要时的 inline definition metadata

禁止返回：

- 任意 protobuf state payload
- 泛化 state type name
- 与 workflow 无关的 actor 内部状态

### 4.2 Command Path：actor-owned binding query

对 `/api/chat` 这类 command path，若输入包含 `actorId`，系统必须向目标 actor 发起 workflow 专用 query：

- `QueryWorkflowActorBindingRequestedEvent`
- `WorkflowActorBindingRespondedEvent`

特征：

- query 由 workflow actor 自己返回
- 返回值只暴露 binding 事实，不暴露原始 state
- Orleans / Local 共用同一套协议，不依赖本地对象实例形状
- command path 读取到的是 actor 当前权威状态，而不是 eventually consistent read model

### 4.3 Query Path：binding index read model

对 list/query/inspection 场景，系统应提供 projection 驱动的 read model，例如：

- `WorkflowActorBindingIndexReadModel`

按 `actorId` 建索引，存储：

- actor kind
- workflow name
- definition actor id
- run id
- revision/version
- last updated at

用途：

- query API
- 后台诊断
- UI / AGUI 展示
- 非强一致 inspection

## 5. Source Actor 解析规则

### 5.1 Named workflow

- `workflow` 仅按 `IWorkflowDefinitionCatalog` 解析。
- registry-backed workflow 必须映射到单一规范 definition actor id：`workflow-definition:{workflow_name_lower}`。
- 默认按 workflow name 启动时，必须复用该 canonical definition actor。

### 5.2 Source actor provided

- 若传入的是 definition actor，则直接读取其 binding。
- 若传入的是 run actor，则读取其 `DefinitionActorId` 与 workflow binding，再创建新的 run actor。
- 若历史 run actor 缺失 `DefinitionActorId`，只允许通过 registry 的 canonical definition id 规则恢复。
- 不允许因为 binding 缺失而静默创建新的匿名 definition actor。

### 5.3 Explicit definition actor id

- 不存在：按该 id 创建。
- 已存在且是 workflow definition actor：复用或原地重绑。
- 已存在但类型不对：立即失败。

## 6. 迁移步骤

1. 新增 workflow 专用 binding query 契约与响应事件。
2. 为 `WorkflowGAgent` / `WorkflowRunGAgent` 增加 binding query handler。
3. 新增 `IWorkflowActorBindingReader` abstraction 与 infrastructure 实现。
4. 将 `WorkflowRunActorResolver` 和 capability endpoints 切到新 reader/query。
5. 增加 `WorkflowActorBindingIndexReadModel`，承接查询与诊断场景。
6. 从 workflow 主链路和 Foundation runtime 注册面移除 generic raw-state probe。
7. 补门禁与测试，禁止 workflow command path 再依赖 generic raw-state read。

## 7. 验收标准

1. `WorkflowRunActorResolver` 不再依赖 `IActorStateProbe`。
2. `WorkflowRunActorPort` 不再承担 binding inspection 读取职责。
3. Orleans 与 Local 在 existing `actorId` 路径上共享同一套 workflow query contract。
4. query/read path 具备专门的 binding read model，不再借道 write-side 原始状态。
5. `Foundation` 不再向 workflow 主链路暴露 generic actor raw-state 读取契约。
6. `dotnet build aevatar.slnx --nologo`、`dotnet test aevatar.slnx --nologo`、`bash tools/ci/architecture_guards.sh` 通过。

## 8. 非目标

- 不回退 definition/run split。
- 不把 workflow binding 全部塞进 actor id。
- 不把所有 command path 都强行改成 projection eventual read。
- 不在本轮引入第二套 workflow execution engine。
