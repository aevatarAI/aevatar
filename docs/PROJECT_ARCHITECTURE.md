# Aevatar 完整项目架构文档（轻量能力装配版）

## 1. 目标与范围

本文档定义 Aevatar 的目标架构基线，覆盖：

1. 分层结构（Domain / Application / Infrastructure / Host）。
2. 能力模型（`Workflow`、`Maker` 均为 Capability）。
3. 轻量装配机制（引用能力项目 + `Add...` 注册）。
4. 默认装配策略（`Mainnet` 默认装配 `Workflow`）。
5. Maker 独立系统（引用 Maker 并 `Add...` 注册）。
6. 平台旧层清理（删除 `Aevatar.Platform.*`）。

## 2. 解决方案结构

```mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
flowchart TB
    R["aevatar.slnx"] --> SRC["src/"]
    SRC --> MN["src/Aevatar.Mainnet.*"]
    SRC --> HT["src/Aevatar.Hosting"]
    SRC --> FRH["src/Aevatar.Foundation.Runtime.Hosting"]
    SRC --> WF["src/workflow/*"]
    SRC --> MK["src/maker/*"]
    SRC --> CQ["src/Aevatar.CQRS.*"]
    SRC --> FD["src/Aevatar.Foundation.*"]
```

## 3. 系统装配模型（轻量）

```mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
flowchart LR
    MH["Aevatar.Mainnet.Host.Api"] --> HADD["AddAevatarDefaultHost()"]
    MH --> WADD["AddWorkflowCapability()"]

    KH["Aevatar.Maker.Host.Api"] --> KHADD["AddAevatarDefaultHost()"]
    KH --> KADD["AddMakerCapability()"]

    WADD --> WIMPL["Workflow Capability Implementation"]
    KADD --> KIMPL["Maker Capability Implementation"]
```

### 3.1 核心约束

1. `Mainnet` 默认通过项目引用 `Workflow` 能力并调用 `AddWorkflowCapability()`。
2. `Maker` 作为独立系统，通过项目引用 `Maker` 并调用 `AddMakerCapability()`。
3. 能力接入不引入运行时发现/注册中心/动态路由框架。
4. 能力开关优先使用配置 + DI 注册控制。
5. `Aevatar.Bootstrap` 仅保留通用装配，不承载 `Workflow` 具体能力注册。
6. 删除 `Aevatar.Platform.*`，不保留兼容壳层。

### 3.2 最小能力契约

1. 每个能力提供 Host 入口扩展（`WebApplicationBuilder` 扩展），一行接入能力。
2. 能力内部可保留 `IServiceCollection` 与 `IEndpointRouteBuilder` 细粒度扩展，供非 Host 场景复用。
3. 能力 API 契约（请求/响应模型 + endpoint 定义）归属能力项目，不在 Host 重复定义。
4. 能力通过 `Aevatar.Hosting` 的 `AddAevatarCapability(...)` 声明端点映射，默认由 `UseAevatarDefaultHost()` 统一挂载。
5. 能力之间通过现有事件与应用服务协作，不要求新增通用微服务基础设施。
6. `AddAevatarCapability(...)` 对同名能力注册幂等；若同名能力使用不同端点映射器则启动前失败（fail-fast）。
7. `MapAevatarCapabilities()` 对重复能力名映射执行冲突检查，禁止重复挂载。

### 3.3 能力 API 契约归属（当前实现）

1. Workflow 能力 API 输入契约定义在 `Aevatar.Workflow.Application.Abstractions`（如 `ChatInput`、`ChatWsCommand`）。
2. Maker 能力 API 输入契约定义在 `Aevatar.Maker.Application.Abstractions`（如 `MakerRunInput`）。
3. Infrastructure Endpoint 仅负责协议绑定与参数校验，不重复定义能力输入模型。

### 3.4 Maker 扩展 Workflow（语义约束）

1. `Maker` 定义为 `Workflow` 的扩展能力，允许受控的直接项目引用。
2. 允许：`src/maker/* -> src/workflow/Aevatar.Workflow.Core`（必要时可含 `Application.Abstractions`）。
3. 禁止：`src/workflow/* -> src/maker/*`（禁止反向依赖）。
4. 禁止：`src/maker/* -> src/workflow/Aevatar.Workflow.Infrastructure|Host.Api|Presentation.*`（禁止跨到 Workflow 实现层与宿主层）。
5. 如未来需要独立发布，再抽 `Workflow.Contracts`，但当前不作为强制前置条件。

## 4. CQRS Runtime 统一接入

```mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
flowchart TB
    Host["Host"] --> RH["Aevatar.CQRS.Runtime.Hosting"]
    RH --> RA["Aevatar.CQRS.Runtime.Abstractions"]
    RH --> RF["Aevatar.CQRS.Runtime.FileSystem"]
    RH --> RW["Aevatar.CQRS.Runtime.Implementations.Wolverine"]
    RH --> RM["Aevatar.CQRS.Runtime.Implementations.MassTransit"]
```

统一规则：

1. Host 仅通过 `AddAevatarDefaultHost(...)` + `UseAevatarDefaultHost()` 接入默认运行时。
2. 业务能力项目不得直接引用 `Runtime.Implementations.*`。
3. 运行时切换仅通过 `Cqrs:Runtime = Wolverine|MassTransit`。

## 4.1 Actor Runtime 统一接入

1. 默认 Host 通过 `Aevatar.Bootstrap` 统一调用 `AddAevatarActorRuntime(...)`。
2. Actor Runtime 提供者通过配置键 `ActorRuntime:Provider` 选择，当前默认 `InMemory`。
3. Mainnet/Subsystem Host 可通过 `EnableActorRestoreOnStartup` 控制启动恢复行为。
4. `ActorRuntime:RestoreOnStartup` 可通过配置直接控制默认恢复开关。

## 5. 命令与查询主链路

```mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
flowchart LR
    C["Command API"] --> APP["Application Service"] --> BUS["ICommandBus"] --> ACT["GAgent"]
    ACT --> EVT["EventEnvelope Stream"] --> PROJ["Projection Pipeline"] --> RM["ReadModel"] --> Q["Query API"]
```

关键约束：

1. `Command -> Event`，`Query -> ReadModel`。
2. AGUI/SSE/WS 只从统一投影链路输出。
3. 不在 API 会话内拼装跨能力长链路流程。

## 6. API 所有权（目标态）

| 路径 | 所有者 | 说明 |
|---|---|---|
| `/api/chat`, `/api/ws/chat` | Mainnet Host | Workflow 运行入口（SSE/WS） |
| `/api/agents`, `/api/workflows` | Mainnet Host | Workflow 查询入口 |
| `/api/actors/{actorId}`, `/api/actors/{actorId}/timeline` | Mainnet Host | Actor 执行快照与时间线查询 |
| `/api/maker/*` | Maker Host | Maker 能力入口 |

收敛要求：

1. 删除 `/api/routes/{subsystem}/*` 目录路由模型。
2. 不再保留 `Platform Host` API 所有权。

## 7. 依赖与命名规范

1. 项目名、命名空间、目录语义一致。
2. 缩写全大写：`LLM`、`CQRS`、`AGUI`。
3. 能力命名使用 `Capability` 语义，避免重复层次包装。
4. 删除 `Aevatar.Platform.*` 相关代码与引用。

## 8. CI 架构门禁

CI（`.github/workflows/ci.yml`）应执行：

1. `build + test`。
2. 禁止 `GetAwaiter().GetResult()`。
3. 禁止 `TypeUrl.Contains(...)` 字符串路由。
4. 禁止 `Aevatar.Workflow.Core` 依赖 `Aevatar.AI.Core`。
5. 禁止任何项目新增 `Aevatar.Platform.*` 引用。
6. 强制 Mainnet Host 与 Maker Host 使用统一 CQRS Runtime 接入扩展。
7. 禁止 Host/Infrastructure 直接 `AddCqrsCore(...)`。
8. 禁止 `docs/agents-working-space` 下工作文档被加入 `aevatar.slnx`。
9. 允许 Maker 对 Workflow 的受控直连（扩展语义），并禁止 Workflow 反向依赖 Maker。
10. 禁止 Maker 直接依赖 Workflow 的实现层与宿主层（`Infrastructure/Host.Api/Presentation.*`）。

## 9. 演进路线

1. Mainnet：`AddAevatarDefaultHost()` 后引用 Workflow 并完成 `AddWorkflowCapability()` 装配。
2. Maker：`AddAevatarDefaultHost()` 后独立部署并完成 `AddMakerCapability()` 装配。
3. 删除 `src/Aevatar.Platform.*` 与旧平台路由目录。
4. 新能力统一按“新增项目引用 + 新增 Add 扩展 + Host 注册”接入。
