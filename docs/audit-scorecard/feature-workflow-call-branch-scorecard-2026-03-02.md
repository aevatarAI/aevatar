# Feature Branch Audit Scorecard（`feature/workflow-call` vs `dev`）- 2026-03-02

## 1. 审计范围与方法

1. 基线与分支：
   - Base：`dev`
   - Target：`feature/workflow-call`（当前分支）
2. 定向范围（`workflow_call` 相关增量）：
   - `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowCallModule.cs`
   - `src/workflow/Aevatar.Workflow.Core/Primitives/SubWorkflowOrchestrator.cs`
   - `src/workflow/Aevatar.Workflow.Core/Primitives/WorkflowCallInvocationIdFactory.cs`
   - `src/workflow/Aevatar.Workflow.Core/Primitives/WorkflowCallLifecycle.cs`
   - `src/workflow/Aevatar.Workflow.Core/workflow_state.proto`
   - `src/workflow/Aevatar.Workflow.Abstractions/workflow_execution_messages.proto`
   - `test/Aevatar.Workflow.Core.Tests/Primitives/WorkflowCallLifecycleTests.cs`
   - `test/Aevatar.Integration.Tests/WorkflowCoreModulesCoverageTests.cs`
   - `test/Aevatar.Integration.Tests/WorkflowGAgentCoverageTests.cs`
3. 方法：
   - 静态审计：代码路径与状态迁移证据核查；
   - 动态验证：运行 `workflow_call` 相关单测/集成测试；
   - 评分口径：`docs/audit-scorecard/README.md`（100 分，6 维）。

## 2. 客观验证结果（命令与结果）

1. 变更定位：
   - `git diff --name-only dev...HEAD | rg -i "workflow[_-]?call|subworkflow|invoke"`
   - 命中 9 个 `workflow_call` 相关文件（核心代码 + proto + demo + tests）。
2. 定向回归测试（本次修复点）：
   - `dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo --filter "FullyQualifiedName~WorkflowCall|FullyQualifiedName~WorkflowParserConfigurationTests|FullyQualifiedName~WorkflowRunIdNormalizerTests"`
   - 结果：Passed `39/39`，Failed `0`。
   - `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --nologo --filter "FullyQualifiedName~HandleSubWorkflowInvokeRequested_WhenLifecycleInvalid_ShouldPublishFailureAndKeepPendingEmpty|FullyQualifiedName~HandleWorkflowCompletionEnvelope_WhenRunMatchesPendingButPublisherMismatch_ShouldIgnoreAndKeepPending|FullyQualifiedName~HandleWorkflowCompletionEnvelope_WhenPublisherMismatchThenValidCompletion_ShouldProcessOnlyValidCompletion|FullyQualifiedName~WorkflowStateProto_ShouldRoundtripPendingInvocationIndexes"`
   - 结果：Passed `8/8`，Failed `0`。
3. 严格验证（full verification）：
   - `bash tools/ci/architecture_guards.sh` ✅ Passed
   - `bash tools/ci/projection_route_mapping_guard.sh` ✅ Passed
   - `bash tools/ci/solution_split_guards.sh` ✅ Passed
   - `bash tools/ci/solution_split_test_guards.sh` ✅ Passed
   - `bash tools/ci/test_stability_guards.sh` ✅ Passed
   - `dotnet build aevatar.slnx --nologo` ✅ Passed（0 errors）
   - `dotnet test aevatar.slnx --nologo` ✅ Passed（全量测试通过，部分外部依赖用例按既有条件跳过）

## 3. 总体评分（100 分制）

**总分：100 / 100（A+）**

| 维度 | 权重 | 得分 | 扣分说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 20 | `WorkflowCallModule` 与 `SubWorkflowOrchestrator` 职责边界清晰，依赖方向正确。 |
| CQRS 与统一投影链路 | 20 | 20 | 子流程调用注册/完成均通过领域事件与状态迁移统一落地。 |
| Projection 编排与状态约束 | 20 | 20 | completion 关联加入 `run_id + publisher(child_actor)` 双条件；pending 索引进入持久态。 |
| 读写分离与会话语义 | 15 | 15 | `lifecycle` 白名单化并拒绝非法值，会话语义确定性增强。 |
| 命名语义与冗余清理 | 10 | 10 | 命名与元数据前缀语义一致，无新增冗余壳层。 |
| 可验证性（门禁/构建/测试） | 15 | 15 | full verification 全部通过，新增回归覆盖修复点。 |

## 4. 分模块评分（定向范围）

| 模块 | 分数 | 结论 |
|---|---:|---|
| Workflow Core（module + orchestrator） | 100 | 生命周期校验、completion 关联与索引化路径均已收敛。 |
| Workflow Abstractions（proto） | 100 | `WorkflowState` 增量索引字段与既有字段语义一致，兼容扩展。 |
| Tests（Core + Integration） | 100 | 非法 lifecycle、publisher mismatch、索引 roundtrip、批量清理路径均有回归。 |
| Demo Workflows | 100 | 文档语义与运行时校验口径一致。 |

## 5. 关键加分证据

1. `lifecycle` 白名单 + 双层拦截（validator + module）：
   - `src/workflow/Aevatar.Workflow.Core/Primitives/WorkflowCallLifecycle.cs:17`
   - `src/workflow/Aevatar.Workflow.Core/Validation/WorkflowValidator.cs:186`
   - `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowCallModule.cs:63`
2. 子流程 completion 强关联（`run_id + publisher(child_actor)`）：
   - `src/workflow/Aevatar.Workflow.Core/Primitives/SubWorkflowOrchestrator.cs:145`
3. pending 持久化索引（child-run 索引 + parent-run 反向索引）：
   - `src/workflow/Aevatar.Workflow.Core/workflow_state.proto:25`
   - `src/workflow/Aevatar.Workflow.Core/workflow_state.proto:42`
   - `src/workflow/Aevatar.Workflow.Core/Primitives/SubWorkflowOrchestrator.cs:593`
4. 回归测试覆盖新增行为：
   - `test/Aevatar.Integration.Tests/WorkflowCoreModulesCoverageTests.cs:524`
   - `test/Aevatar.Integration.Tests/WorkflowGAgentCoverageTests.cs:606`
   - `test/Aevatar.Integration.Tests/AgentYamlLoaderAndWorkflowStateCoverageTests.cs:192`

## 6. 扣分项关闭情况

1. **F1 已关闭**：非法 `lifecycle` 现在会在校验/执行阶段失败，不再静默回落。
2. **F2 已关闭**：completion 关联加入 publisher 一致性校验，mismatch 事件不会推进父流程。
3. **F3 已关闭**：pending 主路径读取/清理引入持久化索引，线性扫描不再是唯一路径。

## 7. 合并建议

1. 当前结论：**建议合并**。
2. 定向审计结果从 `93 -> 100`，主要扣分项均已消除并经 full verification 验证。

## 8. 非扣分观察项

1. 本次定向审计聚焦 `workflow_call` 相关增量，不等同于对整个分支 500+ 文件的全量架构审计。
2. `NU1507`（多包源）告警在现有基线中持续存在，但所有 guards/build/test 均为通过态，当前不作为扣分项。
