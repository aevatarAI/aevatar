# Aevatar CQRS 架构文档

## 1. 文档目标

本文档定义 Aevatar 当前 CQRS 的实现基线，覆盖：

1. 写侧（Command）与读侧（Query / ReadModel）边界。
2. 框架层项目职责与依赖关系。
3. 命令执行链路、投影链路、状态持久化链路。
4. 命令执行策略（Application 直达 Actor）。
5. CQRS 与编排边界关系（编排不承担读模型职责）。
6. 按能力打包的系统接入规范（Mainnet 默认内置 Workflow，Maker 为独立能力提供系统）。

## 2. 顶层原则

1. `Command -> Event`，`Query -> ReadModel`。
2. Host 只做协议适配与依赖组合，不做业务编排。
3. 抽象层不携带具体能力语义（不依赖 workflow/maker 等实现项目）。
4. `CorrelationId` 用于链路关联；`metadata` 仅用于透传与诊断。
5. Projection 负责状态追踪与对外查询，不引入额外状态机编排层。

## 3. 项目分层与职责

| 层 | 项目 | 职责 |
|---|---|---|
| Core Abstractions | `Aevatar.CQRS.Core.Abstractions` | 命令执行抽象、输出流抽象 |
| Core | `Aevatar.CQRS.Core` | `ICommandContextPolicy` 默认实现、事件输出流默认实现 |
| Projection Abstractions | `Aevatar.CQRS.Projection.Abstractions` | 投影生命周期、分发、订阅、读模型契约 |
| Projection Core | `Aevatar.CQRS.Projection.Core` | 通用投影协调与订阅复用实现 |
| Foundation Projection | `Aevatar.Foundation.Projection` | 读模型最小基类、通用读侧能力接口（Timeline/RoleReplies） |
| AI Projection | `Aevatar.AI.Projection` | AI 通用事件 reducer（TextMessage/Tool）与事件 applier 模式 |

## 4. 总体架构图

```mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
flowchart LR
  H["Host API"] --> CC["Aevatar.CQRS.Core"]
  H --> PA["Aevatar.CQRS.Projection.Abstractions"]
  H --> PC["Aevatar.CQRS.Projection.Core"]
```

## 5. 命令模型（写侧）

核心对象：

1. `CommandContext`：`TargetId/CommandId/CorrelationId/Metadata`。
2. `EventEnvelope`：命令触发后进入统一事件流的封装对象。

## 6. 命令执行路径

### 6.1 直接执行路径（Actor 直达）

用于需要即时启动并持续流式输出的场景（如 Mainnet 内置 Workflow 能力、Maker 能力实时执行）：

```mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
sequenceDiagram
  participant API as "Host Endpoint"
  participant APP as "Application Service"
  participant CP as "ICommandContextPolicy"
  participant EF as "ICommandEnvelopeFactory"
  participant ACT as "Actor"

  API->>APP: "Execute(command)"
  APP->>CP: "Create(target)"
  APP->>EF: "CreateEnvelope(command, context)"
  APP->>ACT: "HandleEventAsync(envelope)"
```

特征：低延迟、可实时流输出；命令受理与执行一致，不引入额外总线层。

## 7. 投影架构（读侧）

Projection 内核由 `Aevatar.CQRS.Projection.Core` 提供，职责拆分：

1. `ProjectionLifecycleService`：`start/project/complete` 生命周期编排。
2. `ProjectionSubscriptionRegistry`：按 `actorId` 注册/注销投影上下文。
3. `ActorStreamSubscriptionHub`：同一 actor 底层订阅复用，逻辑处理器多播。
4. `ProjectionDispatcher`：统一事件分发入口。
5. `ProjectionCoordinator`：按服务注册顺序调度多个 projector。
6. `IProjectionEventApplier<,,>`：事件解析后对 read model 的细粒度 apply 扩展点。

```mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
flowchart LR
  EVT["EventEnvelope Stream"] --> HUB["ActorStreamSubscriptionHub"]
  HUB --> REG["ProjectionSubscriptionRegistry"]
  REG --> DIS["ProjectionDispatcher"]
  DIS --> COOR["ProjectionCoordinator"]
  COOR --> PJ1["Projector A"]
  COOR --> PJ2["Projector B"]
  PJ1 --> RM["ReadModel Store"]
  PJ2 --> OUT["Live Output Sink"]
```

说明：CQRS 与 AGUI 输出统一走同一事件输入与投影管线，只是 projector 分支不同。  
当前推荐模式：`Reducer` 负责 `TypeUrl` 精确匹配与反序列化，`Applier` 负责 read model 字段变换。
变更语义约束：`Reducer/Applier` 返回 `mutated`，仅在 read model 实际变更时推进 `StateVersion/LastEventId`。
并发隔离约束：live sink 仅按 `EventEnvelope.CorrelationId` 精确匹配，不允许空 correlation 回退到广播。

订阅判定语义（关键）：

1. `ReadModel` 的字段与能力接口仅表示“可写入能力”，不构成事件订阅声明。
2. 事件订阅入口由 reducer 决定：`IProjectionEventReducer<,>.EventTypeUrl` + DI 注册集合。
3. `Projector` 运行时按 `payload.TypeUrl` 命中 reducer；未命中的事件对该 ReadModel 为 no-op。
4. applier 仅是 reducer 内部的字段映射扩展点，不单独参与事件路由。
5. 事件与 ReadModel 关系是多对多：同一事件可被多个 ReadModel 订阅，一个 ReadModel 可订阅多个事件。

## 8. 编排与 CQRS 的关系

当前原则：

1. CQRS 主链路独立于额外编排层，可完整运行。
2. 跨 Actor 协作优先使用 EventEnvelope 事件链路，不引入独立状态机层。
3. 业务状态查询统一由 Projection/ReadModel 提供。

## 9. 系统接入规范（按能力打包）

每个 Capability Host 统一接入：

1. `builder.AddAevatarDefaultHost(...);`
2. `app.UseAevatarDefaultHost();`
3. 默认 Host 内部统一完成 `AddAevatarActorRuntime(...)`。

当前接入：

1. `src/Aevatar.Mainnet.Host.Api/Program.cs`
2. `src/workflow/Aevatar.Workflow.Host.Api/Program.cs`
3. `src/maker/Aevatar.Maker.Host.Api/Program.cs`

约束：

1. `Mainnet Host` 默认打包 `Workflow Capability`（`AddWorkflowCapability(...)`）。
2. `Workflow Host` 可作为同一能力的独立部署入口（开发/测试/隔离发布）。
3. `Maker Host` 通过引用 Maker 项目并调用 `AddMakerCapability(...)` 接入。
4. `Maker` 作为 `Workflow` 扩展能力，允许直接继承并引用 `Workflow` 能力实现项目（含 `Core/Application/Projection/Presentation/Infrastructure`）。
5. 禁止 `Workflow` 反向依赖 `Maker`；禁止 `Maker` 依赖 `Workflow.Host.Api`（宿主入口禁止跨能力复用）。
6. `AddAevatarCapability(...)` 对同名能力注册幂等，映射冲突时 fail-fast。
7. 不允许新增或回流 `Aevatar.Platform.*` 项目引用。

## 10. 配置基线（`Cqrs:*`）

当前无 `Cqrs:*` 强制配置项。

## 11. 扩展点与反模式

推荐扩展点：

1. 新命令类型：新增 `ICommandExecutionService<TCommand,...>` 并在 Application 层接入。
2. 新读模型：新增 projector/reducer + `IProjectionReadModelStore` 实现。
3. 事件字段映射扩展：新增 `IProjectionEventApplier<TReadModel, TContext, TEvent>` 实现。
4. 新能力接入：通过 `AddAevatarDefaultHost` + capability 扩展注册，不新增中间运行时壳层。

禁止反模式：

1. Host/API 承担业务编排或状态机职责。
2. 命令路径读取 ReadModel 决策业务写入。
3. 业务字段滥用 `metadata`。

## 12. 验证与门禁

最低验证：

1. `dotnet build aevatar.slnx --nologo`
2. `dotnet test aevatar.slnx --nologo`

CI 架构门禁（摘要）：

1. 禁止同步阻塞写法（如 `GetAwaiter().GetResult()`）。
2. 禁止字符串匹配事件类型路由（`TypeUrl.Contains`）。
3. 禁止 Host/Infrastructure 直接拼装 `AddCqrsCore(...)`。
4. 禁止 `docs/agents-working-space` 工作文档进入 `aevatar.slnx`。
5. 禁止 `Workflow` 反向依赖 `Maker`。
6. 允许 `Maker` 直接依赖 `Workflow` 能力实现层；仅禁止依赖 `Workflow.Host.Api`。

## 13. 结论

Aevatar 当前 CQRS 架构已形成：

1. 抽象稳定（Core / Projection 分层清晰）。
2. 写侧执行链路收敛到 Application 直达 Actor。
3. 读写职责清晰（写侧命令执行、读侧投影查询）。
4. 编排通过事件链路完成，不新增额外运行时负担。
