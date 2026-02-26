# Workflow Closed-World Turing Completeness

## Goal

将 `workflow` 的执行语义收敛为“闭包可计算子集”，在不依赖 `tool_call`/`connector_call`/LLM 的前提下，具备可证明的图灵完备表达力。

本次采用的证明路径是 **2-counter Minsky machine**（两计数器机）可编码性。

## Closed-World Primitive Subset

启用 `configuration.closed_world_mode: true` 后，允许用于证明的核心原语子集：

- `assign`（状态更新）
- `conditional` / `switch`（条件跳转）
- `while`（受表达式驱动的循环）
- `workflow_call`（可返回的调用组合）
- `transform` / `checkpoint` / `delay`（辅助）

禁止外部能力或人机交互依赖原语（如 `llm_call`、`tool_call`、`connector_call` 等）。

## Runtime And Validator Contracts

### 1. 表达式与算术能力

- 在 `WorkflowExpressionEvaluator` 中补齐 `add/sub/mul/div/eq/lt/lte/gt/gte`。
- 数字字面量与布尔字面量可以直接求值，保障表达式可编程性。

### 2. 分支语义闭合

- `ConditionalModule` 必须回传 `metadata["branch"]`。
- `WorkflowLoopModule` 以 `branch` 决定 `GetNextStep(current, branchKey)`。

### 3. 状态语义闭合

- `AssignModule` 通过 `assign.target/assign.value` 元数据声明变量写入。
- `WorkflowLoopModule` 在 run 变量表 `_variablesByRunId` 应用赋值，保证后续表达式可见。

### 4. 循环语义通用化

- `WhileModule` 去除 `DONE` 硬编码终止，改为 `condition` 表达式求值。
- `max_iterations` 与迭代计数按 `runId + stepId` 持久跟踪，避免参数丢失。

### 5. 调用语义闭合

- `WorkflowCallModule` 在子工作流完成后，回填父步骤 `StepCompletedEvent`。
- 父子关联通过 `child_run_id` 映射，防止 fire-and-forget 语义导致主流程悬挂。

### 6. 闭包模式治理

- `WorkflowDefinition`/`WorkflowParser` 新增 `configuration.closed_world_mode`。
- `WorkflowValidator` 在闭包模式下执行额外规则：
  - `conditional` 必须配置 `true/false` 分支。
  - `while` 必须具备可解析终止条件（`condition` 或合法 `max_iterations`）。
  - `workflow_call` 必须提供目标 `workflow`，并可选校验目标可解析。
  - 禁止闭包模式下使用被封禁原语。

## Control Flow Model

```mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
flowchart TD
    startRun["StartWorkflowEvent"] --> loopDispatch["WorkflowLoop DispatchStep"]
    loopDispatch --> stepReq["StepRequestEvent"]
    stepReq --> primitiveExec["Closed-World Primitive"]
    primitiveExec --> stepDone["StepCompletedEvent (with branch/assign metadata)"]
    stepDone --> routeNext["GetNextStep(current, branchKey)"]
    routeNext -->| "has next" | loopDispatch
    routeNext -->| "no next" | workflowDone["WorkflowCompletedEvent"]
```

## Minsky Encoding Mapping

- `INC(c)`：`assign target=c value=${add(variables.c, 1)}`
- `DEC(c)`：`assign target=c value=${sub(variables.c, 1)}`
- `JZ(c, L1, L2)`：`conditional condition=${eq(variables.c, '0')} branches.true=L1 branches.false=L2`
- 程序计数器通过 `next/branches` 图跳转表示。

仓库中的证明工件：

- `workflows/turing-completeness/minsky-inc-dec-jz.yaml`
- `workflows/turing-completeness/counter-addition.yaml`
- `test/Aevatar.Integration.Tests/WorkflowTuringCompletenessTests.cs`

## Limits

- 本文给出的是“可编码性证明路径”，不是性能最优路径。
- 非停机程序在工程上通过执行预算/超时进行受控终止，不尝试求解停机判定。
