# 2026-03-13 `fix/workflow-durable-boundaries-20260310` 相对 `dev` 审计评分卡

## 1. 审计范围与方法

- 审计对象：当前分支 `fix/workflow-durable-boundaries-20260310` 相对 `dev` 的增量。
- 差异规模：`1074 files changed, 108932 insertions(+), 17345 deletions(-)`。
- 审计方法：风险优先人工审查，重点下钻 `Workflow/Application/Projection/Host API` 的命令控制链路、accepted-only dispatch、projection lifecycle、resume/signal 控制命令，以及对应测试与门禁。
- `2026-03-13` 最终复评说明：本次已纳入 `resume/signal` 应用层校验修复，以及 detached dispatch / durable completion blocker 修复后的重新评分。
- 说明：由于分支跨度极大，本次评分仍是“关键主链路风险审计”，不是对 1074 个文件的逐行穷举式 review。

## 2. 客观验证结果

### 2.1 已执行命令

1. `bash tools/ci/architecture_guards.sh`
2. `dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --filter "DetachedCommandDispatchService|WorkflowRunControl" --nologo`
3. `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --filter "ReleaseActorProjectionAsync_ShouldStopReceivingNewEvents|HandleResume|HandleSignal" --nologo`

### 2.2 结果摘要

1. `architecture_guards.sh` 通过。
2. Workflow Application 定向测试通过，说明：
   - `resume/signal` 的应用层校验仍然有效；
   - accepted-only dispatch 已改为“先 detach live observation，再持续等待 durable terminal，最后才 release/destroy”；
   - 单次 durable observation 超时只会触发后台重试，不会提前 release projection。
3. Workflow Host API 定向测试通过，说明：
   - `ReleaseActorProjectionAsync()` 作为 projection runtime stop 语义本身没有被误改；
   - Host 侧 `resume/signal` 错误码映射仍与 Application 契约保持一致。

结论：此前审计里两个主要问题都已修复，且修复后的契约已被测试锁定。

## 3. 整体评分

**总分：88 / 100，等级：A-**

### 3.1 六维评分

| 维度 | 分数 | 说明 |
|---|---:|---|
| 分层与依赖反转 | 17 / 20 | Host / Application / Projection 责任边界仍然清晰，控制命令职责已进一步收敛。 |
| CQRS 与统一投影链路 | 17 / 20 | accepted-only dispatch 已不再提前 release projection，durable completion 与 projection cleanup 语义重新对齐。 |
| Projection 编排与状态约束 | 16 / 20 | `detach live observation` 与 `stop projection runtime` 已重新分清；仍保留较复杂的后台监控链路，运维层面需持续关注。 |
| 读写分离与会话语义 | 15 / 15 | ACK 语义比初审更诚实，`resume/signal` 的关键参数校验已落到 Application 命令层。 |
| 命名语义与冗余清理 | 9 / 10 | 命名与职责边界整体保持一致，重构方向正确。 |
| 可验证性 | 14 / 15 | guards 与定向测试覆盖到位，且关键错误语义测试已被改成正确契约。 |

## 4. 分模块评分

| 模块 | 分数 | 一句话结论 |
|---|---:|---|
| Workflow run control / application | 88 | control command 契约与 detached dispatch 收尾语义都已回到正确边界。 |
| CQRS / Projection lifecycle | 86 | release 原语语义保持清晰，调用方已不再把它误用成“仅断开实时观测”。 |
| Host / Capability API | 86 | Host 与 Application 命令契约保持一致，未再发现新的阻断项。 |
| Docs + Guards | 92 | 文档、门禁与定向测试都已同步到最新主链路语义。 |

## 5. 主要 Findings

### 5.1 [Resolved In Final Re-Review] accepted-only dispatch 不再在 durable completion 之前释放 projection

本次修复后：

1. `WorkflowRunDetachedDispatchService` 先 `DetachLiveObservationAsync()`，只在 durable completion 真正进入 terminal 后才调用最终 `ReleaseAsync(...)`。
2. detached 路径不再把 monitor timeout 当成 projection cleanup 的触发条件，因此不会再把仍在运行的 workflow 误写成 `Stopped`。
3. `DetachedCommandDispatchService_ShouldRetryMonitoring_WhenDurableCompletionObservationTimesOut` 已锁定新契约：单次 durable observation 超时后继续后台重试，直到真实 terminal 才 release/destroy。
4. `ReleaseActorProjectionAsync_ShouldStopReceivingNewEvents` 仍通过，证明 release 原语依旧保持“停止 projection runtime”的单一语义，没有被稀释成模糊的 detach 行为。

影响：此前的阻断级问题已经关闭，不再作为扣分项。

### 5.2 [Resolved In Re-Review] `resume/signal` 的关键参数校验已下沉到 Application 命令层

本次复评确认：

1. `WorkflowRunControlCommandTargetResolverBase` 已在通用解析骨架中调用命令级校验。
2. `WorkflowResumeCommandTargetResolver` 明确拒绝空 `StepId`。
3. `WorkflowSignalCommandTargetResolver` 明确拒绝空 `SignalName`。
4. Host 已映射新的应用层错误码，避免 HTTP 与 Application 再次分叉。

影响：此前的 P1 问题已关闭，不再作为扣分项。

## 6. 当前结论

- 在本次审计覆盖的主链路范围内，之前列出的 blocking 项已经全部修复。
- 当前没有剩余的 blocking finding 继续阻止该分支合并。
- 由于分支体量极大，仍建议在合并前按团队惯例补跑更大范围的 solution build/test；但这属于审计覆盖范围之外的常规工程风险控制，不属于本次已确认的阻断缺陷。

## 7. 审计结论

最终复评后，分支总分从初审的 `74`、中间复评的 `78`，上调到 `88`。提升的原因很明确：`resume/signal` 的应用层命令校验已修复，而 accepted-only dispatch 对 projection release / durable completion 的错误组合语义也已被改正，并由新的定向测试锁定。基于本次审计覆盖范围，当前可以给出“无剩余 blocking 项”的结论。
