# PR Review 架构审计打分（Inline YAML 复用 Actor + Cache 隐式依赖）- 2026-02-26

## 1. 审计范围与输入

1. 审计对象：
   - `src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunActorResolver.cs`
   - `src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs`
   - `src/workflow/Aevatar.Workflow.Core/Composition/WorkflowImplicitModuleDependencyExpander.cs`
   - `src/workflow/Aevatar.Workflow.Core/Modules/CacheModule.cs`
2. 输入来源：本次 PR review 结论（`P1 x1`、`P2 x1`）。
3. 评分口径：`docs/audit-scorecard/README.md`（100 分制、6 维度）。

## 2. 客观验证结果

| 检查项 | 命令 | 结果 |
|---|---|---|
| 架构门禁 | `bash tools/ci/architecture_guards.sh` | 通过 |
| Resolver 定向测试 | `dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --filter "FullyQualifiedName~WorkflowRunActorResolverTests" --nologo` | 通过（9/9） |
| 模块组合定向测试 | `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --filter "FullyQualifiedName~WorkflowModuleCompositionTests" --nologo` | 通过（2/2） |

结论：现有门禁与定向测试通过，但未覆盖本次两个运行时缺陷场景，存在“绿灯但语义错误”风险。

## 3. 审计结论（摘要）

1. 发现 1 个阻断问题（P1）：复用已绑定 Actor 时，inline YAML 会直接重配置运行态，可能影响并发 run。
2. 发现 1 个主要问题（P2）：workflow 仅声明 `cache` 且使用默认 `child_step_type` 时，可能缺失 `llm_call` 模块。
3. 当前建议：**不建议合并**，需先关闭 P1，且建议同批关闭 P2 并补测试。

## 4. 总体评分（100 分制）

**总分：77 / 100（B，当前不建议合并）**

| 维度 | 权重 | 得分 | 扣分说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 18 | 分层未破坏，但复用 Actor 的配置策略与运行态边界冲突。 |
| CQRS 与统一投影链路 | 20 | 14 | 并发 run 期间重装 workflow/modules，可能导致执行链路语义漂移。 |
| Projection 编排与状态约束 | 20 | 12 | active run 事实态与编排定义可被中途替换，破坏单一事实源稳定性。 |
| 读写分离与会话语义 | 15 | 11 | cache miss 默认派发 `llm_call`，但组合阶段可能未加载 handler。 |
| 命名语义与冗余清理 | 10 | 10 | 命名语义基本一致，无额外壳层引入。 |
| 可验证性（门禁/构建/测试） | 15 | 12 | 现有测试未覆盖“复用 Actor + inline YAML 并发 run”与“cache 默认 child 依赖”场景。 |

## 5. 问题分级清单

| ID | 级别 | 主题 | 结论 |
|---|---|---|---|
| F1 | P1 | Inline YAML 在复用已绑定 Actor 时重配置活跃运行态 | 阻断 |
| F2 | P2 | `cache` 默认 `child_step_type=llm_call` 但隐式依赖未补齐 | 主要 |

## 6. 详细发现与证据链

### F1（P1）复用已绑定 Actor + inline YAML 会重配置活跃运行态

**现象**

1. 解析到 inline YAML 后，若命中已有 Actor 且 workflow 绑定匹配，resolver 仍会调用 `ConfigureWorkflowAsync`。
2. `ConfigureWorkflowAsync` 会重建编译缓存并重新安装模块。
3. `WorkflowLoopModule` 已支持同 Actor 下多 `run_id` 活跃执行，重配置发生在 run 生命周期中会引入中途语义切换风险。

**代码证据**

1. 复用已有 Actor 时直接重配置：
   - `src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunActorResolver.cs:95`
   - `src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunActorResolver.cs:96`
2. 重配置会重建编译缓存并重装模块：
   - `src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs:98`
   - `src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs:109`
   - `src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs:112`
3. loop 运行态已按 run_id 并发维护：
   - `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:17`
   - `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:64`

**影响**

1. 早先 run 可能在执行中遭遇 workflow/modules 被替换，出现步骤跳转错位、失败或不可重放行为。
2. 同一 actorId 被复用时，接口调用方无法感知运行态被中途切换，行为不可预测。

**修复准入标准（Blocking）**

1. 对“已绑定且可能活跃”的 Actor，inline YAML 不允许原地重配置（返回冲突）或强制新建 Actor 执行。
2. 若保留重配置路径，必须先引入“无活跃 run”硬校验，并在 Actor 事件主线内保证串行切换。
3. 增加回归测试：复用同 actorId 并发发起两个 run，第二次携带 inline YAML 时不得影响第一个 run 的步骤推进。

---

### F2（P2）`cache` 默认 child 依赖未被隐式扩展到 `llm_call`

**现象**

1. `CacheModule` 在未提供 `child_step_type` 时默认派发 `llm_call`。
2. 隐式依赖扩展器未包含 `cache`，仅声明 `cache` 的 workflow 可能不加载 `llm_call` 模块。
3. 该场景下 cache miss 后 child step 无 handler，父步骤无法闭环完成。

**代码证据**

1. `cache` 默认 child 类型为 `llm_call`：
   - `src/workflow/Aevatar.Workflow.Core/Modules/CacheModule.cs:76`
   - `src/workflow/Aevatar.Workflow.Core/Modules/CacheModule.cs:77`
2. 隐式依赖未把 `cache` 映射到 `llm_call`：
   - `src/workflow/Aevatar.Workflow.Core/Composition/WorkflowImplicitModuleDependencyExpander.cs:11`
   - `src/workflow/Aevatar.Workflow.Core/Composition/WorkflowImplicitModuleDependencyExpander.cs:20`
3. step-type 参数扩展只在参数显式存在时生效，无法覆盖默认值语义：
   - `src/workflow/Aevatar.Workflow.Core/Composition/WorkflowStepTypeModuleDependencyExpander.cs:34`
   - `src/workflow/Aevatar.Workflow.Core/Composition/WorkflowStepTypeModuleDependencyExpander.cs:42`

**影响**

1. workflow 只写 `cache`（未显式 `child_step_type`）时，cache miss 可能卡住或最终失败。
2. 模块组合结果与 primitive 默认语义不一致，增加线上配置不确定性。

**修复建议（Major）**

1. 在 `WorkflowImplicitModuleDependencyExpander` 中把 `cache` 纳入需补齐 `llm_call` 的类型集合。
2. 或在 `cache` 组合阶段显式声明默认 child 依赖，不依赖调用者显式参数。
3. 增加回归测试：仅声明 `cache` 的 workflow（省略 `child_step_type`）在 miss 场景可正常完成。

## 7. 测试覆盖缺口

1. `WorkflowRunActorResolverTests` 覆盖了“已有 actor + inline yaml 配置”路径，但未覆盖“已有活跃 run 时禁止重配置”语义：
   - `test/Aevatar.Workflow.Application.Tests/WorkflowApplicationLayerTests.cs:313`
2. `WorkflowModuleCompositionTests` 目前仅验证 `foreach` 等场景触发 `llm_call` 隐式依赖，未覆盖 `cache` 默认 child 语义：
   - `test/Aevatar.Integration.Tests/WorkflowModuleCompositionTests.cs:56`

## 8. 合并门禁建议

1. 合并前必须关闭 F1（P1）。
2. 建议同批关闭 F2（P2），避免 runtime 组合缺口在实际 workflow 配置中触发挂起。
3. 两项修复都需补对应回归测试，避免再次出现门禁盲区。

## 9. 建议回归测试矩阵

1. `ResolveOrCreateAsync_WhenExistingBoundActorHasActiveRun_InlineYamlShouldNotReconfigureInPlace`
2. `ModuleDependencyExpanders_WhenWorkflowUsesCacheDefaultChild_ShouldIncludeLlmCall`
3. `CacheModule_WorkflowWithCacheOnly_DefaultChildStepType_ShouldCompleteOnMiss`

## 10. 审计结论

本次 PR 关键风险是“运行态一致性破坏”与“模块组合默认语义缺口”。在当前状态下，建议先完成 P1/P2 修复及回归测试补齐，再进入合并评审。
