# Aevatar.Scripting 架构复评分（重评版，2026-03-02 R7）

## 1. 结论

- 本轮重评分：`96/100`
- 上一版评分：`93/100`（R6）
- 结论：核心耦合进一步下降，地址命名与演化流程抽象已统一，当前进入高可演进状态。

## 2. 新增完成项

### A. 统一地址解析抽象（已完成）

1. 新增 `IScriptingActorAddressResolver`。
2. 新增 `DefaultScriptingActorAddressResolver` 实现并全局注入。
3. `ScriptEvolutionCapabilities`、`ScriptEvolutionApplicationService`、`RuntimeScriptDefinitionLifecyclePort`、`RuntimeScriptPromotionPort` 统一改为 resolver 获取 actor 地址。

### B. Evolution 流程端口下沉（已完成）

1. 新增 `IScriptEvolutionFlowPort` 与 `RuntimeScriptEvolutionFlowPort`。
2. `ScriptEvolutionManagerGAgent` 从直接依赖 `Policy/Validation/Promotion` 三端口收敛为单一 `FlowPort`。
3. Actor 仍保持事件化状态推进，流程实现可在 Hosting 侧独立演化。

## 3. 复评分项

| 维度 | 得分 | 说明 |
|---|---:|---|
| 分层清晰度 | 15/15 | 编排、装配、流程、地址语义职责分层明确。 |
| 依赖反转 | 15/15 | 关键依赖均通过抽象端口注入。 |
| 抽象层次质量 | 14/15 | 运行能力、地址策略、演化流程均已形成稳定抽象。 |
| Actor 化事实源 | 15/15 | 事实仍由 Actor 状态持有，无中间层事实缓存。 |
| CQRS/Projection 单链路 | 14/15 | 双入口统一主链路与投影链路稳定。 |
| 可测试性 | 13/15 | 关键回归覆盖充分，仍可补双入口对等性更细粒度用例。 |
| 可演进性 | 10/10 | 外部与自我演化均可独立演进而不破坏主链路。 |

## 4. 结论分级

- 架构健康度：`A`
- 当前状态：`可持续大规模演进`

## 5. 仍可优化

1. 增加 dual-source parity 的细粒度对账测试（同输入 external/self 的逐事件一致性）。
2. 将策略规则与验证策略配置化，支持多租户或多环境策略切换。
