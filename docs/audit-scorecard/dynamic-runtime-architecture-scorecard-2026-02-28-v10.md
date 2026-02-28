# Dynamic Runtime 架构实施评分卡（2026-02-28，v10）

## 1. 审计范围与模式

1. 评分模式：**实施评分模式（Implementation Scoring）**。
2. 审计对象：
- `src/Aevatar.DynamicRuntime.*`
- `test/Aevatar.DynamicRuntime.Application.Tests/*`
- `docs/architecture/dynamic-gagent-csharp-script-runtime-requirements.md`
- `docs/architecture/ai-script-runtime-implementation-change-plan.md`
3. 重构口径：**删除优先，不做兼容壳层**。

## 2. 客观验证结果（本轮实测）

| 检查项 | 命令 | 结果 |
|---|---|---|
| 全量构建 | `dotnet build aevatar.slnx --nologo --tl:off` | 通过（0 error） |
| DynamicRuntime 应用测试 | `dotnet test test/Aevatar.DynamicRuntime.Application.Tests/Aevatar.DynamicRuntime.Application.Tests.csproj --nologo --tl:off` | 通过（35/35） |
| 架构守卫 | `bash tools/ci/architecture_guards.sh` | 通过 |
| 投影路由守卫 | `bash tools/ci/projection_route_mapping_guard.sh` | 通过 |
| 测试稳定性守卫 | `bash tools/ci/test_stability_guards.sh` | 通过 |

## 3. 总分

**89 / 100（A-）**

结论：本轮已完成此前 blocking 主链修复（reconcile 基线收敛、envelope 投递闭环、run timeout/retry 事件化、应用层硬编码投影器移除），DynamicRuntime 已从“可运行”提升到“主链闭环可验证”。剩余差距主要在“生产级收敛策略深度”和“统一 projection runtime 主链化”。

## 4. 六维评分

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 18 | `ScriptSideEffectPlanner` / `DynamicRuntimeEventProjector` 由 DI 注入，移除应用层硬编码构造。 |
| CQRS 与统一投影链路 | 20 | 16 | `Command -> Event -> Project` 主路径完整，但仍由应用层直接驱动 projector，未完全并入统一 projection runtime。 |
| Projection 编排与状态约束 | 20 | 17 | Envelope 已具备 `lease -> pull -> execute -> ack/retry` 闭环；关键运行态由 actor/端口承载，无中间层事实态字典违规。 |
| 读写分离与会话语义 | 15 | 14 | Run 已补齐 `timeout/retry` 事件化推进，含 attempt/maxAttempt/backoff 语义。 |
| 命名语义与冗余清理 | 10 | 9 | `Aevatar.DynamicRuntime.*` 命名主线一致，旧兼容壳未引入。 |
| 可验证性（门禁/构建/测试） | 15 | 15 | build + 35 条动态运行测试 + 三类守卫全部通过。 |

## 5. 关键对齐证据

1. Run timeout/retry 事件化推进：
- `src/Aevatar.DynamicRuntime.Application/DynamicRuntimeApplicationService.cs:686`
- `src/Aevatar.DynamicRuntime.Application/DynamicRuntimeApplicationService.cs:755`
- `src/Aevatar.DynamicRuntime.Application/DynamicRuntimeApplicationService.cs:774`

2. Envelope 投递闭环（订阅消费 + ack/retry）：
- `src/Aevatar.DynamicRuntime.Infrastructure/InMemoryEventEnvelopeDeliveryPort.cs:27`
- `src/Aevatar.DynamicRuntime.Application/DynamicRuntimeApplicationService.cs:1774`
- `src/Aevatar.DynamicRuntime.Application/DynamicRuntimeApplicationService.cs:1807`

3. Delivery kind 语义分层（runtime domain vs script output）：
- `src/Aevatar.DynamicRuntime.Application/DynamicRuntimeApplicationService.cs:1533`
- `src/Aevatar.DynamicRuntime.Application/DynamicRuntimeApplicationService.cs:1768`

4. Compose reconcile 从占位改为显式对账：
- `src/Aevatar.DynamicRuntime.Infrastructure/DefaultScriptComposeReconcilePort.cs:19`

5. 核心新增回归测试（timeout/retry/dispatch）：
- `test/Aevatar.DynamicRuntime.Application.Tests/DynamicRuntimeApplicationServiceTests.cs:145`
- `test/Aevatar.DynamicRuntime.Application.Tests/DynamicRuntimeApplicationServiceTests.cs:204`
- `test/Aevatar.DynamicRuntime.Application.Tests/DynamicRuntimeApplicationServiceTests.cs:255`

## 6. 扣分项（剩余问题）

| 级别 | 扣分 | 问题 | 证据 |
|---|---:|---|---|
| Major | -5 | Reconcile 仍是“基线收敛判断”，尚未覆盖依赖拓扑分批、滚动替换策略、失败回滚编排。 | `src/Aevatar.DynamicRuntime.Infrastructure/DefaultScriptComposeReconcilePort.cs:19` |
| Major | -4 | Application 仍内联调用 projector（非统一 runtime 编排入口）。 | `src/Aevatar.DynamicRuntime.Application/DynamicRuntimeApplicationService.cs:1502` |
| Minor | -2 | 现有 Envelope 闭环实现为 in-memory 语义，尚未接入分布式持久 delivery state。 | `src/Aevatar.DynamicRuntime.Infrastructure/InMemoryEventEnvelopeBusState.cs:5` |

## 7. 后续优先级（无需兼容重构）

1. P0：将 reconcile 扩展为“依赖拓扑 + rollout + rollback”事件化编排，不保留占位逻辑。
2. P1：将应用层内联 projector 推进迁移到统一 projection runtime 主链，删除双入口。
3. P1：将 delivery state 从 in-memory 演进到可持久/可分布式实现，并补长稳压测门禁。
