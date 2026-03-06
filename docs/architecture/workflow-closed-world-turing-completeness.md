# Workflow Closed-World Turing Completeness

## Goal

将 `workflow` 的执行语义收敛为闭包可计算子集，在不依赖 `tool_call` / `connector_call` / LLM / 人工交互的前提下，具备可证明的可计算表达力。

当前证明路径仍然是 **2-counter Minsky machine** 可编码性，但执行宿主已经从旧 loop module 模型切换为 `WorkflowRunGAgent`。

## Closed-World Primitive Subset

启用 `configuration.closed_world_mode: true` 后，允许用于证明的核心原语子集：

- `assign`
- `conditional`
- `switch`
- `while`
- `workflow_call`
- `transform`
- `checkpoint`
- `delay`

禁止外部能力或人机交互依赖原语：

- `llm_call`
- `tool_call`
- `connector_call`
- `evaluate`
- `reflect`
- `human_input`
- `human_approval`
- `wait_signal`
- `emit`
- `parallel`
- `foreach`
- `map_reduce`
- `race`
- `vote`
- `dynamic_workflow`

## Runtime And Validator Contracts

### 1. 表达式与算术能力

- `WorkflowExpressionEvaluator` 提供 `add/sub/mul/div/eq/lt/lte/gt/gte`。
- 数字字面量与布尔字面量可直接求值。

### 2. 分支语义闭合

- `ConditionalModule` 输出 `metadata["branch"]`。
- `WorkflowRunGAgent` 根据 `branch` 决定下一步跳转。

### 3. 状态语义闭合

- `AssignModule` 输出赋值元数据。
- `WorkflowRunGAgent` 将赋值应用到持久化变量表，再继续后续步骤。

### 4. 循环语义通用化

- `while` 由 `WorkflowRunGAgent` 的持久化 loop fact 推进。
- 终止条件由表达式求值决定，不依赖硬编码哨兵值。

### 5. 调用语义闭合

- `workflow_call` 的父子关联进入 `WorkflowRunState.pending_sub_workflows`。
- 子 run 完成后回到父 run actor 对账，不允许 fire-and-forget 悬挂。

### 6. 闭包模式治理

- `WorkflowValidator` 在 `closed_world_mode` 下执行额外规则。
- 封禁原语由 `WorkflowPrimitiveCatalog` 统一维护。
- `workflow_closed_world_guards.sh` 与 `WorkflowTuringCompletenessTests` 共同守护当前实现。

## Control Flow Model

```mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
flowchart TD
  Start["StartWorkflowEvent"] --> Dispatch["WorkflowRunGAgent Dispatch"]
  Dispatch --> StepReq["StepRequestEvent"]
  StepReq --> Primitive["Closed-World Primitive"]
  Primitive --> StepDone["StepCompletedEvent"]
  StepDone --> Route["WorkflowRunGAgent Route Next"]
  Route -->| "has next" | Dispatch
  Route -->| "no next" | Done["WorkflowCompletedEvent"]
```

## Minsky Encoding Mapping

- `INC(c)`：`assign target=c value=${add(variables.c, 1)}`
- `DEC(c)`：`assign target=c value=${sub(variables.c, 1)}`
- `JZ(c, L1, L2)`：`conditional condition=${eq(variables.c, '0')} branches.true=L1 branches.false=L2`
- 程序计数器通过 `next/branches` 图跳转表示。

证明工件：

- `workflows/turing-completeness/minsky-inc-dec-jz.yaml`
- `workflows/turing-completeness/counter-addition.yaml`
- `test/Aevatar.Integration.Tests/WorkflowTuringCompletenessTests.cs`

## Limits

- 本文给出的是可编码性证明路径，不是性能最优路径。
- 非停机程序仍通过执行预算、超时和宿主策略受控终止。
