---
title: "Workflow 调度 Actor 化 & 多智能体协作演进方案"
status: active
owner: eanzhao
---

# Workflow 调度 Actor 化 & 多智能体协作演进方案

> 日期：2026-04-01
> 状态：RFC（Request for Comments）
> 评审：Claude Opus 架构分析 + Codex 独立交叉评审

---

## 一、背景与动机

### 1.1 当前瓶颈

Aevatar Workflow 已大量使用 Actor 模型（`WorkflowRunGAgent` 管理 run 生命周期，子工作流通过 `IActorDispatchPort` spawn 独立 actor），但存在一个核心瓶颈：

**单个 `WorkflowRunGAgent` 串行化所有 step 执行。** 即使 `ParallelFanOutModule`、`ForEachModule`、`MapReduceModule` 在语义上是并行的，实际子步骤全部排入同一个 kernel 事件队列顺序处理。

### 1.2 参考系：Claude Code 多智能体架构

Claude Code 的多智能体系统是一个 **file-based actor system**——无共享内存、mailbox 消息传递、结构化协议、原子任务认领。这些模式天然映射到 actor 模型，且经过大规模生产验证。本方案将其中经验证的协调模式引入 Aevatar。

### 1.3 评审方法

每个方向经过两轮独立评审：
1. **Claude Opus**：从 Aevatar 架构约束和 Claude Code 源码出发，识别候选方向
2. **Codex**：对每个候选方向做独立价值/风险评估，补充遗漏方向

两轮评审结论高度一致的方向进入实施路线图；存在分歧的方向标注争议点。

---

## 二、实施路线图（按优先级排序）

### Phase 1：多智能体协作基础原语 [P0]

> 目标：建立 agent 间协调的核心基础设施，可独立于 workflow 调度使用。

#### 1-A. TaskBoard Actor — 多智能体任务协调

**评级：HIGH VALUE** | **来源：Claude Code task claiming 机制**

**问题：** Aevatar 缺少多智能体间的任务分配和协调原语。Claude Code 用 file lock + retry backoff 实现原子任务认领，边界问题多（stale lock、partial write、race condition on claim）。

**方案：** `TaskBoardGAgent` 作为任务状态的唯一权威源。Actor 单线程保证天然解决所有并发原子性问题。

```protobuf
message TaskBoardState {
  repeated TaskEntry tasks = 1;
  int32 high_water_mark = 2;  // 防止 ID 重用
}

message TaskEntry {
  string task_id = 1;
  string subject = 2;
  string description = 3;
  string owner = 4;           // agent actor id
  TaskStatus status = 5;      // pending | in_progress | completed | blocked | failed
  repeated string blocks = 6;
  repeated string blocked_by = 7;
}

enum TaskStatus {
  TASK_STATUS_PENDING = 0;
  TASK_STATUS_IN_PROGRESS = 1;
  TASK_STATUS_COMPLETED = 2;
  TASK_STATUS_BLOCKED = 3;
  TASK_STATUS_FAILED = 4;
}
```

**命令协议：**

| Command | Event（成功） | Event（失败） | 校验逻辑 |
|---------|-------------|-------------|---------|
| `CreateTaskCommand` | `TaskCreatedEvent` | — | 分配 high_water_mark++ 作为 ID |
| `ClaimTaskCommand` | `TaskClaimedEvent` | `TaskClaimRejectedEvent` | 任务存在、未被认领、依赖已满足、agent 未忙 |
| `CompleteTaskCommand` | `TaskCompletedEvent` | — | 自动解除下游任务的 blocked_by |
| `RequestWorkCommand` | `WorkAssignedEvent` | `NoWorkAvailableEvent` | 按 ID 序（低 → 高）分配首个可用任务 |

**扩展性考量（Codex 建议）：** 如果任务量极高，单 TaskBoardActor 成为瓶颈，按 project/workflow-run 分片。

---

#### 1-B. Actor Native Messaging — Agent 间通信

**评级：HIGH VALUE** | **来源：Claude Code file-based mailbox**

**问题：** Claude Code 用 file-based inbox + 1s polling 实现 agent 通信，有 stale lock 风险和秒级延迟。

**方案：** Actor 原生 inbox，typed protobuf messages。

```protobuf
// 通用 agent-to-agent 消息
message AgentMessage {
  string from = 1;
  string text = 2;
  google.protobuf.Timestamp timestamp = 3;
  string summary = 4;
}

// 结构化协议消息
message ShutdownRequest {
  string request_id = 1;
  string reason = 2;
}

message ShutdownResponse {
  string request_id = 1;
  bool approve = 2;
  string reason = 3;
}

message PlanApprovalResponse {
  string request_id = 1;
  bool approve = 2;
  string feedback = 3;
}
```

**对比：**

| 维度 | Claude Code (File) | Aevatar (Actor) |
|------|-------------------|-----------------|
| 投递延迟 | ~1s (polling) | < 1ms |
| 并发安全 | file lock + retry backoff | actor 单线程天然安全 |
| 可靠性 | stale lock 风险 | actor runtime 保证 |
| 广播 | 遍历写入所有 inbox 文件 | `TopologyAudience` 或 TeamManager 转发 |
| 可观察性 | 读文件 | projection pipeline |

---

#### 1-C. Team Registry Actor — 团队生命周期管理

**评级：HIGH VALUE** | **与 1-B 同批实施** | **来源：Claude Code team config file**

**方案：** `TeamManagerGAgent`，per-team 一个实例（不做全局 singleton，避免热点）。

```protobuf
message TeamState {
  string team_name = 1;
  string lead_agent_id = 2;
  repeated TeamMember members = 3;
  google.protobuf.Timestamp created_at = 4;
}

message TeamMember {
  string name = 1;
  string agent_id = 2;
  AgentStatus status = 3;
  string model = 4;
}

enum AgentStatus {
  AGENT_STATUS_IDLE = 0;
  AGENT_STATUS_BUSY = 1;
  AGENT_STATUS_SHUTDOWN = 2;
}
```

**命令协议：**

| Command | Event | 说明 |
|---------|-------|------|
| `RegisterMemberCommand` | `MemberRegisteredEvent` | Agent 启动时注册 |
| `UnregisterMemberCommand` | `MemberUnregisteredEvent` | Agent 关闭时注销 |
| `UpdateStatusCommand` | `MemberStatusUpdatedEvent` | idle/busy 切换 |
| `BroadcastMessageCommand` | — | 转发 `AgentMessage` 到所有活跃成员 |

---

#### 1-D. Backpressure & Admission Control — Worker 资源保护

**评级：HIGH VALUE** | **Phase 2 的前置条件**

**问题（Codex 补充）：** 当 1000 个 run 各 spawn 50 个 parallel worker 时，无保护机制会压垮系统。

**方案：**
- 每个 run 的最大并发 worker 数限制（配置项，默认 `max_concurrent_workers_per_run = 20`）
- 全局 worker actor 数量上限（运维级配置）
- 队列满时返回 `BackpressureEvent`，run actor 降级为串行执行
- 资源感知：可选接入 CPU/memory 水位指标

**设计原则：** Backpressure 信号通过事件传递，不引入中间层状态字典。

---

#### 1-E. Idempotent Step Execution — 幂等执行契约

**评级：HIGH VALUE** | **Phase 2 的前置条件**

**问题（Codex 补充）：** Worker actor crash 后 Orleans 可能重新激活，必须保证 side effect 幂等。

**方案：**
- 每个 step execution 分配唯一 `execution_id`（由 run actor 生成，传递给 worker）
- 外部调用（LLM/connector）通过 `execution_id` 做请求去重（tool provider 层）
- Step 结果只在 committed event 后才算完成
- 重复结果报告幂等：`StepCompletedEvent` 的 `execution_id` + `state_version` 做覆盖判定（旧不覆盖新）

```protobuf
message StepExecutionContext {
  string execution_id = 1;   // 唯一执行标识
  string run_id = 2;
  string step_id = 3;
  int64 state_version = 4;   // 权威版本，防止重复提交
}
```

---

### Phase 2：Workflow 调度优化 [P0-P1]

> 前置条件：Phase 1 的 backpressure（1-D）和幂等性（1-E）框架就绪。

#### 2-A. Parallel Worker Actor 化 — 核心调度瓶颈解除

**评级：HIGH VALUE** | **优先级：P0**

**问题：** `ParallelFanOutModule`/`ForEachModule`/`MapReduceModule` 子步骤排入父 actor 事件队列，无真正并行。

**方案：** 每个 worker 作为独立 actor 运行，父 run actor 只做编排和聚合。

```
WorkflowRunGAgent (编排者)
  │
  ├─ dispatch ParallelFanOutStep
  │   │
  │   ├─ spawn WorkerActor[0] → 独立执行 sub-step (LLM call / connector call)
  │   ├─ spawn WorkerActor[1] → 独立执行 sub-step
  │   └─ spawn WorkerActor[N] → 独立执行 sub-step
  │
  └─ WorkerActor 完成 → WorkerCompletedEvent(execution_id, result) 回报父 actor
      └─ 父 actor 聚合 → 全部完成后推进工作流
```

**关键设计约束：**
1. **Run actor 保持 state 唯一权威性**——workers 只报告结果，不直接修改 run 状态
2. **Worker actor 为 short-lived**——执行完毕即 deactivate
3. **每个 worker 独立超时/重试**——不影响兄弟 worker
4. **Worker spawn 受 1-D backpressure 控制**——超过阈值降级为串行
5. **Worker 携带 1-E execution context**——crash 重启幂等

**预期收益：**
- 真正的水平并行（N 个 worker 同时执行 LLM call）
- 故障隔离（单个 worker 失败不影响兄弟）
- 独立超时/取消（per-worker 粒度）

---

#### 2-B. Sub-Workflow 结构化错误协议

**评级：HIGH VALUE** | **优先级：P1**

**问题：** Sub-workflow 已经是独立 actor，但子 workflow crash 后父 workflow 的行为未定义。

**为什么不照搬 Erlang Supervision（Codex 关键洞察）：** Erlang supervisor 管理无状态进程，restart = 从零开始。Aevatar actor 有持久 state 和 event history，"restart" 语义完全不同。**需要的是错误协议，不是进程管理。**

**错误协议设计：**

```
1. 结构化错误传播
   子 actor 失败
     → SubWorkflowFailedEvent {
         run_id, step_id,
         error_type (timeout | crash | business_error),
         error_detail,
         is_retryable
       }
   父 actor 收到 → 进入错误处理分支

2. 可配置重试策略（per sub-workflow step definition）
   retry_policy:
     max_attempts: 3
     backoff: exponential
     backoff_base_ms: 1000

3. 熔断器语义
   连续 N 次失败 → CircuitOpenEvent → 快速失败后续请求
   冷却期后 → half-open → 试探性重试

4. 补偿协议（可选）
   子 workflow 失败且不可重试
     → CompensationRequestEvent
     → 父 workflow 执行定义中的 fallback 步骤
```

---

### Phase 3：协议完善 [P1]

#### 3-A. Typed Protocol + Continuation Timeout

**评级：MEDIUM VALUE**

**核心价值（Codex 观点）：** 不在编译期类型安全（协议消息很少变更），而在 **continuation-based timeout handling**。

```
Agent A: 发送 ShutdownRequest(request_id=X)
         注册 continuation: 等待 Response(request_id=X) 或 timeout
         timeout 事件化: ScheduleSelfDurableTimeout → ShutdownTimeoutFiredEvent
Agent B: 收到 request → 处理 → 发送 ShutdownResponse
Agent A: continuation 触发 → Response 到达则处理 / timeout 则 fallback
```

---

#### 3-B. Progress Tracking via Projection

**评级：MEDIUM VALUE**

Agent actor 发布 `ProgressEvent` 作为 committed event → 现有 projection pipeline 物化为 progress readmodel。

**约束：** 必须走统一 projection pipeline，不得构建独立的可观察性 sidecar。

---

#### 3-C. Agent Lifecycle 映射

**评级：MEDIUM VALUE**

**关键区分（Codex 建议）：** Orleans deactivation 是资源驱动的（idle timeout），不是语义驱动的（agent 完成工作）。

正确映射：

| 事件 | Actor 机制 | 说明 |
|------|-----------|------|
| Agent 启动 | `OnActivateAsync` | 注册到 TeamManager |
| Agent 完成任务 | **显式 `AgentCompletedEvent`** | 业务完成（不是 deactivation） |
| Agent 资源回收 | `OnDeactivateAsync` | best-effort 通知 leader，非业务语义 |

---

### Phase 4：条件性优化 [P2, 按需]

#### 4-A. Race Cancellation

**前置条件：** 2-A 完成，且性能分析证实 loser 分支有显著资源浪费。

**策略：** 先实现 best-effort cancel（发 cancel message，worker 检查时自行终止）。不追求强一致取消——cancel propagation 在 actor 系统中复杂度极高（cancel 在 completion 之后到达、部分取消、cancel 期间状态持久化等边界）。

---

#### 4-B. Peer-to-Peer Agent Communication

**前置条件：** 有具体的协作式多智能体场景需求。

**方案：** Agent A 通过 `TeamManagerActor` 做 name resolution，然后直接发送 typed message 给 Agent B。需要明确的 peer 通信协议，避免绕过 actor 边界约束。

---

#### 4-C. Agent Context & Memory Flow

**前置条件：** 大上下文场景（如完整对话历史跨 step 传递）出现。

**问题：** 当前 step 间上下文通过 workflow variables（string key-value）流转，大上下文不适合塞入 variables。

**备选方案：** step output 的 reference-based 传递（存储到 external store，传递 reference ID），或 `ContextActor` 持有 session-scoped 上下文。

---

## 三、明确否决的方向

以下方向经 Codex 评审明确否决，附否决理由和替代方案。

### Signal Broker Actor — OVER-ENGINEERED

**否决理由：** 单 actor 承担全系统信号路由 → 吞吐热点 + 单点故障。违反"单线程 actor 不做热点共享服务"。

**替代：** 如确需跨 workflow 信号，建模为 `SignalChannelActor`（per named channel），不是 god-broker。

### Cache Actor Pool — RISKY

**否决理由：** 跨 run 共享缓存破坏 event sourcing 确定性——replay 时缓存状态不同，结果不可重现。

**替代：** 跨 run 去重在 tool provider 层实现为幂等外部服务。

### Timeout Broker Actor — OVER-ENGINEERED

**否决理由：** Orleans 已有 reminder/timer 基础设施。自建 Timeout Broker 是重复造轮，且成为系统级热点。

**替代：** 在 `ScheduleSelfDurableTimeoutAsync` 调用点加 jitter。大规模 timer 是 Orleans 运维配置问题。

### Streaming MapReduce — LOW VALUE

**否决理由：** Map-Reduce barrier 是语义必要的——reduce 操作完整集合。Streaming 只适用于 associative + commutative reducer（sum/count/max），不具通用性。

**替代：** 如有具体场景，实现为特化 event module，不替换通用 MapReduce。

### Per-Step Execution Isolation — 延期

**否决理由：** 串行执行的真正痛点是并行步骤（由 2-A 解决）。对顺序步骤拆 actor 只增加协调开销。且 run state 分布式一致性引入新复杂度。

**重新考虑条件：** 有明确证据表明 step 级别故障隔离是必须的。

---

## 四、架构约束检查清单

每个方案实施前必须确认：

- [ ] **单一权威拥有者：** 新 actor 清晰拥有其管理的事实
- [ ] **不引入中间层状态：** 无 `Dictionary<>` / `ConcurrentDictionary<>` 事实态字段
- [ ] **读写分离：** 查询走 readmodel，不直读 actor 内部状态
- [ ] **事件化推进：** 业务推进在 actor 事件处理流程内完成
- [ ] **self continuation：** "下一拍继续"通过 self-message，非 inline dispatch
- [ ] **Protobuf 序列化：** 新 state/event 先定义 `.proto`
- [ ] **不热点化：** 新 actor 不承担无限扩张的共享吞吐
- [ ] **committed event 可观察：** 新 actor 的 committed event 进入 projection 主链
- [ ] **幂等执行：** 外部调用携带 execution_id，crash 重启不产生副作用
- [ ] **Backpressure：** actor spawn 受资源限制控制

---

## 五、参考

| 资料 | 路径 |
|------|------|
<<<<<<<< HEAD:docs/design/WORKFLOW_MULTIAGENT_EVOLUTION.md
| Workflow 原语文档 | `docs/guides/WORKFLOW_PRIMITIVES.md` |
| CQRS 架构 | `docs/architecture/CQRS_ARCHITECTURE.md` |
| Event Sourcing | `docs/architecture/EVENT_SOURCING.md` |
========
| Workflow 原语文档 | `docs/canon/workflow-primitives.md` |
| CQRS 架构 | `docs/canon/cqrs-projection.md` |
| Event Sourcing | `docs/canon/event-sourcing.md` |
>>>>>>>> c20fc87ec173e49be645ea287f4bb54ecd975935:docs/decisions/0006-multi-agent-evolution.md
| Claude Code 多智能体源码 | `~/Code/claude-code/src/utils/swarm/` |
| 架构约束 | `CLAUDE.md`（仓库根目录） |
