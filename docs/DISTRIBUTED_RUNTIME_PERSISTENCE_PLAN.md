# Distributed Runtime & Persistence Plan

## 1. 目标

本文档定义 Aevatar 从当前默认 `InMemory` 运行口径迁移到生产分布式口径的目标与步骤：

1. 分布式 Actor Runtime（同一 `actorId` 全局单激活）。
2. 非 InMemory 持久化（state/event/manifest/read model）。
3. 投影编排 Actor 化（每个 `rootActorId` 固定投影协调 Actor）。

## 2. 当前与目标对比

| 主题 | 当前实现（2026-02-20） | 目标态（生产） |
|---|---|---|
| Actor Runtime Provider | 默认 `InMemory` | 非 `InMemory` provider（Redis/数据库等） |
| Actor 并发语义 | 单进程内邮箱串行 | 跨节点仍保持“同一 actorId 串行” |
| Projection 启动并发（Ensure/Release） | 已由 `projection:{rootActorId}` 协调 Actor 串行裁决 | 分布式 Runtime 下保持同一 actorId 单激活 + 邮箱串行 |
| LiveSink 绑定（Attach/Detach） | lease 句柄本地绑定输出通道 | 多节点场景需配套会话通道或粘性路由 |
| ReadModel Store | Workflow 默认 InMemory store | 生产默认持久化 read model store |
| 生产部署建议 | 单实例/开发验证 | 多实例 + 持久化 + 恢复能力 |

## 3. 核心原则

1. 并发互斥优先依赖 Actor 语义，不额外引入中心化锁服务作为前置条件。
2. 投影会话状态不在中间层服务保存事实态，统一由投影协调 Actor 承载。
3. InMemory 实现继续保留在 dev/test profile，不删除。
4. 生产评分只依据“已落地实现”，不按规划提前加分。

## 4. 投影协调 Actor 设计

### 4.1 标识规则

1. 每个工作流根 Actor 映射唯一投影协调 Actor：`projection:{rootActorId}`。
2. 同一 `rootActorId` 的 `Ensure/Release` 请求都路由到该 Actor；`Attach/Detach` 保持 lease 会话句柄绑定语义。

### 4.2 职责边界

1. 维护投影会话生命周期仲裁（启动、释放）。
2. 保障同一 `rootActorId` 的投影 ownership 串行互斥。
3. 输出与校验 ownership 上下文（`rootActorId + commandId`）。
4. 在 release 时清理 ownership 运行态。

### 4.3 中间层约束

1. `WorkflowExecutionProjectionService` 只做 facade，不再持有进程内并发事实门禁。
2. 禁止新增 `actor/run/session` 维度的服务级内存事实态映射字段。

## 5. 持久化目标矩阵

| 能力 | dev/test | production |
|---|---|---|
| `IStateStore<TState>` | InMemory 可用 | 非 InMemory 必选 |
| `IEventStore` | InMemory 可用 | 非 InMemory 必选 |
| `IAgentManifestStore` | InMemory 可用 | 非 InMemory 必选 |
| `IProjectionReadModelStore` | InMemory 可用 | 非 InMemory 必选 |

## 6. 迁移步骤（建议顺序）

1. 引入非 InMemory Runtime provider 与持久化 store 实现，并通过 `ActorRuntime:Provider` 切换。
2. （已完成）实现投影协调 Actor，迁移 `Ensure/Release` 并发裁决到 Actor。
3. 将 Workflow 投影默认读模型存储切换为持久化实现（InMemory 下沉为开发配置）。
4. 设计并落地多节点 `Attach/Detach` 实时输出一致性策略（会话通道或粘性路由）。
5. 补充门禁与测试：多实例一致性、重启恢复、重复订阅与会话释放。

## 7. 架构审计评分口径

1. 当前分：按当前代码快照评分（见 `docs/audit-scorecard/architecture-scorecard-2026-02-20.md`）。
2. 已完成项：投影启动并发（`Ensure/Release`）从进程内门禁迁移至投影协调 Actor。
3. 目标态加分条件：
   - 生产默认使用非 InMemory 读写存储；
   - 多节点实时输出一致性策略落地；
   - 多节点一致性测试纳入 CI 或发布门禁。
4. 任何“仅文档声明、未落地实现”的能力不计入评分加分。
