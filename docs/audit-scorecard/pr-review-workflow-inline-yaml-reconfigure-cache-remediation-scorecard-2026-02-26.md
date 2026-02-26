# PR Review 修复复评打分（Inline YAML 复用 Actor + Cache 隐式依赖）- 2026-02-26

## 1. 复评范围

1. `src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunActorResolver.cs`
2. `src/workflow/Aevatar.Workflow.Core/Composition/WorkflowImplicitModuleDependencyExpander.cs`
3. `test/Aevatar.Workflow.Application.Tests/WorkflowApplicationLayerTests.cs`
4. `test/Aevatar.Integration.Tests/WorkflowModuleCompositionTests.cs`

对照基线：`docs/audit-scorecard/pr-review-workflow-inline-yaml-reconfigure-cache-architecture-audit-2026-02-26.md`（77/100，P1+P2）。

## 2. 客观验证结果

| 检查项 | 命令 | 结果 |
|---|---|---|
| Resolver 定向回归 | `dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --filter "FullyQualifiedName~WorkflowRunActorResolverTests" --nologo` | 通过（10/10） |
| 组合规则定向回归 | `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --filter "FullyQualifiedName~WorkflowModuleCompositionTests" --nologo` | 通过（3/3） |
| 架构门禁 | `bash tools/ci/architecture_guards.sh` | 通过 |
| 测试稳定性门禁 | `bash tools/ci/test_stability_guards.sh` | 通过 |

## 3. 问题关闭状态

### C1（原 P1）inline YAML 不再重配置已绑定 actor（Closed）

1. 对已绑定 actor 且请求携带 inline YAML，改为创建隔离新 actor 并配置，不再原地重配：
   - `src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunActorResolver.cs:85`
   - `src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunActorResolver.cs:91`
2. 回归测试已覆盖：
   - `test/Aevatar.Workflow.Application.Tests/WorkflowApplicationLayerTests.cs:335`

### C2（原 P2）`cache` 默认 child 依赖补齐 `llm_call`（Closed）

1. 隐式依赖扩展补充 `cache -> llm_call`：
   - `src/workflow/Aevatar.Workflow.Core/Composition/WorkflowImplicitModuleDependencyExpander.cs:18`
2. 回归测试已覆盖：
   - `test/Aevatar.Integration.Tests/WorkflowModuleCompositionTests.cs:61`

## 4. 复评评分（100 分制）

**总分：96 / 100（A+，建议合并）**

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 19 | resolver 行为收敛到“已绑定 actor 不变更”原则，边界更清晰。 |
| CQRS 与统一投影链路 | 20 | 19 | 避免运行中模块重装导致的执行链路漂移。 |
| Projection 编排与状态约束 | 20 | 19 | 活跃 run 的编排事实不再被 inline 配置中途替换。 |
| 读写分离与会话语义 | 15 | 15 | `cache` 默认 child 语义与模块组合结果一致。 |
| 命名语义与冗余清理 | 10 | 10 | 变更点集中，无新增壳层。 |
| 可验证性（门禁/构建/测试） | 15 | 14 | 新增两条定向回归并通过门禁；本次未跑 `dotnet test aevatar.slnx`。 |

## 5. 复评结论

本轮修复已关闭原审计中的 P1/P2 问题，且有对应测试与门禁证据支撑。当前结论：**通过复评，建议合并**。
