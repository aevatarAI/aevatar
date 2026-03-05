# Foundation Runtime Callback Scheduler 重构审计打分 - 2026-03-06

## 1. 审计范围与决策修正

1. 审计对象：
   - `src/Aevatar.Foundation.Abstractions/Runtime/Callbacks/*`
   - `src/Aevatar.Foundation.Core/Pipeline/EventHandlerContext.cs`
   - `src/Aevatar.Foundation.Runtime/Callbacks/*`
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Callbacks/*`
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrain.cs`
   - `src/workflow/Aevatar.Workflow.Core/Modules/*`
   - `src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs`
   - `tools/ci/architecture_guards.sh`
   - `tools/ci/test_stability_guards.sh`
2. 本轮审计修正了上一版的重要口径：
   - 撤回“callback pending/lease/generation 必须上收到 business actor 的 event-sourced state”这一建议。
   - 新结论改为：`business actor runtime coordination` 采用 `volatile execution` 更合适；callback 运行态只保留在 activation 内存，丢 activation 即放弃 in-flight runtime。
   - `scheduler` 自身可按后端机制持久（如 Orleans reminder/grain state），但这不等于业务 actor 也要把运行态写进 event-sourced `State`。
3. 评分口径：100 分制，重点看“语义是否一致、实现是否单一主干、治理是否可验证”。

## 2. 修正后的架构结论

1. `Runtime.Callbacks` 这层抽象是对的。
2. callback fired 必须强制自描述，至少带齐：
   - `callback_id`
   - `generation`
   - `fire_index`
   - `fired_at_utc`
   - 业务相关键（如 `run_id / step_id / session_id / attempt`）
3. workflow/script 这类业务 actor 不应为了 timeout/retry/wait/LLM watchdog 把运行态强塞进 event-sourced `State`。
4. 如果接受 `volatile execution`，那么 activation 丢失后的晚到 callback 应按 `stale/no-op` 处理，而不是尝试恢复业务运行态。
5. 因此，新的正确目标不是“把 callback facts 全部写进 state”，而是：
   - `scheduler correctness` 做对
   - `callback metadata` 做强校验
   - `business actor runtime coordination` 明确为 volatile，并在文档/测试/门禁里写死这个语义

## 3. 总体评分（100 分制）

**总分：72 / 100（B-）**

| 维度 | 权重 | 得分 | 审计结论 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 16 | `Runtime.Callbacks` 命名和契约方向正确，但 script/workflow/runtime 三处语义仍未完全收敛。 |
| 单一主干与调度一致性 | 20 | 12 | timeout/timer 已进入 callback 主链，但 Orleans retry 仍保留 `Task.Delay` 第二路径。 |
| 运行时语义清晰度 | 20 | 12 | workflow 代码实际偏向 `volatile runtime`，但 script 仍把 pending query 放进 state，语义混搭。 |
| 对账与正确性 | 15 | 10 | fired callback 仍普遍是“弱 generation 校验”，缺少 `callback_id + generation` 强匹配。 |
| 命名与冗余清理 | 10 | 9 | `Runtime.Async -> Runtime.Callbacks` 已完成，但 callback key/string 拼接仍散落。 |
| 可验证性（测试/门禁/文档） | 15 | 13 | 文档已可修正，但当前代码库仍缺 volatile runtime 专项门禁与测试覆盖。 |

## 4. 已确认正确的部分

### C1 `Runtime.Async -> Runtime.Callbacks` 收敛方向正确

1. 证据：
   - `src/Aevatar.Foundation.Abstractions/Runtime/Callbacks/IActorRuntimeCallbackScheduler.cs:3`
2. 结论：
   - 问题确实不在 `Runtime`，而在旧的 `Async` 命名过泛；现在统一到 `Callbacks` 是正确收敛。

### C2 Orleans cancel 已按 lease backend 路由

1. 证据：
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Callbacks/OrleansActorRuntimeCallbackScheduler.cs:104`
2. 结论：
   - 取消路由现在以 `RuntimeCallbackLease.Backend` 为权威事实，不再猜测 inline/dedicated 所在位置。

### C3 InMemory cancel generation compare/remove 已具备条件删除语义

1. 证据：
   - `src/Aevatar.Foundation.Runtime/Callbacks/InMemoryActorRuntimeCallbackScheduler.cs:62`
   - `src/Aevatar.Foundation.Runtime/Callbacks/InMemoryActorRuntimeCallbackScheduler.cs:71`
2. 结论：
   - 原 review 中“旧 generation cancel 误删新 callback”的核心竞态已被修正。

## 5. 主要问题

### F1（P1）Orleans runtime 仍保留 `Task.Delay` 第二套 retry 调度路径

1. 证据：
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrain.cs:375`
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrain.cs:385`
2. 问题：
   - callback scheduler 已经存在，但 retry 仍绕过它直接 `Task.Delay` 后发流。
   - 这让系统仍保留两条“延迟触发”主链。
3. 结论：
   - 无论 runtime facts 是否持久，`delay/timeout/retry` 都应该统一走 callback 主链。

### F2（P1）fired callback 仍普遍是“弱 generation 校验”，不是强 lease 对账

1. 证据：
   - `src/workflow/Aevatar.Workflow.Core/Modules/DelayModule.cs:116`
   - `src/workflow/Aevatar.Workflow.Core/Modules/WaitSignalModule.cs:126`
   - `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:110`
   - `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:404`
   - `src/workflow/Aevatar.Workflow.Core/Modules/LLMCallModule.cs:275`
   - `src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs:270`
2. 问题：
   - 当前大多只看 `generation`，甚至 script timeout handler 完全不读 callback metadata。
   - 这在 `volatile runtime` 模式下更危险，因为 actor 不打算做持久恢复，那就更依赖 fired message 自描述和强校验。
3. 正确目标：
   - 统一强制 `callback_id + generation` 匹配；缺 metadata 一律丢弃。

### F3（P1）当前代码对 runtime 语义是“混搭”的：workflow 偏 volatile，script 偏 persisted

1. 证据：
   - workflow 模块大量使用 activation 内存字典：
     - `src/workflow/Aevatar.Workflow.Core/Modules/DelayModule.cs:16`
     - `src/workflow/Aevatar.Workflow.Core/Modules/WaitSignalModule.cs:17`
     - `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:18`
     - `src/workflow/Aevatar.Workflow.Core/Modules/LLMCallModule.cs:31`
   - script runtime 仍把 pending definition query 写入 state：
     - `src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs:40`
     - `src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs:270`
     - `src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs:364`
2. 问题：
   - 一个系统里同时存在两种 runtime 语义：
     - workflow：activation 丢失即 runtime 丢失
     - script：尝试通过 state 恢复 pending query
   - 这会让 callback 主链的恢复/取消/晚到事件处理规则无法统一。
3. 结论：
   - 必须选边并写进文档。按本轮修正后的判断，推荐统一到 `volatile runtime`。

### F4（P2）如果采用 `volatile runtime`，就必须把“activation 丢失 = in-flight runtime 放弃”写成显式契约

1. 当前缺口：
   - 旧蓝图仍写着“Orleans 生产语义下，调度事实以持久态为唯一权威”：`docs/architecture/actor-runtime-stream-callback-request-reply-capability-refactor-blueprint-2026-03-05.md:50`
   - 但 workflow 实现实际已经是内存字典模型：`WorkflowLoopModule.cs:18`、`WaitSignalModule.cs:17` 等。
2. 影响：
   - 文档说“可恢复”，代码却是“丢 activation 就丢运行态”。这是架构级误导。
3. 正确口径：
   - callback scheduler 后端可以持久；business actor 的 runtime coordination 不恢复。
   - 晚到 callback 在 activation 恢复后命不中本地活跃态，直接 `no-op`。

### F5（P2）callback key 与 callbackId 仍大量手工字符串拼接

1. 证据：
   - `src/Aevatar.Foundation.Runtime/Callbacks/InMemoryActorRuntimeCallbackScheduler.cs:91`
   - `src/workflow/Aevatar.Workflow.Core/Modules/DelayModule.cs:156`
   - `src/workflow/Aevatar.Workflow.Core/Modules/WaitSignalModule.cs:234`
   - `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:684`
   - `src/workflow/Aevatar.Workflow.Core/Modules/LLMCallModule.cs:348`
2. 问题：
   - 只要 `run_id / step_id / signal_name / session_id` 含分隔符，就会带来碰撞或误取消风险。
3. 建议：
   - 提供统一 key composer，禁止模块自行拼接字符串。

### F6（P2）门禁和测试还没有把 `volatile runtime` 语义守住

1. 证据：
   - `tools/ci/test_stability_guards.sh` 只扫测试中的轮询等待，不校验 runtime callback 主链。
   - `tools/ci/architecture_guards.sh` 目前没有“volatile runtime / strict callback metadata”专项守卫。
2. 需要新增的验证：
   - fired callback 缺 `callback_id/generation` 时必须被丢弃。
   - activation 内存事实清空后，晚到 callback 必须 `no-op`。
   - workflow/script 不得再次把 runtime callback coordination 写进 event-sourced state。

## 6. 当前最合理的目标架构

1. `business state` 继续 event-sourced。
2. `runtime callback coordination` 不进入 business actor 的 event-sourced state。
3. `scheduler` 负责交付，`actor` 负责在 activation 内根据本地活跃态对账。
4. fired callback 消息必须自描述。
5. activation 丢失后不做恢复，晚到事件直接丢弃。

## 7. 合并建议

1. 如果本次目标只是：
   - 完成 `Runtime.Callbacks` 命名收敛
   - 修掉 Orleans cancel 路由缺口
   - 修掉 InMemory stale cancel 竞态
   这些局部目标可以成立。
2. 如果本次目标是：
   - 把 callback runtime 作为唯一延迟主干
   - 同时把 runtime 语义收敛到明确的 `volatile execution`
   当前仍**不建议按“重构完成”口径合并定稿**。

## 8. 复审结论

上一版审计里“callback facts 应上收到 actor event-sourced state”这一条，经过本轮约束复核后应当撤回。对你们这个 actor/event-sourcing 系统，更合理的做法是：

1. 业务事实持久化
2. runtime callback coordination 不持久化
3. fired callback 强自描述
4. activation 丢失后放弃 in-flight runtime

基于这个新基线，当前代码的真正问题不再是“为什么没把事实写进 state”，而是：

1. 仍有第二套 retry 调度路径
2. callback fired 仍未做强 lease 对账
3. workflow 与 script 的 runtime 语义还没有统一
4. 文档/测试/门禁还没把 `volatile runtime` 写死

所以最新结论是：**方向应改为 `volatile runtime`，但当前实现和治理仍未完全对齐，暂不应按“重构完成”收口。**
