# PR Review 打分审计报告（Workflow Retry + WaitSignal）- 2026-02-26

## 1. 审计范围与输入

1. 审计对象：
   - `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs`
   - `src/workflow/Aevatar.Workflow.Core/Modules/WaitSignalModule.cs`
2. 输入来源：本次 PR review 结论（3 条有效问题：P1×1、P2×1、P3×1）。
3. 评分口径：`docs/audit-scorecard/README.md`（100 分制，6 维度）。

## 2. 审计结论（摘要）

1. 当前变更存在运行时语义缺陷，影响 retry 正确性、wait_signal 并发等待可靠性与 run 关联一致性。
2. 结论：**当前不建议合并**，需先修复阻断问题并补回归测试。

## 3. 总体评分（100 分制）

**总分：78 / 100（B）**

| 维度 | 权重 | 得分 | 扣分说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 17 | 无明显跨层耦合，但 run_id 口径不一致导致跨模块关联不稳定。 |
| CQRS 与统一投影链路 | 20 | 15 | 同一 run 的事件归并与处理存在 ID 规范化偏差风险。 |
| Projection 编排与状态约束 | 20 | 13 | `wait_signal` 等待态键设计无法承载同 run 同 signal 多 waiter。 |
| 读写分离与会话语义 | 15 | 12 | retry 未保留原始输入，失败重试语义偏移。 |
| 命名语义与冗余清理 | 10 | 9 | 语义整体清晰，主要问题不在命名。 |
| 可验证性（门禁/构建/测试） | 15 | 12 | 现有测试未覆盖本次三条回归路径。 |

## 4. 问题分级与证据

### F1（P1）Retry 未保留失败前原始输入（阻断）

- 证据：
  - `WorkflowLoopModule.TryRetryAsync` 重试时使用 `evt.Output ?? ""` 作为输入：
    - `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:241`
- 影响：
  - 对于失败且无输出的步骤（如 `connector_call` 异常），重试会变成空输入，不是“重试同一请求”。
  - 直接破坏 step 级 retry 语义，导致暂态故障场景不可恢复。
- 修复准入：
  - retry 必须使用失败步骤的原始输入（建议从 step/run 运行态恢复），不得回退为 `evt.Output`。

### F2（P2）`wait_signal` 等待态未按 step 维度隔离（主要）

- 证据：
  - `_pending` 键仅为 `(RunId, SignalName)`：
    - `src/workflow/Aevatar.Workflow.Core/Modules/WaitSignalModule.cs:16`
  - 注册等待时同键直接覆盖：
    - `src/workflow/Aevatar.Workflow.Core/Modules/WaitSignalModule.cs:42-45`
- 影响：
  - 同一 run 内多个 `wait_signal` 步骤使用相同 `signal_name` 时会互相覆盖。
  - 信号到达/超时可能恢复或取消错误步骤，其他 waiter 卡死。
- 修复准入：
  - 等待态必须包含 `stepId` 维度（或改为 waiter 列表模型），确保同 run 同 signal 的并发 waiter 可正确关联。

### F3（P3）WorkflowLoop run_id 未统一规范化（主要）

- 证据：
  - 启动时直接使用原始 `evt.RunId`：
    - `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:46`
  - completion 解析也未规范化：
    - `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:425-430`
  - 其他模块普遍使用 `WorkflowRunIdNormalizer.Normalize(...)`：
    - `src/workflow/Aevatar.Workflow.Core/Primitives/WorkflowRunIdNormalizer.cs:8-9`
- 影响：
  - 当调用方传入含首尾空白的 run_id，loop 活跃态与其他模块 completion run_id 不一致，可能被误判为非活跃 run。
- 修复准入：
  - Start 与 completion 两侧统一调用 `WorkflowRunIdNormalizer.Normalize(...)`，保证关联键一致。

## 5. 合并门禁（Blocking Exit Criteria）

1. 修复 F1：重试输入必须等于失败前该 step 的原始输入。
2. 修复 F2：`wait_signal` 并发 waiter 不能相互覆盖，信号/超时需命中正确 step。
3. 修复 F3：WorkflowLoop 全链路 run_id 归一化。
4. 补充最小回归测试并通过：
   - retry 无输出失败后再次重试仍使用原输入；
   - 同 run 同 signal 多 waiter 不串扰；
   - run_id 含空白时流程仍可正确完成。

## 6. 审计结论

本 PR 当前问题属于运行时行为缺陷，不是纯风格问题。修复上述 3 项并补回归后，再进入合并评审。
