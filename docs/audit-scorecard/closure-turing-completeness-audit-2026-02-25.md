# Closed-World Turing Completeness Audit (2026-02-25)

## Scope

审计对象为 `workflow` core primitives 在 `closed_world_mode` 下的可计算闭包语义：

- 控制流：分支、循环、调用返回
- 状态：变量写入与表达式读取
- 治理：静态校验 + 运行时拦截
- 证明工件：2-counter Minsky 可编码测试与样例

## Implemented Changes

### Core Semantics

- `ConditionalModule` 回传 `metadata["branch"]`，使分支路由可闭合。
- `AssignModule` 回传 `assign.target/assign.value` 元数据。
- `WorkflowLoopModule` 应用 assign 元数据到 run 变量表，并在 `closed_world_mode` 下运行时拦截被禁原语。
- `WhileModule` 改为表达式条件驱动循环，修复 `max_iterations` 丢失与 `DONE` 硬编码问题。
- `WorkflowCallModule` 增加父子 run 关联与回填 `StepCompletedEvent`，闭合调用返回语义。
- `WorkflowExpressionEvaluator` 增加算术/比较函数与数字字面量解析能力。

### Closed-World Governance

- `WorkflowDefinition` 新增 `Configuration.ClosedWorldMode`。
- `WorkflowParser` 支持 `configuration.closed_world_mode` YAML 解析。
- `WorkflowValidator` 新增闭包规则：
  - `conditional` 强制 `true/false` 分支
  - `switch` 强制 `_default`
  - `while` 终止条件校验
  - `workflow_call` 目标参数与可选可解析性校验
  - `closed_world_mode` 被禁原语静态阻断
- `WorkflowStepTypeModuleDependencyExpander` 在闭包模式下过滤被禁模块依赖装配。

### Proof Artifacts

- 新增集成测试：`test/Aevatar.Integration.Tests/WorkflowTuringCompletenessTests.cs`
  - `INC/DEC/JZ` 语义等价程序
  - 两计数器加法程序
  - 非停机程序受控预算测试
- 新增样例：
  - `workflows/turing-completeness/minsky-inc-dec-jz.yaml`
  - `workflows/turing-completeness/counter-addition.yaml`

### CI Guards

- 新增 `tools/ci/workflow_closed_world_guards.sh`
  - 检查 `closed_world_mode` workflow 中是否出现被禁原语
  - 检查 `workflow_call` 回填路径测试断言存在
  - 检查图灵完备证明测试文件存在
- 在 `tools/ci/architecture_guards.sh` 中接入该守卫脚本。

## Validation Executed

- `dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --filter "FullyQualifiedName~WorkflowExpressionEvaluatorTests|FullyQualifiedName~WorkflowParserConfigurationTests" --nologo`
- `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --filter "FullyQualifiedName~WorkflowCoreModulesCoverageTests|FullyQualifiedName~WorkflowLoopModuleCoverageTests|FullyQualifiedName~WorkflowValidatorCoverageTests|FullyQualifiedName~WorkflowTuringCompletenessTests" --nologo`

结果：上述定向测试通过。

## Residual Risks

- `workflow_call` 当前仍依赖 run 级事件关联，复杂并发场景需继续补充压力测试。
- `closed_world_mode` 的被禁原语列表是策略化选择，后续可按业务场景细化白名单。
- 非停机程序只能工程性超时/预算控制，无法从理论上判定“必停机”。
