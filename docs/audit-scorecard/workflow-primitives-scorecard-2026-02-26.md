# Workflow 原语设计与实现评分卡（2026-02-26）

## 1. 审计范围与方法

1. 审计对象：`Aevatar.Workflow.Core` 中 workflow 原语相关设计与实现（原语目录、解析/校验、核心模块、装配机制、关键测试）。
2. 重点范围：原语语义模型、别名与规范化、闭世界约束、运行时调度与相关性（`run_id`）、扩展性与可验证性。
3. 评分方法：100 分制，按“设计一致性 + 运行时正确性 + 可扩展 + 可验证”分维度打分。

## 2. 客观验证结果

| 检查项 | 命令 | 结果 |
|---|---|---|
| Parser/配置相关测试 | `dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --filter "FullyQualifiedName~WorkflowParserConfigurationTests" --nologo` | 通过（5/5） |
| 原语核心集成测试 | `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --filter "FullyQualifiedName~WorkflowValidatorCoverageTests\|FullyQualifiedName~WorkflowCoreModulesCoverageTests\|FullyQualifiedName~WorkflowAdditionalModulesCoverageTests\|FullyQualifiedName~WorkflowLoopModuleCoverageTests" --nologo` | 通过（61/61） |

备注：测试过程中出现 `NU1507`（多包源配置）警告，未阻断本次 workflow 原语审计结论。

## 3. 总体评分

**89 / 100（A-）**

## 4. 维度评分

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 原语模型与语义一致性 | 25 | 22 | 原语目录、别名归一、闭世界策略集中化；`children` 语义在模型中存在但执行面不够明确。 |
| 解析/校验与治理强度 | 20 | 17 | parser+validator 规则较全（分支/while/workflow_call/closed-world），但对“未知 step_type”缺少 fail-fast。 |
| 运行时正确性与隔离性 | 30 | 26 | `run_id` 已贯穿多数模块，loop 支持 retry/on_error/timeout/branch；但 run_id 归一化策略在模块间不一致。 |
| 可扩展性与组合能力 | 15 | 14 | `IWorkflowModulePack` + expander/configurator + factory 结构清晰，扩展成本低。 |
| 测试覆盖与可验证性 | 10 | 10 | 覆盖面广、场景完整（循环/并行/人工/cache/connector/tool）；仍缺“未知原语运行时阻断”回归用例。 |

## 5. 关键加分证据

1. 原语别名与闭世界策略集中维护：`src/workflow/Aevatar.Workflow.Core/Primitives/WorkflowPrimitiveCatalog.cs:9-80`。
2. 解析期统一规范化（step type + `*_step_type` 参数）：`src/workflow/Aevatar.Workflow.Core/Primitives/WorkflowParser.cs:86-101`。
3. 校验期闭世界约束与类型特定规则：`src/workflow/Aevatar.Workflow.Core/Validation/WorkflowValidator.cs:92-181`。
4. 运行时闭世界兜底拦截：`src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:252-263`。
5. loop 对 retry/on_error/timeout/branch 的统一编排：`src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:159-243`、`306-334`。
6. 原语扩展机制清晰（pack + registration + factory）：`src/workflow/Aevatar.Workflow.Core/WorkflowCoreModulePack.cs:8-57`、`src/workflow/Aevatar.Workflow.Core/WorkflowModuleFactory.cs:31-54`。
7. 关键相关性键升级（run/step/attempt 级 session）：`src/workflow/Aevatar.Workflow.Core/Modules/LLMCallModule.cs:58-66`、`src/workflow/Aevatar.Workflow.Core/Modules/EvaluateModule.cs:55-61`。

## 6. 主要扣分项（按影响度）

### 6.1 [High] 未知 `step_type` 缺少编译期/装配期 fail-fast，可能导致运行期静默卡住

**影响**  
YAML 拼写错误或未注册原语时，当前实现可能不会在编译/装配阶段报错，而是在运行期发出 `StepRequestEvent` 后无人处理，表现为 workflow 挂起。

**证据**

1. validator 仅覆盖少数 type 的专项规则，未见“必须属于已注册原语集合”的通用校验：`src/workflow/Aevatar.Workflow.Core/Validation/WorkflowValidator.cs:92-166`。
2. 模块安装阶段对 `TryCreate=false` 没有报错或中断：`src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs:215-222`。
3. loop 仍会按该 step 正常派发请求：`src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:270-304`。

**扣分**：-6

### 6.2 [Medium] `run_id` 归一化策略在模块间不一致，增加边缘关联复杂度

**影响**  
有的模块将缺失 run_id 归一为 `"default"`，有的归一为空串 `""`。在老客户端、省略 run_id 或人工注入事件场景下，关联行为更难预测。

**证据**

1. 归一为 `""`：`src/workflow/Aevatar.Workflow.Core/Modules/ParallelFanOutModule.cs:194`、`src/workflow/Aevatar.Workflow.Core/Modules/MapReduceModule.cs:131`、`src/workflow/Aevatar.Workflow.Core/Modules/RaceModule.cs:105`。
2. 归一为 `"default"`：`src/workflow/Aevatar.Workflow.Core/Modules/HumanInputModule.cs:145-146`、`src/workflow/Aevatar.Workflow.Core/Modules/WaitSignalModule.cs:152-153`、`src/workflow/Aevatar.Workflow.Core/Modules/CacheModule.cs:127-128`。

**扣分**：-3

### 6.3 [Low] `ReflectModule` 的状态索引维度与使用方式不够干净

**影响**  
`_states` 仅按 `stepId` 建索引且当前基本是写入/覆盖/删除，不参与读取决策；并发 run 同 stepId 时可维护性较差，也易形成“看起来有状态，实际未参与判定”的误导。

**证据**

`src/workflow/Aevatar.Workflow.Core/Modules/ReflectModule.cs:14`、`46`、`78`、`90`、`96`。

**扣分**：-2

## 7. 观察项（不扣分）

1. 原语模型中有 `Children` 字段（`src/workflow/Aevatar.Workflow.Core/Primitives/StepDefinition.cs:33-37`），解析与校验会保留/遍历（`WorkflowParser.cs:96`、`WorkflowValidator.cs:198-208`），但执行主路径以 step 参数驱动；建议明确“children 是否执行语义的一部分”。
2. 文档示例存在“参数式 while”与“children 式 while”并存（如 `docs/WORKFLOW.md:409-417`），建议统一口径。

## 8. 改进优先级建议

### P1（应尽快）

1. 在 `WorkflowValidator` 新增“step_type 必须可解析到已注册模块”的通用校验（含别名 canonicalize 后判定）。
2. 在 `WorkflowGAgent.InstallCognitiveModules` 中对关键模块创建失败做 fail-fast（至少对 workflow 中显式使用的原语必须报错）。

### P2（建议本迭代）

1. 抽取统一 `RunIdNormalizer`，将缺省 run_id 策略收敛到单一规则。
2. 清理或重构 `ReflectModule` 的 `_states`（改为 `(runId, stepId)` 或移除无效状态）。
3. 明确 `children` 的执行语义（要么实现消费链路，要么在 schema/文档中降级为保留字段并标注不生效）。

## 9. 结论

当前 workflow 原语体系已经具备较强的工程化基础：原语目录化、别名归一、闭世界约束、运行时编排与测试覆盖都较成熟。  
要从 **A- 提升到 A/A+**，关键是补齐“未知原语 fail-fast”与“run_id 归一一致性”两项治理闭环。

## 10. 修复状态（2026-02-26 更新）

### 已完成项

1. 已补齐“未知原语”双层 fail-fast：
   - `WorkflowValidator` 支持已知原语集合校验（含 `step_type` 与 `*_step_type` 参数位）。
   - `WorkflowGAgent` 编译阶段注入已注册原语集合，并在模块安装阶段对 `TryCreate=false` 直接抛错。
2. 已统一 `run_id` 归一化口径：
   - 新增 `WorkflowRunIdNormalizer.Normalize()`（`"default" + Trim`）。
   - 相关模块统一改为集中归一化，移除分散策略差异。
3. 已完成 `ReflectModule` 状态清理：
   - 去除按 `stepId` 维护的冗余状态容器，仅保留会话相关状态映射。

### 回归验证（本次）

1. `dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --filter "FullyQualifiedName~WorkflowParserConfigurationTests|FullyQualifiedName~WorkflowRunIdNormalizerTests" --nologo`  
   结果：通过（11/11）。
2. `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --filter "FullyQualifiedName~WorkflowValidatorCoverageTests|FullyQualifiedName~WorkflowGAgentCoverageTests|FullyQualifiedName~WorkflowAdditionalModulesCoverageTests|FullyQualifiedName~WorkflowLoopModuleCoverageTests" --nologo`  
   结果：通过（58/58）。

### 剩余风险

1. `children` 字段的执行语义仍需进一步统一到文档与实现口径（本次未改语义）。
2. `WorkflowLoopModule` 对“缺失 run_id + 多活跃 run”的保守丢弃策略被保留（设计性选择，非缺陷回退）。
