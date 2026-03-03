# PR Review 架构审计报告（Workflow Runtime + Inline YAML）- 2026-02-26

## 1. 审计范围与输入

1. 审计对象：Workflow 执行链路与 Inline YAML 入口校验。
2. 输入来源：本次 PR review 的 4 条问题（P1×3，P2×1）+ 代码复核证据。
3. 重点路径：
   - `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs`
   - `src/workflow/Aevatar.Workflow.Core/Modules/GuardModule.cs`
   - `src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowRunActorPort.cs`
4. 评分口径：`docs/audit-scorecard/README.md`（100 分制，6 维度）。

## 2. 客观验证结果

| 检查项 | 命令 | 结果 |
|---|---|---|
| 架构门禁 | `bash tools/ci/architecture_guards.sh` | 通过 |
| Workflow 定向测试 | `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --filter "FullyQualifiedName~WorkflowLoopModuleCoverageTests\|FullyQualifiedName~WorkflowAdditionalModulesCoverageTests\|FullyQualifiedName~WorkflowValidatorCoverageTests" --nologo` | 通过（46/46） |

结论：现有门禁与定向测试均通过，但未覆盖本次 4 个行为回归场景，存在“门禁绿灯但运行时语义错误”的测试盲区。

## 3. 审计结论（摘要）

1. 当前变更存在 3 个 P1 阻断问题与 1 个 P2 主要问题。
2. 3 个 P1 直接影响工作流运行正确性与 API 输入一致性，建议 **阻断合并**。
3. P2 会导致 `guard` 的失败分流与文档/参数语义不一致，建议同批修复，至少补兼容路由。

## 4. 总体评分（100 分制）

**总分：74 / 100（B，当前不建议合并）**

| 维度 | 权重 | 得分 | 扣分说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 17 | 入口校验与 Actor 编译校验口径不一致，边界语义分裂。 |
| CQRS 与统一投影链路 | 20 | 14 | runtime 事件处理存在超时后陈旧完成事件重入，链路幂等性不足。 |
| Projection 编排与状态约束 | 20 | 13 | step attempt 未建模，无法识别 timeout 后晚到结果。 |
| 读写分离与会话语义 | 15 | 11 | `while` 条件/子参数被启动时冻结，迭代语义失真。 |
| 命名语义与冗余清理 | 10 | 7 | `branch_target`（目标 step）与 `metadata[branch]`（分支 key）语义冲突。 |
| 可验证性（门禁/构建/测试） | 15 | 12 | 现有测试未命中 4 个回归路径，缺失针对性回归用例。 |

## 5. 问题分级清单

| ID | 级别 | 主题 | 结论 |
|---|---|---|---|
| F1 | P1 | `while` 表达式提前求值导致迭代语义冻结 | 阻断 |
| F2 | P2 | `guard.on_fail=branch` 的 `branch_target` 被当作 branch key 使用 | 主要 |
| F3 | P1 | Inline YAML 未开启 known-step 校验导致 API 接受无效工作流 | 阻断 |
| F4 | P1 | timeout 失败后未屏蔽晚到 completion，可能重复推进流程 | 阻断 |

## 6. 详细发现与证据链

### F1（P1）`while` 参数被 `WorkflowLoopModule` 启动时提前求值

**现象**

- `WorkflowLoopModule.DispatchStep` 对全部参数做一次 `_expressionEvaluator.Evaluate(...)` 后再下发 `while` 请求。
- `WhileModule` 设计上要求在每次迭代用最新变量评估 `condition`，并基于迭代状态运行。

**代码证据**

1. `WorkflowLoopModule` 启动阶段提前求值：
   - `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:281-286`
2. `WhileModule` 在迭代阶段评估条件：
   - `src/workflow/Aevatar.Workflow.Core/Modules/WhileModule.cs:149-160`
3. 文档已声明 while 支持迭代表达式：
   - `docs/WORKFLOW_PRIMITIVES.md:174-189`（示例 `condition: "${lt(iteration, 5)}"`）

**影响**

1. `condition` 与 `sub_param_*` 失去迭代上下文，循环退出/继续条件可能错误。
2. 依赖每轮输出更新的表达式工作流会表现为常量行为或无效字符串行为。

**修复建议（准入）**

1. `DispatchStep` 对 `stepType == while` 时，禁止对 `condition` 与 `sub_param_*` 做前置求值；保留原始表达式。
2. 如需动态参数，统一由 `WhileModule` 在 `DispatchIterationAsync` 依据迭代变量求值。
3. 增加回归测试：验证 `${lt(iteration, 3)}` 能执行 3 次且逐轮读取最新输出/变量。

---

### F2（P2）`branch_target`（step id）被写入 `metadata["branch"]`（branch key 语义）

**现象**

- `GuardModule` 在 `on_fail=branch` 时，将 `branch_target` 直接写入 `StepCompletedEvent.Metadata["branch"]`。
- `WorkflowLoopModule` 将该字段解释为 branch key，调用 `GetNextStep(currentId, branchKey)`。
- `GetNextStep` 会用 key 查 `StepDefinition.Branches[key]`，不是直接按 step id 跳转。

**代码证据**

1. guard 写入逻辑：
   - `src/workflow/Aevatar.Workflow.Core/Modules/GuardModule.cs:55-62`
2. loop 读取 branch key：
   - `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:141-143`
3. branch key 路由语义：
   - `src/workflow/Aevatar.Workflow.Core/Primitives/WorkflowDefinition.cs:60-73`
4. 文档/演示参数语义是“目标 step id”：
   - `demos/Aevatar.Demos.Workflow.Web/Program.cs:824`

**影响**

1. 当 `branch_target` 提供的是目标 step id 时，运行期可能回退到 `next` 或 `_default`，无法跳到预期恢复步骤。
2. 用户可见行为与参数语义不一致，排障难度高。

**修复建议（准入）**

1. 统一语义：新增显式字段（如 `metadata["next_step"]`）承载“直接 step 跳转”。
2. `WorkflowLoopModule` 优先处理直接 step 跳转，再处理 branch key 路由。
3. 增加集成测试：`guard(on_fail=branch, branch_target=<stepId>)` 必须命中目标步骤，而非 `_default/next`。

---

### F3（P1）Inline YAML 入口未执行 known-step 校验

**现象**

- API 入口解析 inline YAML 后调用 `WorkflowValidator.Validate(workflow)`（默认 options）。
- 默认 options 下 `RequireKnownStepTypes = false`，未知 `type` 可通过。
- Actor 编译侧使用 `RequireKnownStepTypes = true`，导致请求入口和运行时编译口径不一致。

**代码证据**

1. 入口校验（默认 options）：
   - `src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowRunActorPort.cs:88-90`
2. 默认 options 未强制 known-step：
   - `src/workflow/Aevatar.Workflow.Core/Validation/WorkflowValidator.cs:21-23`
   - `src/workflow/Aevatar.Workflow.Core/Validation/WorkflowValidator.cs:227`
3. Actor 侧强制 known-step：
   - `src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs:348-354`

**影响**

1. API 可能接受无效 YAML，随后在 actor 编译阶段失败，错误阶段后移。
2. 无法在请求时稳定返回 `INVALID_WORKFLOW_YAML`，破坏接口契约预期。

**修复建议（准入）**

1. `ParseWorkflowYamlAsync` 与 Actor 编译使用同一验证选项（`RequireKnownStepTypes=true` + 同源 `KnownStepTypes`）。
2. 校验逻辑抽到共享验证服务，避免两处实现漂移。
3. 增加 Host/API 测试：未知 step type 必须在入口返回 `INVALID_WORKFLOW_YAML`。

---

### F4（P1）timeout 触发失败后，晚到成功 completion 仍可推进流程

**现象**

- timeout 通过 `Task.Delay` 到期后发布一个失败 `StepCompletedEvent`。
- 当前实现未取消底层执行，也没有 step attempt/token 机制。
- 因此晚到 completion 仍会进入正常处理流程，可能二次推进或污染状态。

**代码证据**

1. timeout 合成失败 completion：
   - `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:322-330`
2. completion 处理路径无 attempt 校验：
   - `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:81-156`
3. 协议层缺少 step attempt 字段：
   - `src/workflow/Aevatar.Workflow.Abstractions/workflow_execution_messages.proto:8-9`

**影响**

1. timeout + retry/on_error 场景中，晚到成功可能与失败补偿并行生效，导致状态不一致。
2. 运行语义非确定，属于跨事件顺序竞争问题。

**修复建议（准入）**

1. 引入 step attempt 标识（协议字段或强约束 metadata），请求/完成事件必须同 attempt 对账。
2. `WorkflowLoopModule` 仅接受“当前活跃 attempt”的 completion，其他一律忽略并记录告警。
3. 增加回归测试：构造 timeout 后晚到 success，断言不会二次推进流程。

## 7. 测试覆盖缺口（本次问题为何未被现有测试拦截）

1. `guard` 测试只校验 metadata 写入，不校验 loop 路由是否命中目标步骤：
   - `test/Aevatar.Integration.Tests/WorkflowAdditionalModulesCoverageTests.cs:343-415`
2. timeout 测试只验证 timeout 事件会发出，不验证晚到 completion 被忽略：
   - `test/Aevatar.Integration.Tests/WorkflowLoopModuleCoverageTests.cs:473-502`
3. 当前未发现 `WorkflowRunActorPort.ParseWorkflowYamlAsync` 针对 unknown step type 的真实实现测试（Host API 侧更多是错误码映射与服务桩测试）：
   - `test/Aevatar.Workflow.Host.Api.Tests/ChatEndpointsInternalTests.cs:248-275`
   - `test/Aevatar.Workflow.Host.Api.Tests/WorkflowInfrastructureCoverageTests.cs:184-215`

## 8. 合并门禁（Blocking Exit Criteria）

以下 3 项为合并前必须满足：

1. 修复 F1：while 条件与子参数不再被启动时冻结，迭代语义正确。
2. 修复 F3：inline YAML 入口与 actor 编译对 unknown step type 保持同口径拒绝。
3. 修复 F4：timeout 后晚到 completion 不再推进流程（需要 attempt 级别一致性）。

建议同批完成（非阻断但高优先）：

1. 修复 F2：`branch_target` 语义与实际路由一致（直接 step 跳转或明确 branch key 约束）。

## 9. 建议回归测试矩阵

1. `WorkflowLoopModule_ShouldKeepWhileConditionExpressionForRuntimeEvaluation`。
2. `GuardBranchTarget_ShouldJumpToTargetStepId_NotFallbackToNext`。
3. `ParseWorkflowYamlAsync_UnknownStepType_ShouldReturnInvalidWorkflowYaml`。
4. `WorkflowLoopModule_ShouldIgnoreLateCompletionAfterTimeoutForOldAttempt`。

## 10. 审计结论

该 PR 的主要风险不是“编译失败”，而是“运行语义偏移与一致性破坏”。
在当前状态下，建议先完成 3 个 P1 阻断项修复并补齐针对性回归测试，再进入合并评审。
