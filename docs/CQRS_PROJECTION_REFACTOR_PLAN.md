# CQRS Projection 重构计划（Order 解耦）

## 1. 背景

当前未提交改造已将通用 AI 事件投影抽到 `Aevatar.AI.Projection`，并引入 `IProjectionEventApplier<,,>`。方向正确，但现阶段仍存在明显的排序耦合和语义风险：

1. `Order` 仍是跨模块的“魔法数字协议”，扩展方需要理解全局数字带才能安全接入。
2. `StateVersion` 在 reducer 执行前即递增，导致“无业务变更事件”也会推进版本。
3. AI reducer 默认全量注册，未实现 applier 的事件会成为 no-op reducer，但仍参与投影流程。
4. reducer/applier 仅靠 `Order` 排序，缺乏冲突检测与阶段语义。

## 当前进度（2026-02-19）

1. `Phase 1` 已完成：`IProjectionEventReducer`、`IProjectionEventApplier` 已改为返回 `bool` 变更结果；`WorkflowExecutionReadModelProjector` 已改为汇总 `mutated` 后再更新 `StateVersion/LastEventId`。
2. `Phase 2` 部分完成：Projection reducer/applier 的全局 `Order` 耦合已去除，按事件类型分组执行。
3. `Phase 3` 部分完成：AI reducer 已支持按事件类型选择性注册，Workflow 默认仅注册 `TextMessageEnd` reducer。

## 2. 重构目标

1. 让 `StateVersion/LastEventId` 仅在 read model 实际发生变更时更新。
2. 消除跨模块全局 `Order` 数字耦合，排序语义改为“同事件类型内局部有序 + 显式阶段”。
3. 保留统一 Projection Pipeline（CQRS 与 AGUI 同链路），不引入双轨实现。
4. 删除无业务价值的空转逻辑与冗余注册。

## 3. 非目标

1. 不改变 Command/Query 对外 API 语义。
2. 不引入新的运行时或存储引擎。
3. 不做兼容层保留，旧接口可直接替换。

## 4. 目标架构

### 4.1 事件处理语义

1. `Reducer/Applier` 返回“是否发生变更”结果。
2. `Projector` 汇总结果后再更新 `StateVersion/LastEventId`。
3. 无变更事件仅记处理统计，不推进版本。

### 4.2 排序语义

1. 先按 `EventTypeUrl` 分组，再在组内排序。
2. 组内排序从“裸 `Order`”升级为“`Stage + OrderInStage`”。
3. 同阶段同序号冲突在启动时直接失败（Fail Fast）。

### 4.3 注册语义

1. AI reducer 支持按事件类型选择性注册，避免全量 no-op。
2. 业务模块只注册自己需要的 applier。
3. 删除无效 reducer/applier 与无意义扩展点。

## 5. 分阶段执行计划

### Phase 0：基线锁定（0.5 天）

1. 固化现有测试基线，补齐当前缺失断言。
2. 新增回归用例。
3. `TextMessageStart/Content/Tool*` 无 applier 时不应推进 `StateVersion`。
4. 同一 `EventTypeUrl` 多 reducer 执行顺序稳定。
5. 不同 `EventTypeUrl` 的 `Order` 不互相影响。

交付物：
1. 测试用例与基线报告。

### Phase 1：修复版本语义（1 天）

1. 调整 `IProjectionEventReducer` 与 `IProjectionEventApplier` 签名，返回 `bool mutated`（或等价结果对象）。
2. `WorkflowExecutionReadModelProjector` 仅在任一 reducer 返回变更时更新 `StateVersion/LastEventId`。
3. 将 `RecordProjectedEvent` 从“投影前固定调用”改为“投影后按变更调用”。

交付物：
1. 接口与实现改造提交。
2. `StateVersion` 语义回归测试通过。

### Phase 2：Order 解耦（1 天）

1. `WorkflowExecutionReadModelProjector` 调整排序策略。
2. 先 `GroupBy(EventTypeUrl)`，后组内排序。
3. 引入阶段枚举（建议：`Core`、`Domain`、`Integration`）与 `OrderInStage`。
4. 内置 reducer/applier 替换为阶段化排序，去掉跨模块 magic number 依赖。

交付物：
1. 新排序模型与迁移后的内置实现。
2. 顺序一致性测试。

### Phase 3：DI 收敛与空转删除（0.5-1 天）

1. AI reducer 注册改为可选式 API（按事件类型注册）。
2. Workflow 默认仅注册已实现 applier 的事件 reducer。
3. 删除无业务价值的 no-op 注册与相关文档陈述。

交付物：
1. 新 DI API 与旧 API 删除。
2. 文档同步更新。

### Phase 4：守卫与可观测性（0.5 天）

1. 启动时校验。
2. 同一事件类型内 `Stage/OrderInStage` 冲突直接报错。
3. 增加投影指标：`processed`、`mutated`、`no_op`、`deduplicated`。
4. 在 README 与架构文档中明确排序和版本语义。

交付物：
1. 启动校验实现。
2. 监控指标与文档更新。

## 6. 影响范围

1. `src/Aevatar.CQRS.Projection.Abstractions`
2. `src/Aevatar.AI.Projection`
3. `src/workflow/Aevatar.Workflow.Projection`
4. `src/workflow/Aevatar.Workflow.Presentation.AGUIAdapter`（仅联动验证）
5. `test/Aevatar.Workflow.Host.Api.Tests`
6. `docs/CQRS_ARCHITECTURE.md`、`docs/FOUNDATION.md`、`src/workflow/Aevatar.Workflow.Projection/README.md`

## 7. 验收标准（DoD）

1. `StateVersion` 与真实 read model 变更次数一致。
2. 未实现 applier 的事件不会推进 `StateVersion`。
3. 扩展方无需了解全局 magic number，即可安全新增 reducer/applier。
4. 同事件类型内排序可预测，冲突可检测。
5. `dotnet build aevatar.slnx --nologo` 与 `dotnet test aevatar.slnx --nologo` 全绿。
6. 架构文档与 README 同步完成。

## 8. 风险与回滚

1. 风险：接口签名变更会波及所有 reducer/applier。
2. 处理：按 Phase 分批提交，每阶段先补测试再改实现。
3. 回滚：按提交粒度回滚到上一个稳定阶段，不做局部热修拼补。

## 9. 建议执行顺序

1. 先做 Phase 1（语义正确性），立即止损版本漂移。
2. 再做 Phase 2（排序解耦），降低后续扩展耦合成本。
3. 最后做 Phase 3/4（清理与守卫），形成长期可维护形态。
