# Aevatar.Scripting 架构复评分（重评版，2026-03-02 R6）

## 1. 结论

- 本轮重评分：`93/100`
- 上一版评分：`82/100`（R5）
- 结论：核心耦合问题已显著收敛，双入口主链路已闭环；剩余问题集中在命名策略统一与演化管理器进一步解耦。

## 2. 已完成整改

### A. Runtime 能力拆分（已完成）

1. `IScriptRuntimeCapabilities` 已拆分为：
   `IScriptInteractionCapabilities`、`IScriptAgentLifecycleCapabilities`、`IScriptEvolutionCapabilities`。
2. `ScriptRuntimeCapabilities` 由“超级能力对象”改为组合代理。
3. 影响：能力边界更清晰，测试桩复杂度下降。

### B. Orchestrator 去装配耦合（已完成）

1. `ScriptRuntimeExecutionOrchestrator` 不再直接构造能力对象。
2. 通过 `IScriptRuntimeCapabilityComposer` 统一装配能力。
3. 影响：Application 编排职责与能力装配职责分离。

### C. 外部入口落地（已完成）

1. 新增 `ScriptEvolutionApplicationService` 作为外部通道应用层入口。
2. 新增 `POST /api/scripts/evolutions/proposals`，通过 Hosting endpoint 接入。
3. 外部入口与脚本入口统一汇聚到 `ScriptEvolutionManagerGAgent` 主链路，无旁路发布。

## 3. 仍需优化项（按优先级）

### P1: Actor 地址命名仍有硬编码分散

证据：

1. `script-evolution-manager`、`script-catalog` 等常量仍分布在多个类中。
2. 尚未形成统一 `IScriptingActorAddressResolver`。

影响：

1. 地址规则变更成本偏高。
2. 多点修改存在遗漏风险。

### P2: EvolutionManager 仍承担完整推进流程

证据：

1. `ScriptEvolutionManagerGAgent` 同时处理状态推进与策略/验证/发布调用。
2. 流程分支继续扩展时，单类复杂度会增长。

影响：

1. 可维护性与变更可控性仍有改进空间。

## 4. 复评分项

| 维度 | 得分 | 说明 |
|---|---:|---|
| 分层清晰度 | 14/15 | 编排/装配边界显著改善。 |
| 依赖反转 | 14/15 | 端口化完整，入口统一走抽象。 |
| 抽象层次质量 | 13/15 | 能力分层已完成，地址策略仍待统一。 |
| Actor 化事实源 | 15/15 | 一致性事实保持在 Actor 状态。 |
| CQRS/Projection 单链路 | 14/15 | 双入口统一投影链路闭环。 |
| 可测试性 | 13/15 | 新增外部通道 E2E，关键路径覆盖提升。 |
| 可演进性 | 10/10 | 双入口迭代能力已落地。 |

## 5. 结论分级

- 架构健康度：`A-`
- 当前状态：`可持续迭代`

## 6. 下一阶段建议

1. 引入 `IScriptingActorAddressResolver`，集中管理 actor id 命名与解析。
2. 将 EvolutionManager 的外部调用推进逻辑进一步模块化（policy/validation/promotion stage）。
3. 增加双入口一致性回归用例（同输入 external/self 输出一致决策）。
