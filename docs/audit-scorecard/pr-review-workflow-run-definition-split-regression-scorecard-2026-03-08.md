# PR Review 架构审计打分（Workflow Run/Definition Split 回归复核）- 2026-03-08

## 1. 审计范围与输入

1. 审计对象：
   - `src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowRunActorPort.cs`
   - `src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunActorResolver.cs`
   - `src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunContextFactory.cs`
   - `src/workflow/Aevatar.Workflow.Application/Workflows/WorkflowDefinitionRegistry.cs`
   - `src/workflow/Aevatar.Workflow.Application.Abstractions/Workflows/IWorkflowDefinitionRegistry.cs`
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Actors/OrleansActor.cs`
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Actors/OrleansAgentProxy.cs`
   - `test/Aevatar.Workflow.Host.Api.Tests/WorkflowInfrastructureCoverageTests.cs`
   - `test/Aevatar.Workflow.Application.Tests/WorkflowApplicationLayerTests.cs`
   - `docs/architecture/workflow-run-actorized-target-architecture-2026-03-08.md`
2. 输入来源：本轮 reviewer 输出的 2 条有效问题（`2 x P1`）+ 源码复核 + 定向守卫/测试结果。
3. 评分口径：`docs/audit-scorecard/README.md`（100 分制、6 维度）。
4. 本文档性质：针对当前 PR diff 的定向 review 打分，不替代全量架构审计。

## 2. 审计结论（摘要）

1. `WorkflowRunActorPort` 通过 `actor.Agent` 的具体运行时类型推断 workflow binding，这个前提只在 local runtime 成立；Orleans runtime 暴露的是 `OrleansAgentProxy`，导致 existing actor inspection 主链失效。
2. 默认按 workflow name 新建 run 时，Application 总是把空 `DefinitionActorId` 传给 Infrastructure，当前实现因此会为每次 run 额外创建一个新的 definition actor；但系统没有 `workflowName -> definitionActorId` 的权威映射，所以这些 definition actor 无法在下一次默认入口复用。
3. 现有 guards 与定向测试均通过，但测试要么只覆盖 fake/local agent，要么直接把“空 `DefinitionActorId` 时创建新 definition actor”编码成正确行为，没有阻断这两条回归。
4. 当前结论：**73 / 100（B）**，**不建议合并**。原因不是分层名称错误，而是 run/definition split 的两条核心执行路径在 Orleans 兼容性与 definition lifecycle 收敛上都未闭环。

## 3. 总体评分（100 分制）

**总分：73 / 100（B）**

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 15 | `WorkflowRunActorPort` 已注入类型验证抽象，但 binding 识别仍回退到 concrete agent instance，破坏 runtime 抽象边界。 |
| CQRS 与统一投影链路 | 20 | 16 | `/api/chat + actorId` 的 existing actor 主链在 Orleans 下被错误短路，run 启动入口行为不再统一。 |
| Projection 编排与状态约束 | 20 | 14 | 默认入口每次隐式派生新的 definition actor，但缺少 workflow 维度的权威复用句柄，definition 生命周期没有闭环。 |
| 读写分离与会话语义 | 15 | 10 | 现有 actor 复用路径会在 Orleans 下误报 `AgentTypeNotSupported`；默认 workflow-name 启动也无法稳定落到同一 definition 事实源。 |
| 命名语义与冗余清理 | 10 | 8 | “definition actor 长生命周期、run actor 单次执行”的语义已经写入目标架构，但当前默认创建路径仍制造不可见 definition actor。 |
| 可验证性（门禁/构建/测试） | 15 | 10 | guards 与定向测试全部通过，但未覆盖 Orleans 代理识别与 definition actor 复用/泄漏语义。 |

## 4. 问题清单（按严重度）

| ID | 级别 | 问题 | 状态 | 结论 |
|---|---|---|---|---|
| F1 | P1 | 通过 concrete `actor.Agent` 推断 workflow binding，导致 Orleans existing actor inspection 失效 | Open | 阻断合并 |
| F2 | P1 | 默认 workflow-name 启动每次都会隐式创建新的 definition actor，且后续无法按 workflow 名复用 | Open | 阻断合并 |

## 5. 主要扣分项与证据

### F1：通过 concrete `actor.Agent` 推断 workflow binding，导致 Orleans existing actor inspection 失效（P1）

1. 直接证据：
   - `WorkflowRunActorPort.DescribeAsync(...)` 和 `GetBoundWorkflowNameAsync(...)` 都通过 `actor.Agent switch` 识别 `WorkflowGAgent` / `WorkflowRunGAgent`；不匹配时分别回退到 `Unsupported` / `null`：`src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowRunActorPort.cs:47`-`:74`、`src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowRunActorPort.cs:129`-`:145`
   - 同一个 port 已经注入 `IAgentTypeVerifier`，并在 `IsWorkflowDefinitionActorAsync(...)` / `IsWorkflowRunActorAsync(...)` 中走抽象化类型验证，但 `DescribeAsync(...)` 没有复用这一抽象：`src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowRunActorPort.cs:18`-`:19`、`src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowRunActorPort.cs:117`-`:127`
   - Orleans actor 的 `Agent` 固定是 `OrleansAgentProxy`，不是具体 `WorkflowGAgent` / `WorkflowRunGAgent`：`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Actors/OrleansActor.cs:8`-`:17`、`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Actors/OrleansAgentProxy.cs:3`-`:38`
   - `ResolveFromSourceActorAsync(...)` 会先依赖 `DescribeAsync(...)` 的 `IsWorkflowCapable` 判定 existing actor 是否可用；一旦拿到 `Unsupported`，就直接返回 `AgentTypeNotSupported`：`src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunActorResolver.cs:99`-`:105`
   - 即便放过第一层拦截，后续 workflow 名获取仍依赖 `GetBoundWorkflowNameAsync(...)`，当前 Orleans 下会返回 `null`，继续把流程推向 `AgentWorkflowNotConfigured`：`src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunActorResolver.cs:107`-`:109`、`src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunActorResolver.cs:137`-`:143`
   - 在 definition actor 复用路径中，代码先通过 `IAgentTypeVerifier` 证明 existing actor 是 `WorkflowGAgent`，随后又调用 `DescribeAsync(...)` 读取 payload；Orleans 下这里会丢失 definition payload，无法正确判断“同定义复用 vs 定义冲突”：`src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowRunActorPort.cs:200`-`:218`
2. 影响：
   - `/api/chat` 在提供现有 `actorId` 的主路径上，会因为 Orleans actor inspection 失败而直接报错。
   - `CreateRunAsync(...)` 对已存在 definition actor 的复用无法可靠判断 definition 是否一致，definition reuse 语义不成立。
3. 扣分归因：
   - 主扣分维度：`分层与依赖反转`
   - 影响维度：`读写分离与会话语义`

### F2：默认 workflow-name 启动每次都会隐式创建新的 definition actor，且后续无法按 workflow 名复用（P1）

1. 直接证据：
   - 无 `actorId` 的默认入口会构造 `WorkflowDefinitionBinding(string.Empty, workflowNameForRun, workflowYamlForRun, ...)`，明确把空 `DefinitionActorId` 传给 Infrastructure：`src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunActorResolver.cs:64`-`:80`
   - `WorkflowRunActorPort.EnsureDefinitionActorAsync(...)` 会把空白 `DefinitionActorId` 归一化为 `null`；一旦为 `null`，代码直接走 `CreateBoundDefinitionActorAsync(...)`：`src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowRunActorPort.cs:197`-`:231`
   - `CreateBoundDefinitionActorAsync(...)` 每次都会 `CreateDefinitionAsync(...)` 新建 `WorkflowGAgent`，再发送 `BindWorkflowDefinitionEvent` 绑定 YAML：`src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowRunActorPort.cs:234`-`:246`
   - 当前 registry 只维护 `workflow name -> yaml`，没有 `workflow name -> definition actor id` 的权威映射，因此下一次同名默认启动无法查回上一次创建的 definition actor：`src/workflow/Aevatar.Workflow.Application/Workflows/WorkflowDefinitionRegistry.cs:57`-`:62`、`src/workflow/Aevatar.Workflow.Application.Abstractions/Workflows/IWorkflowDefinitionRegistry.cs:3`-`:9`
   - 成功创建上下文后，`WorkflowRunContextFactory` 只把 run actor 放入返回的 `WorkflowRunContext`；`CreatedActorIds` 仅在 projection 创建失败或异常时用于回滚，不会在成功路径形成可复用句柄：`src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunContextFactory.cs:33`-`:42`、`src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunContextFactory.cs:73`-`:83`、`src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunContextFactory.cs:107`-`:136`
   - 现有测试甚至把“空 `DefinitionActorId` -> 新建 definition actor”编码成预期：`test/Aevatar.Workflow.Host.Api.Tests/WorkflowInfrastructureCoverageTests.cs:323`-`:343`
   - 目标架构明确要求 Application “选择或创建 definition actor”并“请求 definition actor 派生 run actor”；当前默认入口并没有 workflow-name 维度的 definition actor 选择策略，只是在每次 run 前匿名创建一个新的 definition actor：`docs/architecture/workflow-run-actorized-target-architecture-2026-03-08.md:493`-`:499`
2. 影响：
   - 正常的 `/api/chat` workflow-name 启动会为每次 run 累积一个新的 definition actor。
   - 这些 definition actor 不构成可复用的长生命周期 definition facts，只是 run 前置创建出来的隐式副产物。
   - run/definition split 的设计目标被削弱为“每次 run 多创建一个隐藏 actor”，既增加生命周期噪音，也没有形成单一 definition 事实源。
3. 扣分归因：
   - 主扣分维度：`Projection 编排与状态约束`
   - 影响维度：`命名语义与冗余清理`

## 6. 分模块评分（定向范围）

| 模块 | 评分 | 结论 |
|---|---:|---|
| Workflow Infrastructure / Actor Inspection | 67 | existing actor inspection 绑死到 local concrete agent，Orleans 运行时直接失效。 |
| Workflow Application / Run Resolver | 72 | 默认入口能创建 run，但没有以 workflow 为键选择/复用 definition actor。 |
| Workflow Definition Registry / Lifecycle | 70 | registry 只有 name->yaml，没有 name->definition actor 句柄，definition 生命周期无法闭环。 |
| Guards + Tests | 76 | 守卫和定向测试均通过，但现有测试集既未覆盖 Orleans 代理，又把空 definition id 创建新 actor 视为合法行为。 |

## 7. 客观验证记录

1. `bash tools/ci/architecture_guards.sh`
   - 结果：通过。
   - 摘要：`Projection route-mapping guard passed`、`runtime callback guards passed`、`Architecture guards passed`。
   - 结论：现有架构守卫未覆盖 Orleans actor inspection 与 definition actor 复用语义。
2. `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo --filter "FullyQualifiedName~WorkflowInfrastructureCoverageTests|FullyQualifiedName~WorkflowRunActorPortBranchTests"`
   - 结果：通过，`25 passed`。
   - 结论：基础设施测试集验证了当前分支实现，但其中 `WorkflowRunActorPort_WhenDefinitionActorIdBlank_ShouldCreateDefinitionWithNullPreferredId` 直接把问题行为固化为预期：`test/Aevatar.Workflow.Host.Api.Tests/WorkflowInfrastructureCoverageTests.cs:323`-`:343`
3. `dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --nologo --filter "FullyQualifiedName~WorkflowRunActorResolverTests"`
   - 结果：通过，`19 passed`。
   - 结论：resolver 测试主要依赖 `FakeActor` / `FakeWorkflowAgent` 场景，没有 Orleans `OrleansAgentProxy` 覆盖：`test/Aevatar.Workflow.Application.Tests/WorkflowApplicationLayerTests.cs:449`-`:461`、`test/Aevatar.Workflow.Application.Tests/WorkflowApplicationLayerTests.cs:523`-`:534`、`test/Aevatar.Workflow.Application.Tests/WorkflowApplicationLayerTests.cs:660`-`:676`
4. 本轮未执行：
   - `dotnet build aevatar.slnx --nologo`
   - `dotnet test aevatar.slnx --nologo`
   - 原因：本文档为 reviewer comment 驱动的定向复核，重点是闭合两条具体 correctness 证据链。

## 8. 合并前修复准入标准

1. **F1（Blocking）必须关闭**
   - binding discovery 不能再依赖 `actor.Agent` 的 concrete 类型；必须引入对 local 与 Orleans 都成立的统一描述/状态读取通道。
   - 必补回归测试：Orleans-backed existing `definition actor` 与 existing `run actor` 两条 `/api/chat + actorId` 路径都能正确识别 workflow 绑定。
2. **F2（Blocking）必须关闭**
   - 默认 workflow-name 启动必须改为“选择或创建 definition actor”，不能在缺少显式 `DefinitionActorId` 时匿名生成新 definition actor。
   - 必须建立单一权威来源来完成 `workflow -> definition actor` 复用，例如确定性 actor id 或显式 definition registry 句柄；不能把“上一次临时创建的 actor”留在成功路径外部不可见。
   - 必补回归测试：连续两次同 workflow name 启动 run，不得额外累积新的 definition actor；若定义未变，应复用同一个 definition actor。
3. 修复后建议至少执行：
   - `bash tools/ci/architecture_guards.sh`
   - `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo --filter "FullyQualifiedName~WorkflowInfrastructureCoverageTests|FullyQualifiedName~WorkflowRunActorPortBranchTests"`
   - `dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --nologo --filter "FullyQualifiedName~WorkflowRunActorResolverTests"`
   - 补充 Orleans-backed integration / hosting tests，覆盖 `OrleansAgentProxy` 路径

## 9. 审计结论

这轮 PR 的核心问题是 run/definition split 的两条关键语义没有真正闭环：

1. existing actor inspection 仍依赖 local runtime 的具体 agent 实例。
2. 默认 workflow-name 入口没有 authoritative definition actor 选择策略，只是在每次 run 前额外生成一个匿名 definition actor。

因此，本轮 PR review 定向打分结论为：**73 / 100（B）**，**不建议合并**。建议先关闭 `2 x P1`，补齐 Orleans 路径和 definition actor 复用回归测试，再进行一次修复复评。
