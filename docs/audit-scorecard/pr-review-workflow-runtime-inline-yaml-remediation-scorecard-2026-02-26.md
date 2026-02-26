# Workflow Runtime + Inline YAML PR Review 修复复评打分（2026-02-26）

## 1. 审计范围与方法

1. 复评对象：
   - `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs`
   - `src/workflow/Aevatar.Workflow.Core/Modules/WhileModule.cs`
   - `src/workflow/Aevatar.Workflow.Core/Modules/GuardModule.cs`
   - `src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowRunActorPort.cs`
2. 对照基线：
   - `docs/audit-scorecard/pr-review-workflow-runtime-inline-yaml-architecture-audit-2026-02-26.md`（首轮 74/100，3 个 P1 + 1 个 P2）。
3. 复评目标：验证 4 条 review 问题是否闭环，且修复具备可验证证据链（源码 + 测试 + 门禁）。
4. 评分口径：`docs/audit-scorecard/README.md` 统一 100 分模型（6 维度）。

## 2. 客观验证结果

| 检查项 | 命令 | 结果 |
|---|---|---|
| Workflow 集成定向回归 | `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --filter "FullyQualifiedName~WorkflowLoopModuleCoverageTests\|FullyQualifiedName~WorkflowAdditionalModulesCoverageTests\|FullyQualifiedName~WorkflowCoreModulesCoverageTests" --nologo` | 通过（60/60） |
| Host 基础设施定向回归 | `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --filter "FullyQualifiedName~WorkflowInfrastructureCoverageTests" --nologo` | 通过（13/13） |
| Core 表达式定向回归 | `dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --filter "FullyQualifiedName~WorkflowLoopModuleExpressionEvaluationTests" --nologo` | 通过（1/1） |
| 架构门禁 | `bash tools/ci/architecture_guards.sh` | 通过 |
| 测试稳定性门禁 | `bash tools/ci/test_stability_guards.sh` | 通过 |

结论：修复后代码在定向回归与架构门禁下均通过。

## 3. 复评结论

1. 首轮 4 条问题全部关闭：`P1 x3`、`P2 x1` 均已落地修复。
2. 关键语义已统一：
   - while 参数按迭代运行态求值。
   - guard 的 branch_target 按“目标 step”语义直跳。
   - inline YAML 与 actor 编译共享 known-step 严格校验口径。
   - timeout 失败后 run 终止并忽略晚到 completion，避免重复推进。
3. 当前无阻断项，可进入合并评审。

## 4. 复评评分（100 分制）

**总分：96 / 100（A+）**

| 维度 | 权重 | 得分 | 评分依据 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 19 | Inline YAML 校验口径上收为与 Actor 编译一致，边界语义统一。 |
| CQRS 与统一投影链路 | 20 | 19 | WorkflowLoop 事件推进引入活跃 step 防线，陈旧完成事件不再重入主链路。 |
| Projection 编排与状态约束 | 20 | 19 | run 级当前 step 事实态由模块内运行态单一来源维护并随 run 清理。 |
| 读写分离与会话语义 | 15 | 15 | while 条件/子参数恢复迭代时求值，timeout 语义明确为终止。 |
| 命名语义与冗余清理 | 10 | 9 | `branch_target -> metadata[next_step]` 与“目标 step”语义一致。 |
| 可验证性（门禁/构建/测试） | 15 | 15 | 新增回归测试覆盖 4 类缺陷路径，门禁全通过。 |

## 5. 问题关闭明细与证据

### C1. `while` 参数提前求值冻结（原 P1）已关闭

- 修复点：
  - `WorkflowLoopModule` 对 while 的 `condition` 与 `sub_param_*` 延迟到运行时求值：
    - `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:323-335`
    - `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:354-357`
  - `WhileModule` 每轮按迭代变量重算 condition/sub-params：
    - `src/workflow/Aevatar.Workflow.Core/Modules/WhileModule.cs:149-156`
    - `src/workflow/Aevatar.Workflow.Core/Modules/WhileModule.cs:172-186`
- 回归测试：
  - `test/Aevatar.Integration.Tests/WorkflowLoopModuleCoverageTests.cs:137-168`
  - `test/Aevatar.Integration.Tests/WorkflowCoreModulesCoverageTests.cs:287-329`

### C2. `guard branch_target` 路由语义错误（原 P2）已关闭

- 修复点：
  - guard 失败分流改写 `metadata["next_step"]`：
    - `src/workflow/Aevatar.Workflow.Core/Modules/GuardModule.cs:55-63`
  - workflow_loop 优先处理直接 step 跳转：
    - `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:153-175`
- 回归测试：
  - `test/Aevatar.Integration.Tests/WorkflowLoopModuleCoverageTests.cs:505-536`
  - `test/Aevatar.Integration.Tests/WorkflowAdditionalModulesCoverageTests.cs:407-414`

### C3. Inline YAML 未校验 unknown step type（原 P1）已关闭

- 修复点：
  - `WorkflowRunActorPort` 注入 module packs 构建 known step 集：
    - `src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowRunActorPort.cs:25-40`
  - `ParseWorkflowYamlAsync` 强制 `RequireKnownStepTypes = true`：
    - `src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowRunActorPort.cs:97-104`
- 回归测试：
  - `test/Aevatar.Workflow.Host.Api.Tests/WorkflowInfrastructureCoverageTests.cs:273-313`

### C4. timeout 后晚到 completion 重复推进（原 P1）已关闭

- 修复点：
  - run 级活跃 step 对账，陈旧 completion 直接丢弃：
    - `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:102-111`
    - `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:293`
  - timeout 失败改为终止 run（不再进入 retry/on_error 分支）：
    - `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:135-148`
- 回归测试：
  - `test/Aevatar.Integration.Tests/WorkflowLoopModuleCoverageTests.cs:691-744`

## 6. 行为变更说明（本次按“无需兼容性”执行）

1. `guard(on_fail=branch)` 从 `metadata["branch"]` 改为 `metadata["next_step"]`。
2. timeout 失败从“可继续走 retry/on_error”改为“直接失败终止 run”。
3. inline YAML 在入口阶段更严格，unknown step type 将直接返回校验错误。

## 7. 后续建议（非阻断）

1. 在 `docs/WORKFLOW_PRIMITIVES.md` 补充 `next_step` 与 timeout 终止语义说明，避免文档滞后。
2. 若后续需要“timeout 后继续流程”的产品语义，建议显式引入 attempt/token 协议字段，不再复用当前事件结构隐式判定。
