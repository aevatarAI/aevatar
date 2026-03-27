# 2026-03-27 Projection Observation Relay Fix Review

## 背景

在一次真实三节点 smoke 联调里，workflow 可以正常执行完成，但 `GET /api/actors/{actorId}` 长时间返回 `404`。

最终确认的现象是：

- write side 已经成功提交 `CommittedStateEventPublished`
- workflow actor 本身已经推进到 completed
- `WorkflowExecutionCurrentStateProjector` 没有稳定拿到 committed observation
- current-state readmodel 没有物化出来
- `/api/actors/{actorId}` 因为读不到 `WorkflowExecutionCurrentStateDocument` 而返回 `404`

这次修复的核心目标不是“让 workflow 能跑”，而是“让 committed observation 稳定进入 projection scope actor，再由统一 projection pipeline 物化 readmodel”。

## 最终方案

本次保留的实现是：

1. `ProjectionScopeGAgentBase` 不再依赖宿主侧的 `ProjectionObservationSubscriber` 回调转发。
2. projection scope 在激活/ensure 时，对 `root actor stream -> projection scope actor stream` 建立一条 relay binding。
3. relay 只转发 `CommittedStateEventPublished`。
4. projection scope actor 在自己的 inbox 内处理这类 forwarded observer publication。
5. local runtime 与 Orleans runtime 都允许“明确转发给自己”的 observer publication 入 actor inbox。

对应代码：

- `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeGAgentBase.cs`
- `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrain.cs`
- `src/Aevatar.Foundation.Runtime.Implementations.Local/Actors/LocalActor.cs`

## 为什么这次选 relay

这次选择 relay，不是因为它“新”，而是因为它更符合仓库已经确立的架构主张：

- projection 运行态应 actor 化，不应依赖宿主进程内临时订阅器持有事实链路
- committed fact 应沿统一 observation 主链传播
- runtime 语义应 local / Orleans 一致
- fan-out / forwarding 应优先复用现有 stream relay 机制，而不是再开一条 projection 专用旁路

换句话说，这次修的不是单个 bug，而是把 projection observation 收敛回 runtime 已有的统一能力模型。

## 评分

如果把“最优”理解成“当前仓库约束下的综合最优权衡”，我对当前保留实现的评分是 `9 / 10`。

评分拆解如下：

- 架构一致性：`9 / 10`
  方案回到了 actor inbox + relay binding 这条主干，没有继续扩散宿主侧临时订阅逻辑。

- 正确性：`9 / 10`
  已经通过跨 silo runtime 集成测试、projection core 测试与 hosting 回归测试验证。

- 变更面控制：`8.5 / 10`
  最终保留的改动面不大，但仍然涉及 projection core、Orleans runtime、local runtime 与 smoke 脚本清理。

- 可维护性：`8 / 10`
  逻辑比“宿主侧 callback 订阅”更统一，但 relay 语义对第一次阅读代码的人来说并不直观，需要文档补足。

- 终态纯度：`8.5 / 10`
  这次已经明显优于宿主侧订阅；当前代码里已经不再保留为了 smoke 临时引入的 host 侧 dev-only scope identity 入口。

## 为什么不是 10 分

它不是理论上的满分终态，主要有三点：

1. projection scope 仍然需要在激活时自己建立 relay binding

这比宿主侧订阅更好，但仍然意味着“观察链路的接通”发生在 scope activation 阶段，而不是由更高层、更显式的 runtime provisioning 统一声明。

2. runtime 仍然需要对 forwarded observer publication 做专门放行

这说明 observer publication 在 runtime 中仍有一点“特殊分支”语义。虽然现在分支已经收敛且清晰，但还没有完全抽象成更自然的统一投递模型。

3. distributed smoke 里的 workflow 执行回归暂时没有以正式认证链路补回

这次已经把依赖匿名 scope 入口的 smoke 验证段落撤掉，避免把测试便利层继续混进主实现；但如果后续要恢复这条回归，最好通过正式认证或专用测试宿主补回。

## 备选方案对比

### 方案 A：继续修宿主侧 `ProjectionObservationSubscriber`

不推荐。

原因：

- 它会继续把 projection observation 维持成宿主侧旁路
- 和“projection 运行态 actor 化”的原则冲突
- Orleans / local 一致性更难保证
- 后续更容易再出现“本地可用，分布式不稳”的问题

### 方案 B：让 projection scope 直接订阅底层 stream provider，但不走 relay

不推荐。

原因：

- 还是会留下 projection 自己持有订阅句柄的旁路模型
- 与现有 stream forwarding / relay binding 机制重复
- 不如直接复用 runtime 已经用于拓扑 fan-out 的统一能力

### 方案 C：把观察绑定进一步前移到显式 activation / provisioning 层

这是我认为更接近长期终态的方向。

如果后续继续收敛，可以考虑：

- 由 projection scope activation service 显式声明 observation binding
- scope actor 只处理已经投递到自己 inbox 的 observation
- relay 建立与释放由更统一的 lease / activation 生命周期管理

这个方向理论上能再加 `0.5 ~ 1.0` 分，但它的改动面会更大，不适合作为这次 smoke 回归修复的第一刀。

## 为什么 local runtime 也一起修

虽然这次是 distributed Orleans smoke 先把问题打出来，但旧的 local runtime 在同样的 relay 语义下也有同一个缺口：

- 旧逻辑会直接丢掉 observer publication
- 当 projection scope 改为依赖 relay 后，本地 runtime 也需要允许“明确转发给自己”的 observer publication 入 inbox

所以 local runtime 的改动不是顺手补齐，而是为了保证 runtime-neutral 语义一致。

## 这次保留的必要改动

- `ProjectionScopeGAgentBase` 的 relay 化观察链路
- `RuntimeActorGrain` 对 forwarded observer publication 的放行
- `LocalActor` 的同语义放行
- Neo4j query auth 预检
- runtime / projection / host 回归测试

## 这次明确撤回的非必要改动

- `OrleansStreamProviderAdapter` 的探索性修改
- `appsettings.Distributed.json` 中无关的默认 `IndexPrefix` 调整
- `MainnetDevelopmentScopeIdentityExtensions` 及其对应测试
- 依赖匿名 scope 入口的 distributed smoke workflow 执行段落

这些内容在最终 fix 中不是必要条件，已经移除，避免把问题和实现噪音混在一起。

## 当前结论

结论可以概括成一句话：

这次方案不是“理论上最纯”的最终形态，但它已经是当前代码基线下明显优于宿主侧订阅旁路的正确修复，而且和仓库既有的 actor / relay / committed observation 设计方向一致；在去掉 dev-only host 旁路之后，它也更接近当前版本可提交的推荐实现。
