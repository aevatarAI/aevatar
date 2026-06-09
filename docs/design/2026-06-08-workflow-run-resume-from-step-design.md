---
title: "Workflow Run 失败恢复：改 YAML 后从指定 step fork 续跑"
status: draft
owner: eanzhao
---

# Workflow Run 失败恢复：改 YAML 后从指定 step fork 续跑

Generated via superpowers:brainstorming on 2026-06-08. Branch: feature/integrate. Repo: aevatarAI/aevatar.

## 1. 背景与目标

workflow run（`WorkflowRunGAgent`）跑失败后，目前只能从头重跑。需求：**失败后修改 workflow YAML，从中间某一步基于"已跑出来的中间状态"继续执行**，而不是从第一步重来。

owner 拆出的三条诉求：

1. 旧 run 的 State 要存所有中间过程（不知道哪步会失败）。
2. 旧 run 的中间过程能 set 进新 run 的 State。
3. 新 run 能在 YAML 规定的某个 step，基于已加载状态继续执行。

**核心结论**：这三条收敛为**一个核心原语 + 两个驱动**，不造第二系统：

> **Fork 原语**：新建一个 `WorkflowRunGAgent`，绑定一份 YAML（改过的 / 原样的），用旧 run 的 variables 作 seed 注入，从指定 step 起跑。

- 人工 fork：失败 → 人改 YAML → 调接口从指定 step 恢复。
- 自动重试：失败 → 按 run 级策略用同一 YAML 从失败步重跑。

二者只是同一原语的不同入参与触发方式。

非目标见 §15。

## 2. 现状与 Ground Truth（file:line，已实证）

- **定义态 GAgent** = `WorkflowGAgent`（kind `workflow.definition`），`src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs`。**不叫** `WorkflowDefinitionGAgent`。
- **运行态 GAgent** = `WorkflowRunGAgent`（kind `workflow.run`），`src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs`。run-scoped、短生命周期；终态状态常量含 `failed`/`completed`/`stopped`。
- **每次跑新建一个 run actor**：`WorkflowRunActorPort.CreateRunAsync`（`src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowRunActorPort.cs:67-117`）每调一次 `_runtime.CreateAsync<WorkflowRunGAgent>(BuildRunActorId(...))`（:86-88），actorId = `{definitionActorId}:run:{GUID:N}`；随后 `CreateWorkflowRunBindEnvelope(...)` 携带 `WorkflowYaml/WorkflowName/InlineWorkflowYamls/ScopeId` 投递 bind（:96-105）。入参是 `WorkflowDefinitionBinding`。
- **YAML 来源有三条路径**（owner 原以为只有"ornn→定义"一条）：
  - ornn skill → `OrnnRemoteSkillFetcher`（`src/Aevatar.AI.ToolProviders.Ornn/OrnnRemoteSkillFetcher.cs`）抽出 `workflows/*.yaml`，再分两支：**mount** 走 `IScopeWorkflowCommandPort.UpsertAsync`（进 catalog 成持久定义）；**inline 直跑** 走 `aevatar_start_workflow` → `WorkflowChatSource.InlineYamlBundle` → `WorkflowRunActorResolver`（`src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunActorResolver.cs`）直接建 run，**不经持久定义 GAgent**。
  - 文件加载：`WorkflowDefinitionFileLoader` + `FileBackedWorkflowCatalogPort`。
  - 含义：**新 run 用 inline YAML 直跑是现成能力**，本设计 fork 复用它。
- **中间状态已持久化（需求1 ≈ 已存在）**：
  - `WorkflowRunState.execution_states`（`map<string, Any>`，field 12，`src/workflow/Aevatar.Workflow.Core/workflow_state.proto:134`）装着内核态。
  - `WorkflowExecutionKernelState`（同 proto :260-274）：`variables`（field 5，:266，**每步输出按 step_id**）、`current_step_id`（:264）、`current_step_input`（:265）、`retry_attempts_by_step_id`（:267）、`execution_ids_by_step_id`（:272）等。
  - 写入时机：`DispatchStepAsync` 与 `HandleStepCompletedAsync` 都 `SaveStateAsync`；失败步**之前**所有步的输出在失败时已落盘。
- **内核固定从第一步起跑（需求3 的插桩点）**：`WorkflowExecutionKernel.HandleStartWorkflowAsync`（`src/workflow/Aevatar.Workflow.Core/Execution/WorkflowExecutionKernel.cs:82-148`）：
  - :117 `state.Variables.Clear()`；:125 `state.Variables["input"] = evt.Input`；:127 `MergeStartParametersIntoVariables(state.Variables, evt.Parameters)`（**已有"start 时注入变量"先例**）；:130 `_workflow.Steps.FirstOrDefault()` 取第一步；:147 `DispatchStepAsync(entry, evt.Input, ...)`。
  - step 推进是事件驱动 self-continuation：`HandleStepCompletedAsync` 存 `Variables[stepId]=output` 后 `GetNextStep` 取下一步再 `DispatchStepAsync`。
- **已有 step 级容错（与本设计正交，不冲突）**：内核 `TryRetryAsync`/`TryOnErrorAsync`（同一 run 内重试当前步 / on_error 跳转）。本设计是 **run 级**（fork 新 run），层次不同。
- **已有 resume ≠ 本需求（诚实命名，不复用）**：`WorkflowResumeCommand`/`WorkflowResumedEvent`（`src/workflow/Aevatar.Workflow.Application.Abstractions/Runs/WorkflowRunControlModels.cs:78-88`；`src/workflow/Aevatar.Workflow.Abstractions/workflow_execution_messages.proto` 的 `WorkflowResumedEvent`）虽带 `StepId`，但**只服务 human approval/input 等挂起点**，由 `HumanApprovalModule`/`HumanInputModule`/`SecureInputModule` 消费。**不存在** checkpoint / start-from-step / replay。现有 resume 端点 `ChatEndpoints.HandleResume`（`src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatEndpoints.cs`）同理。
- **序列化**：内部状态/事件/跨 actor 载荷全 Protobuf（`workflow_state.proto` / `workflow_execution_messages.proto`）。

## 3. 设计决策（已与 owner 对齐）

| # | 决策 | 结论 |
|---|---|---|
| 范围 | 用法 | **两者都要**：人工改-YAML fork + 自动同-YAML 重试 |
| 改后 YAML 入口 | 怎么进新 run | **inline 随请求传入**，复用现有 inline-bundle→新 run 路径；省略=复用 source run 的 YAML |
| seed | 中间产物注入 | **自动为主 + 允许覆盖**：fork 给 `source_run_id`，经 query 自动提取 seed；请求可覆盖/追加个别变量 |
| 自动 fork 归属 | 谁建新 run | **committed 事件 + 专用协调消费方**：失败 run 发 `WorkflowRunForkRequestedEvent`，coordinator 消费后用 `WorkflowRunActorPort` 建新 run |
| D1 | 新 run vs 原地恢复 | **fork 新 run**（owner 原意 + CLAUDE.md「升级前滚/禁止原地热替换」）；旧失败 run 保留为不可变记录 |
| D2 | 不复用 suspension resume | 新建独立 fork 路径与事件；不污染 `WorkflowResumedEvent` 语义 |
| D3 | 新 run 不侧读旧 run | seed 由 application 层经 query 读旧 run，**显式**塞进新 run 的 bind 命令（Protobuf）；新 run 绝不读旧 run 的 store |
| D4 | step 身份 | 按 step `id`；不建自动映射引擎（YAGNI） |

## 4. 架构总览

```
                         ┌─────────────────────────────────────────┐
   人工 driver (HTTP)    │              Fork 原语                     │
   POST .../runs/fork ──►│  WorkflowForkRunService                  │
                         │   1. query 旧 run → seed + (base YAML)    │
   自动 driver (策略)    │   2. seed 合并 overrides                  │
   WorkflowRunForkReq ──►│   3. 校验 YAML compile + start step 存在  │
   uestedEvent(committed)│   4. WorkflowRunActorPort.CreateRunAsync  │
        │                │      (binding + WorkflowRunForkSeed)    │
   coordinator 消费 ─────┘                   │                       │
                                             ▼
                            新 WorkflowRunGAgent (新 actorId)
                              bind 携带 fork_seed
                                 │
                                 ▼ StartWorkflowEvent(fork_seed)
                            WorkflowExecutionKernel.HandleStartWorkflowAsync
                              seed 非空：Variables←seed.variables
                              起跑步：_workflow.GetStep(start_at_step_id)  (非 first)
                                 │
                                 ▼  正常 self-continuation 跑后续步
```

**唯一原语，两个入口；新 run 经 bind 拿 seed，内核从指定 step 起跑。**

## 5. 核心数据结构（新增 proto）

新增于 workflow proto（state 侧入 `workflow_state.proto`；事件/命令载荷入 `workflow_execution_messages.proto`）：

```proto
// 跨 actor 传给新 run 的恢复种子（随 bind/start 流转）
message WorkflowRunForkSeed {
  string source_run_id = 1;          // 取 seed 的来源（lineage/审计）
  string start_at_step_id = 2;       // 新 run 从哪一步起跑
  map<string, string> variables = 3; // 注入 kernel 的初始 variables（含各完成步输出）
}

// Unit 1 query 返回的只读 seed 契约（read-side，非 state 原样 dump）
message WorkflowRunForkSeedView {
  string run_id = 1;
  string status = 2;                 // failed/completed/stopped
  string workflow_yaml = 3;          // 旧 run 绑定的 YAML（自动重试复用它）
  map<string, string> inline_workflow_yamls = 4;
  map<string, string> variables = 5; // 各完成步输出
  repeated string completed_step_ids = 6;
  string last_failed_step_id = 7;
  string final_error = 8;
}

// 自动 driver：失败 run 推进的 committed 事件
message WorkflowRunForkRequestedEvent {
  string source_run_id = 1;
  string start_at_step_id = 2;       // = 失败步
  int32 attempt = 3;                 // 第几次自动 fork（max_attempts 兜底）
  string scope_id = 4;
}
```

`StartWorkflowEvent`（或 run bind 事件）增加 optional `WorkflowRunForkSeed fork_seed`；`WorkflowDefinitionBinding`（应用层 record）增加 optional `WorkflowRunForkSeed? ForkSeed`。

## 6. Unit 1 — 中间状态可读（需求1）

中间状态已落盘，只需补**面向读侧的 seed 查询契约**，让 application 层不侧读 event store 就能取 seed。

- 新端口 `IWorkflowRunForkSeedQueryPort.GetForkSeedAsync(runId) → WorkflowRunForkSeedView?`。
- 由 `WorkflowRunGAgent` 暴露（它是权威拥有者）：内部解 `execution_states` 里的 `WorkflowExecutionKernelState`，映射出 read-side `WorkflowRunForkSeedView`（强类型契约，非 state 原样 dump）。✅ 符合「状态镜像契约面向查询」。
- `completed_step_ids` 由 `variables` 的 key 集合（排除 `input` 等保留键）+ `current_step_id` 推出；`last_failed_step_id` = 终态失败时的 `current_step_id`。

## 7. Unit 2 — Fork 命令 + seed 传输（需求2）

应用层命令：

```csharp
record WorkflowForkRunCommand(
  string SourceRunId,                 // 取 seed / 取 base YAML
  string StartAtStepId,
  string? InlineYaml,                 // 给=用改后的；省略=复用 source YAML
  IReadOnlyDictionary<string,string>? InlineSubYamls,
  IReadOnlyDictionary<string,string>? VariableOverrides,  // 自动为主+覆盖
  string? Input,
  string? CommandId,
  string? CorrelationId);
```

`WorkflowForkRunService` 流程：

1. 经 `IWorkflowRunForkSeedQueryPort` 读 `SourceRunId` → `WorkflowRunForkSeedView`；校验 source 为终态（`failed`/`completed`/`stopped`），否则结构化报错（不 fork 活 run）。
2. 选定 YAML = `InlineYaml ?? view.WorkflowYaml`（sub-yaml 同理）。
3. seed variables = `view.variables` 合并 `VariableOverrides`（覆盖优先）。
4. 校验：选定 YAML 能 `WorkflowParser.Parse` + `WorkflowValidator.Validate`；`StartAtStepId` 在该 YAML 的 steps 里存在——否则 loud fail（结构化 start error，含可读原因）。
5. `WorkflowRunActorPort.CreateRunAsync(binding)`，`binding.ForkSeed = {source_run_id, start_at_step_id, variables}`；`CreateWorkflowRunBindEnvelope` 把 seed 一并放进 bind envelope（Protobuf）。

ACK 诚实：返回 `accepted + 新 run id`，只承诺新 run inbox admission（对齐现有 resume 端点措辞）。

## 8. Unit 3 — 内核从指定 step 起跑（需求3）

插桩点 `HandleStartWorkflowAsync`（已读，:82-148），最小改动：

- bind→start 流转携带 `fork_seed`（经 run bind 状态保存，start 时读出放进 `StartWorkflowEvent.fork_seed`）。
- start handler 在 :117 `Variables.Clear()` 之后：若 `fork_seed` 非空，`foreach seed.variables → state.Variables[k]=v`（仍保留 :125 `input` / :127 start params 合并语义，覆盖顺序：seed → input → start params，或按实现确定并测试固定）。
- 起跑步：`fork_seed` 非空时 `entry = _workflow.Steps.FirstOrDefault(s => s.Id == seed.start_at_step_id)`，为空则 loud fail（`WorkflowCompletedEvent.Success=false`，error 指明 step 不存在）；否则保持 `FirstOrDefault()`。
- start step 的 input：优先 `seed.variables["input"]`（旧 run 失败步的入参语义）→ 退回 `evt.Input`；其余 step 参数仍按 `state.Variables` 做表达式求值，故 seed 注入后下游引用可解析。

**诚实命名**：新增 `fork_seed` 字段/路径，不碰 `WorkflowResumedEvent`。

## 9. 驱动 A：人工 fork（HTTP）

- 新端点 `POST /api/workflow/runs/fork`（workflow capability API 内），body → `WorkflowForkRunCommand`。
- 经 `ICommandDispatchService` 走 Unit 2，返回 202 + 新 run id + `Location: .../workflow-actors/{newRunId}/current-state`。
- 典型用法：人看失败 run 的 `final_error` 与 `last_failed_step_id`（Unit 1 query）→ 改 YAML → 带 `source_run_id + start_at_step_id + inline_yaml` 调用。

## 10. 驱动 B：自动同-YAML 重试（committed 事件 + coordinator）

- **策略来源（新增）**：workflow YAML 顶层 run 级 `on_failure`，区别于 step 级 retry/on_error：
  ```yaml
  on_failure: { action: fork_from_failed_step, max_attempts: 3 }
  ```
  经 `WorkflowParser` 解析进 `WorkflowDefinition`（core 视为 run 级元信息）。
- **推进**：`WorkflowRunGAgent` 在事件处理流程内置 run 终态 `failed` 后，按策略 + `attempt < max_attempts` 判定 → 发 **committed** `WorkflowRunForkRequestedEvent {source_run_id, start_at_step_id=失败步, attempt+1, scope_id}`。回调线程不直接建 run；推进在 actor 事件处理内（符合「业务推进内聚 / 回调只发信号」）。
- **消费**：专用 `WorkflowRunForkCoordinator` 消费该 committed 事件 → 调 Unit 2 fork 原语（`InlineYaml` 省略=复用 source YAML、`start_at_step_id`=失败步、自动 seed）。不依赖持久定义 GAgent，覆盖 inline run。
- **兜底**：`max_attempts` 防风暴；`attempt` 随 fork 链递增并写入新 run lineage。

## 11. 数据流（两条序列）

人工 fork：
```
operator ─query─► IWorkflowRunForkSeedQueryPort.GetForkSeed(oldRun)  // 看失败步/错误
operator ─edit──► YAML
operator ─POST──► /runs/fork {source_run_id, start_at_step_id, inline_yaml}
  → WorkflowForkRunService: query seed → merge overrides → validate
  → WorkflowRunActorPort.CreateRunAsync(binding+seed)
  → 新 WorkflowRunGAgent bind(seed) → StartWorkflowEvent(seed)
  → kernel: Variables←seed → 起跑 start_at_step → self-continuation 跑完
  ◄─ 202 + newRunId
```

自动重试：
```
旧 run step 失败 → 内核终态 → WorkflowRunGAgent 置 failed
  → (策略 fork_from_failed_step 且 attempt<max) publish committed WorkflowRunForkRequestedEvent
  → WorkflowRunForkCoordinator 消费
  → WorkflowForkRunService(inline_yaml=null ⇒ 复用 source YAML, start=失败步, 自动 seed)
  → CreateRunAsync(binding+seed) → 新 run 从失败步起跑
```

## 12. 语义边界（写进 spec，先对齐认知）

- start step **之前**的步骤不重放，副作用不重复（用 seed 跳过）。
- start step **本身重跑**：若失败步之前已产生部分外部副作用（如已发一半 Lark 消息 / 已调外部 API），重跑会再做一次——「从失败步恢复」的固有语义，文档显式声明。
- step 身份按 `id`：改 YAML 时 start step 被改名/删除 → 校验报错；seed 变量按旧 step_id 注入，下游引用被改名 id 取不到值 → 调用方负责保持一致。**不建自动映射引擎**。
- 只 fork 终态 run，不 fork 活 run。
- lineage：新 run 记 `source_run_id`，fork 链可追溯。
- 查询诚实：`WorkflowRunForkSeedView` 暴露 source `status`/`final_error`，不在弱读上假装强一致。

## 13. 新增 / 修改清单（实现期细化）

新增：
- proto：`WorkflowRunForkSeed`、`WorkflowRunForkSeedView`、`WorkflowRunForkRequestedEvent`；`StartWorkflowEvent` 加 `fork_seed`。
- `IWorkflowRunForkSeedQueryPort` + `WorkflowRunGAgent` 实现（Unit 1）。
- `WorkflowForkRunCommand` + `WorkflowForkRunService` + envelope factory + target resolver（Unit 2）。
- `WorkflowRunForkCoordinator`（驱动 B 消费方）。
- `/api/workflow/runs/fork` 端点（驱动 A）。

修改：
- `WorkflowExecutionKernel.HandleStartWorkflowAsync`（Unit 3，seed hydrate + start step 选择）。
- `WorkflowDefinitionBinding` 加 `ForkSeed?`；`WorkflowRunActorPort.CreateRunAsync` / `CreateWorkflowRunBindEnvelope` 透传 seed。
- `WorkflowRunGAgent` bind 处理存 seed；failed 终态按 `on_failure` 策略发 fork-requested 事件。
- `WorkflowParser`/`WorkflowDefinition` 解析 run 级 `on_failure`。
- DI 注册（service / port / coordinator）。

## 14. 测试计划（行为变更必须补测试）

- 内核：start-from-step + seed → 派发 start_at_step、`Variables` 已 hydrate、step<X 不派发；start step 不存在 → loud fail。
- Unit 1：query 返回 seed view（variables/completed/last_failed/error 正确映射）。
- Unit 2：query seed → override 合并优先级；YAML compile/start-step 校验失败路径；`CreateRunAsync` 收到带 seed 的 binding。
- 驱动 A：端点 dispatch 命令 + 202 + Location。
- 驱动 B：failed + 策略 → 发 fork-requested 事件；无策略 → 不发；`attempt>=max_attempts` → 不再 fork；coordinator 消费 → 建新 run。
- self-send/continuation：经 publisher 驱动（`SendToAsync`/`PublishAsync`）并断言副作用，不直接调 handler。
- guards：`bash tools/ci/architecture_guards.sh` + `bash tools/ci/test_stability_guards.sh`（从 worktree 相对路径跑）。

## 15. 非目标 / YAGNI

- 不做 step id 自动映射 / 变量重命名引擎。
- 不做"原地恢复同一 run actor"（一律 fork 新 run）。
- 不复用 / 不扩展 human-approval suspension resume。
- 不做跨 run 的全量 timeline/audit readmodel（lineage 仅记 `source_run_id`）。
- 不在 query 路径做 event replay / 重建 state mirror（只读已落盘的 `execution_states`）。

## 16. 分阶段实施（供 writing-plans 拆解）

1. **proto + Unit 1**：新增 proto 消息 + `IWorkflowRunForkSeedQueryPort` + `WorkflowRunGAgent` seed view（需求1 闭环、可独立测）。
2. **Unit 3**：内核 start-from-step + seed hydrate（核心执行能力）。
3. **Unit 2**：fork command/service + binding/bind envelope 透传 seed（打通建 run）。
4. **驱动 A**：HTTP 端点（人工 fork 端到端）。
5. **驱动 B**：`on_failure` 解析 + 失败发事件 + coordinator（自动重试）。

每阶段独立 build/test/guard 通过再进下一阶段。

## 17. 风险与开放问题

- **bind→start 的 seed 流转细节**：需确认 run bind 后 `StartWorkflowEvent` 的发起处，把 seed 从 bind 状态带到 start 事件（实现期读 `WorkflowRunGAgent` bind/start handler 确认精确接缝）。
- **`variables` 覆盖顺序**：seed vs `input` vs start params 的优先级需在 Unit 3 实现期固定并加测试。
- **on_error 跳转后失败的 start step 选择**：自动重试取"内核终态时的 `current_step_id`"作失败步；需确认 on_error 链路下该值语义符合预期。
- **scope/credential 继承**：新 fork run 的 `scope_id` 与 caller credential 应继承自 source run（经 seed/ binding 传递），实现期对齐 `WorkflowRunExecutionContextState`。
