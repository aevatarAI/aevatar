---
title: Architecture Audit — Milestone-Oriented
status: active
owner: Loning
---

# Architecture Health Scorecard — 2026-04-08

Audit scope: Milestone-oriented (Living with AI Demo 04-17, NyxID M0 04-18)
Audit method: Two-layer (CLAUDE.md compliance + hot-path deep dive)
Reviewed by: /office-hours + /plan-ceo-review (HOLD SCOPE) + /plan-eng-review + outside voice (10 findings)

## Dimension Scores

| Dimension | Score | Evidence |
|-----------|-------|---------|
| CI Guards 合规 | 9/10 | architecture_guards.sh 全通过。94/95 架构测试通过。1 个 env-tooling (pnpm 未安装跳过 playground guard)。 |
| 分层合规 | 8/10 | Application 层 query 全走 readmodel reader。无跨层反向依赖。但 Host 层耦合度极高 (ScopeServiceEndpoints 205 类型)。 |
| 投影一致性 | 4/10 | **VIOLATION**: Host 层 3 处直接订阅 actor EventEnvelope 流 + TCS 阻塞等待，绕过 Projection Pipeline。Workflow 侧有正式 Projector (WorkflowExecutionRunEventProjector)，Platform 侧完全绕过。 |
| 读写分离 | 8/10 | Application 层 Query services 全部干净（readmodel-only）。Application 层 Command/Query 界限清晰。Host 层的投影 bypass 是唯一破口。 |
| 序列化 | 7/10 | 核心路径用 Protobuf。agents/ 的 JSON 序列化是 HTTP API 边界，可接受。 |
| Actor 生命周期 | 5/10 | agents/ 的 ConcurrentDictionary 单例违反 Actor 边界。StreamingProxyGAgent._proxyState 是影子状态机。 |
| 前端可构建性 | 2/10 | CLI Frontend 和 Console Web **都构建失败**。缺依赖 (@types/node, vitest, @tanstack/react-virtual, max CLI)。 |
| 测试覆盖 (agents/) | 0/10 | agents/ 3 个项目零测试。ScopeServiceEndpoints 集成测试 3/51 失败 (InvokeStreamEndpoint 500)。 |
| Governance 实质 | 2/10 | ServiceConfigurationGAgent 是 binding/endpoint CRUD + admission check。无 Goal/Scope/Objective Function 建模。无递归组合。无三层治理。Harness Theory 只在 CEO 库文档中。 |

**综合架构健康度: 5.0 / 10**

CEO 库 4/2 自评 "Architecture A" → 审计实测 5.0/10。CI 层面合规（9/10），但热路径有显著漂移。

## 漂移清单

### MILESTONE_BLOCKER (影响 04-17/04-18)

| # | 位置 | 违反规则 | 严重度 | 描述 |
|---|------|---------|--------|------|
| 1 | `agents/Aevatar.GAgents.NyxidChat/NyxIdChatActorStore.cs:20` | 中间层状态约束 | Critical | ConcurrentDictionary 单例做事实源。进程重启丢失所有对话状态。无法多节点。 |
| 2 | `agents/Aevatar.GAgents.StreamingProxy/StreamingProxyActorStore.cs:11-12` | 中间层状态约束 | Critical | 2 个 ConcurrentDictionary 单例 (rooms + participants)。同 #1。 |
| 3 | `agents/` 全部 3 个项目 | 测试要求 | Critical | 零测试覆盖。NyxID M0 关键路径无任何自动化验证。 |
| 4 | `test/Aevatar.GAgentService.Integration.Tests/ScopeServiceEndpointsTests.cs:1087` | — | High | InvokeStreamEndpoint 3 个测试失败 (500 error)。直接影响 API 调用路径。 |
| 5 | `tools/Aevatar.Tools.Cli/Frontend/` | — | High | TypeScript 编译失败。缺 @types/node, @tanstack/react-virtual, vitest 等依赖。 |
| 6 | `apps/aevatar-console-web/` | — | High | 构建失败。`max` CLI 未安装。 |

### BACKLOG (记录但不修)

| # | 位置 | 违反规则 | 严重度 | 保质期 |
|---|------|---------|--------|--------|
| 7 | `ScopeGAgentEndpoints.cs:352-379` | 统一投影链路 | High | v0.2 迁移时必须解决 |
| 8 | `ScopeServiceEndpoints.cs:1002, 1148` | 统一投影链路 | High | v0.2 迁移时必须解决 |
| 9 | `ScopeServiceEndpoints.cs` class | 代码耦合 | Medium | 205 类型耦合 (限制 96)，3 个方法 cyclomatic complexity > 26 |
| 10 | `ScopeGAgentEndpoints.cs` class | 代码耦合 | Medium | 111 类型耦合 (限制 96) |
| 11 | `StreamingProxyGAgent._proxyState` | Actor 执行模型 | Medium | 影子状态机绕过 ES。grain 重激活后可能状态不一致 |
| 12 | Governance 子系统 | 产品 thesis | Low | config CRUD 而非组织工程。Harness Theory 未落地。 |

## Guard 补充建议

| 建议 | 优先级 | 原因 |
|------|--------|------|
| `architecture_guards.sh` 扫描范围扩展到 `agents/` | P0 | agents/ 完全不在 CI 守卫范围内，是 NyxID M0 关键路径 |
| 新增 guard: Host 层禁止直接 `SubscribeAsync<EventEnvelope>` | P1 | 投影 bypass 是当前最大架构漂移 |
| playground asset drift guard 修复 (支持 npm/bun fallback) | P2 | 当前因 pnpm 未安装而跳过 |

## Harness Theory 备注

当前评分: **0-2 / 10**。`rg "Goal|Scope|Governance|Harness|Optimization|Adaptability" src/` 在领域模型层无匹配。Governance 子系统 (~2.6k LOC) 是 binding/endpoint/policy CRUD + admission check，不包含三层治理模型或递归组合。详细分析延后至 TODOS.md。
