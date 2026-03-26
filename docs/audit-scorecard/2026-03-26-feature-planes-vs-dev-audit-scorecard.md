# 2026-03-26 `feature/planes` vs `dev` Audit Scorecard

## 审计范围

- 基线分支：`dev`
- 复核目标：当前工作区相对初版审计结论的修复落地情况
- 复核重点：
  - `workflow_call` 子工作流 stop 终态回传
  - Studio / CLI `ResumeAsync` 等待态恢复后的状态收敛
  - 对应回归测试与门禁覆盖

## 修复后结论

本次复核未再发现阻断合入的 High 风险问题。此前审计关注的 `workflow_call` stop 传播链路已经具备对称处理；本次额外修复了 Studio `ResumeAsync` 在后续读取/列表重放时可能回落为 `waiting` 的状态回归问题，并补齐了针对性测试。

## 客观验证

| 命令 | 结果 |
|---|---|
| `dotnet build aevatar.slnx --nologo` | 通过 |
| `bash tools/ci/architecture_guards.sh` | 通过 |
| `bash tools/ci/test_stability_guards.sh` | 通过 |
| `bash tools/ci/workflow_binding_boundary_guard.sh` | 通过 |
| `dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo --filter "FullyQualifiedName~SubWorkflowOrchestratorTests"` | 19/19 通过 |
| `dotnet test test/Aevatar.Tools.Cli.Tests/Aevatar.Tools.Cli.Tests.csproj --nologo --filter "FullyQualifiedName~ExecutionServiceTests"` | 13/13 通过 |

说明：

1. 本次执行的是修复相关路径的定向验证，不是全量 `dotnet test aevatar.slnx --nologo`。
2. `dotnet build` 仍存在仓库内既有 warning，但没有新增错误或阻断门禁失败。

## 总分

**91 / 100（A-）**

## 六维评分

| 维度 | 分数 | 说明 |
|---|---:|---|
| 分层与依赖反转 | 18/20 | 本次修复保持在既有分层内完成，没有引入跨层回退 |
| CQRS 与统一投影链路 | 18/20 | 子工作流 stop 终态处理已形成 completion / stopped 对称面 |
| Projection 编排与状态约束 | 18/20 | 运行态仍由 actor / 持久记录承载，没有回退到进程内事实表 |
| 读写分离与会话语义 | 13/15 | `ResumeAsync` 的等待态回放语义已闭合，但仍缺少更大范围恢复场景的集成覆盖 |
| 命名语义与冗余清理 | 9/10 | 新增测试与实现命名直接对应问题域，语义清晰 |
| 可验证性（门禁/构建/测试） | 15/15 | build、关键 guard、定向回归测试都已覆盖并通过 |

## 本次修复摘要

### 1. Studio / CLI 恢复态回放修复

- 变更点：`src/Aevatar.Studio.Application/Studio/Services/ExecutionService.cs`
- 修复内容：
  - `TrackExecutionState(...)` 现在识别 `studio.human.resume` synthetic frame。
  - 当恢复帧带回 `stepId` 时，会把对应步骤从 `pendingHumanSteps` 中移除。
  - 这样 `GetAsync(...)` / `ListAsync(...)` 在基于已持久化 frame 重新收敛状态时，不会把已恢复的执行再次判回 `waiting`。

### 2. 子工作流 stop 传播回归测试补齐

- 变更点：`test/Aevatar.Workflow.Core.Tests/Primitives/SubWorkflowOrchestratorTests.cs`
- 覆盖内容：
  - `TryHandleStoppedAsync_WhenTransientChildStops_ShouldPublishParentFailureAndCleanup`
  - `TryHandleRunStoppedAsync_WhenChildActorIdMissing_ShouldPublishFailureWithoutCleanup`
- 作用：
  - 锁定 `workflow_call` 子运行 stop / run-stopped 路径的终态传播和清理行为，避免后续回退。

### 3. 恢复态状态收敛回归测试补齐

- 变更点：`test/Aevatar.Tools.Cli.Tests/ExecutionServiceTests.cs`
- 覆盖内容：
  - `ResumeAsync_WhenSyntheticResumeFrameIsPersisted_ShouldClearWaitingStateOnSubsequentReads`
- 作用：
  - 锁定“`waiting -> resume -> 后续读取仍保持 running`”这一修复语义。

## 剩余风险

1. 本次没有重跑全量测试，因此尚未用完整回归成本验证所有非相关模块。
2. `ResumeAsync` 当前修复的是本地持久记录重放语义；若后续需要覆盖更复杂的跨进程/断线恢复体验，仍建议补一个 Host 级集成场景。

## 结论

当前分支在本次修复后，之前审计中最实质的运行时缺口已经收敛，且具备对应的回归测试与门禁验证。基于现有证据，这次复核将评分更新为 **91/100**；若后续再补一轮全量回归和更完整的 resume 集成测试，评分还可以继续上调。
