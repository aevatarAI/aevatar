# GAgent 协议优先第一阶段任务清单（2026-03-12）

## 1. 文档元信息

- 状态：Completed
- 版本：R3
- 日期：2026-03-12
- 关联文档：
  - `docs/architecture/2026-03-12-gagent-implementation-source-unification-blueprint.md`
  - `docs/architecture/2026-03-12-gagent-protocol-first-implementation-plan.md`
  - `docs/architecture/2026-03-12-gagent-protocol-first-minimal-implementation-plan.md`
  - `AGENTS.md`
- 文档定位：
  - 本文记录第一阶段最终收口后的实际落地状态，而不是执行过程中的中间方案。
  - 原始最小实施文档中的 `IActorMessagingPort`、`gagent_send`、`gagent_query` 仅是过渡方案；当前仓库代码已删除这些中间抽象。
  - 本文只覆盖第一阶段，不扩展到统一创建入口、definition schema、热替换或 Mainnet 全量改造。

## 1.1 完成结果

- `T1` 到 `T13` 已完成。
- 第一阶段最终没有保留公共 `IActorMessagingPort` 或 `IActorMessagingSession*`；Foundation 已回归最小原语边界：`IActorRuntime`、`IActorDispatchPort`、`IEventPublisher`、`IEventContext`。
- Scripting 的 actor 内发送/发布能力已收敛为子系统内部运行时上下文 `ScriptExecutionMessageContext`；workflow 侧落地为 `actor_send` 模块，并直接通过 `IWorkflowExecutionContext.SendToAsync(...)` 发送。
- 通用 `gagent_query` 方案未进入最终代码。query/reply 保持为协议自有 typed contract，不在 `Workflow.Core` 引入反射式通用查询模块。
- 协议样本 `.proto` 已从生产 `Abstractions` 移出，放入集成测试项目，避免把测试契约编进 Foundation 生产包。
- 已落地产物：
  1. `test/Aevatar.Integration.Tests/Protos/text_normalization_protocol.proto`
  2. `test/Aevatar.Integration.Tests/TextNormalizationProtocolContractTests.cs`
  3. `src/Aevatar.Scripting.Core/Runtime/ScriptExecutionMessageContext.cs`
  4. `src/workflow/Aevatar.Workflow.Core/Modules/ActorSendModule.cs`
  5. `docs/FOUNDATION.md`
  6. `docs/SCRIPTING_ARCHITECTURE.md`
  7. `src/workflow/README.md`
  8. `tools/ci/architecture_guards.sh`
- 已验证命令：
  1. `dotnet build aevatar.slnx --nologo`
  2. `dotnet test test/Aevatar.Hosting.Tests/Aevatar.Hosting.Tests.csproj --nologo --filter "ScriptCapabilityHostExtensionsTests"`
  3. `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo --filter "FullyQualifiedName~ScriptAgentLifecycleCapabilitiesTests|FullyQualifiedName~ScriptRuntimeGAgentReplayContractTests|FullyQualifiedName~ScriptRuntimeExecutionOrchestratorTests|FullyQualifiedName~RoslynScriptPackageCompilerTests|FullyQualifiedName~ClaimRoleIntegrationTests|FullyQualifiedName~ScriptPackageRuntimeContractTests"`
  4. `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --nologo --filter "FullyQualifiedName~TextNormalizationProtocolContractTests|FullyQualifiedName~ClaimComplexBusinessScenarioTests|FullyQualifiedName~ClaimOrchestrationIntegrationTests|FullyQualifiedName~ClaimScriptDocumentDrivenFlexibilityTests|FullyQualifiedName~ClaimLifecycleBoundaryTests|FullyQualifiedName~ScriptGAgentFactoryLifecycleBoundaryTests|FullyQualifiedName~WorkflowGAgentCoverageTests"`
  5. `bash tools/ci/architecture_guards.sh`
  6. `bash tools/ci/test_stability_guards.sh`

## 2. 第一阶段目标

第一阶段只达成四个结果：

1. 建立第一组跨来源协议 `contract tests`
2. 将通用 actor 通信能力从 `Scripting` 私有层收敛回 `runtime/dispatch/context` 原语边界
3. 为 workflow 增加最小通用 actor 发送能力，而不是继续堆专用步骤
4. 增加最小治理守卫，阻止继续按来源分叉

## 3. 完成定义

第一阶段完成时，必须满足：

1. 至少有一个真实协议同时被静态 `GAgent`、workflow、scripting 中的至少两种来源实现，并通过同一套 contract tests。
2. workflow 可以通过最小通用发送模块和协议自有 typed contract 与协议兼容实例通信。
3. 通用 actor 通信能力不再挂在 `Aevatar.Scripting.*` 私有语义下，也不再新增公共 all-in-one messaging/session 端口。
4. Host/Application 中新增代码不能再解析 `actorId` 或按 workflow/script/static 来源分支。

## 4. 执行顺序

严格按以下顺序推进：

1. 先完成协议 contract test 样本设计
2. 再收窄公共边界到 `runtime/dispatch/context`
3. 再补 workflow 最小通用发送模块
4. 最后补治理守卫和文档收尾

禁止一开始并行大改多个子系统。

## 5. 任务清单

## T1. 选定第一组协议样本

### 目标

选出一个足够简单、但能验证核心互通语义的协议样本。

### 要求

协议必须同时包含：

1. 一个 `command`
2. 一个 `reply` 或 `completion`
3. 一个 `query`
4. 一个最小 read model

### 推荐样本

优先使用“字符串输入 -> 规范化输出 -> query 返回最新结果”的最小协议。

### 输出

1. 协议名称
2. `.proto` 契约列表
3. 预期 read model 语义

### 涉及位置

1. `test/Aevatar.Integration.Tests/`
2. 若后续演进为正式共享协议，再拆到独立协议契约项目

### 验收

1. 团队确认协议样本不依赖特定来源内部机制

## T2. 落地协议 proto 契约

### 目标

将第一组协议定义为 typed `proto`，避免使用字符串约定。

### 要求

至少包含：

1. `Requested`
2. `Responded` 或 `Completed`
3. `QueryRequested`
4. `QueryResponded`

### 涉及位置

当前第一阶段最终落点：

1. `test/Aevatar.Integration.Tests/Protos/`

后续若演进为正式共享协议：

1. 再拆到独立协议契约项目，而不是回塞到 `Foundation.Abstractions`

### 产物

1. `.proto`
2. 生成类型
3. 基础 contract test fixture 所需辅助模型

### 验收

1. 不存在字符串路由替代 typed message

## T3. 实现静态 `GAgent` 协议样本

### 目标

提供一个最小静态 `GAgent` 实现，作为协议基线。

### 要求

该 actor 必须：

1. 接收协议 command
2. 持久化最小事实状态
3. 发出协议 completion/reply
4. 响应协议 query

### 涉及位置

建议新增测试样本项目或集成测试辅助类：

1. `test/Aevatar.Integration.Tests/TestDoubles/`
2. 或新的 `test/Aevatar.ProtocolContract.Tests/Samples/`

### 验收

1. 该静态样本能独立通过协议 contract tests

## T4. 实现 workflow 协议样本

### 目标

提供一个最小 workflow 实现，使其对外说同一套协议。

### 最小实现要求

1. 一个 workflow definition
2. 一个 `WorkflowRunGAgent` 实例
3. 通过最少步骤实现相同 command -> completion -> query 语义

### 涉及位置

1. `src/workflow/`
2. `workflows/`
3. `test/Aevatar.Integration.Tests/`

### 设计限制

1. 第一阶段不要为这个样本新增复杂专用步骤
2. 若现有步骤不够，优先为最终的 `actor_send` 模块和协议自有 query/reply 路径预留最小能力

### 验收

1. workflow 样本能通过同一套协议 contract tests

## T5. 实现 scripting 协议样本

### 目标

提供一个最小 scripting 实现，使其对外说同一套协议。

### 最小实现要求

1. 一个 script definition
2. 一个 script runtime actor
3. 相同 command -> completion -> query 语义

### 涉及位置

1. `src/Aevatar.Scripting.*`
2. `test/Aevatar.Integration.Tests/`

### 验收

1. scripting 样本能通过同一套协议 contract tests

## T6. 建立跨来源协议 contract tests

### 目标

建立真正的协议治理基线。

### 测试维度

必须至少覆盖：

1. command 接收
2. completion/reply 语义
3. query 语义
4. read model 语义

### 推荐结构

1. 共用 fixture
2. 静态样本测试
3. workflow 样本测试
4. scripting 样本测试
5. cross-source 一致性断言

### 涉及位置

建议新增：

1. `test/Aevatar.ProtocolContract.Tests/`

若暂时不拆项目，则放在：

1. `test/Aevatar.Integration.Tests/`

### 验收

1. 至少两种来源实现通过完全相同的 contract tests

## T7. 收窄公共边界，回归 runtime/dispatch/context 原语

### 目标

把当前 `Scripting` 私有的通用 actor 通信能力收敛为公共稳定原语，而不是新建一个公共全能消息口。

### 第一阶段起点代码（历史）

1. `src/Aevatar.Scripting.Core/Ports/IGAgentRuntimePort.cs`
2. `src/Aevatar.Scripting.Infrastructure/Ports/RuntimeGAgentRuntimePort.cs`

### 最终实现

1. Foundation 只保留 `IActorRuntime`、`IActorDispatchPort`、`IEventPublisher`、`IEventContext`
2. 删除公共 `IActorMessagingPort` / `IActorMessagingSession*` 过渡层
3. actor 内 `PublishAsync` / `SendToAsync` 继续作为执行上下文能力存在，而不是全局服务
4. 调整 DI 与依赖归属，让 workflow 和 scripting 分别通过自身上下文或 facade 接入

### 禁止

1. 第一阶段不要往公共抽象里新增来源识别字段
2. 第一阶段不要引入统一来源注册模型
3. 第一阶段不要把 runtime、dispatch、message context 混回一个接口

### 验收

1. 公共边界已经不再带 `Scripting` 私有语义
2. 生命周期/拓扑、外部 dispatch、actor 内消息上下文已经分责
3. workflow 与 scripting 项目都能在不依赖公共 messaging/session 层的情况下通过编译与测试

## T8. 更新 scripting 对公共原语与执行消息上下文的依赖

### 目标

让 scripting 继续工作，但依赖新的公共原语和脚本内部执行消息上下文。

### 涉及位置

1. `src/Aevatar.Scripting.Application/Runtime/ScriptRuntimeCapabilityComposer.cs`
2. `src/Aevatar.Scripting.Application/Runtime/ScriptRuntimeExecutionOrchestrator.cs`
3. `src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs`
4. `src/Aevatar.Scripting.Core/Runtime/ScriptExecutionMessageContext.cs`
5. `src/Aevatar.Scripting.Core/Runtime/ScriptInteractionCapabilities.cs`
6. `src/Aevatar.Scripting.Core/Runtime/ScriptAgentLifecycleCapabilities.cs`
7. `src/Aevatar.Scripting.Hosting/DependencyInjection/ServiceCollectionExtensions.cs`

### 改造要求

1. `SendToAsync` / `PublishAsync` 通过 `ScriptExecutionMessageContext` 暴露
2. lifecycle / topology 继续通过 `IActorRuntime`
3. 删除与 `SendToAsync` 语义重复的 `InvokeAgentAsync`
4. 不改变现有运行语义

### 验收

1. scripting 测试不因边界收窄而退化
2. scripting 不再依赖公共 messaging/session 过渡层

## T9. 为 workflow 新增 `actor_send` 模块

### 目标

提供最小通用 actor 发送能力。

### 模块职责

1. 接收目标 `actorId`
2. 接收 typed payload
3. 通过 `IWorkflowExecutionContext.SendToAsync(...)`
4. 回写最小执行结果

### 涉及位置

1. `src/workflow/Aevatar.Workflow.Core/Modules/`
2. `src/workflow/Aevatar.Workflow.Core/WorkflowCoreModulePack.cs`

### 验收

1. workflow 可通过 `actor_send` 与静态或 script actor 通信

## T10. 删除通用 `gagent_query` 方案，查询语义回归协议专属契约

### 目标

避免把反射式 query/reply 魔法字段引入 `Workflow.Core`，保持查询语义由协议自己拥有。

### 最终约束

1. `Workflow.Core` 不提供通用 `gagent_query` 模块
2. 若协议需要 query/reply，必须通过协议自有 typed message 与显式 request/reply 路径实现
3. 禁止在通用模块里反射注入 `request_id`、`reply_stream_id` 等魔法字段

### 涉及位置

1. `src/workflow/Aevatar.Workflow.Core/Modules/`
2. `src/workflow/Aevatar.Workflow.Core/WorkflowCoreModulePack.cs`

### 验收

1. `Workflow.Core` 中不存在通用 `gagent_query`
2. query/reply 仍可通过协议专属 typed contract 完成真实闭环

## T11. 用协议样本接通 workflow 通用通信模块

### 目标

证明 `actor_send` 不是空壳，并且协议查询不需要通用反射模块。

### 任务

1. 用 `actor_send` 驱动 workflow 协议样本
2. 由 source actor 使用显式协议契约完成 request/reply 查询路径
3. 让 workflow 样本通过协议 contract tests

### 验收

1. workflow 样本不再完全依赖专用通信步骤完成协议交互
2. workflow 样本不依赖通用 `gagent_query`

## T12. 增加最小治理守卫

### 目标

防止代码继续向旧方向漂移。

### 第一批守卫

1. 禁止 Host/Application 解析 `actorId`
2. 禁止新增带 workflow/script/static 来源名的 actor 通信抽象
3. 禁止 capability 私造平行 observation 主链

### 涉及位置

1. `tools/ci/architecture_guards.sh`
2. 或相关专项 guard 脚本

### 验收

1. 新增来源分叉代码会被 guard 拦截

## T13. 更新文档

### 目标

确保第一阶段落地后，文档与代码一致。

### 必须更新

1. `docs/FOUNDATION.md`
2. `docs/SCRIPTING_ARCHITECTURE.md`
3. `src/workflow/README.md`
4. `docs/architecture/2026-03-12-gagent-protocol-first-minimal-implementation-plan.md` 的归档说明
5. 本任务清单状态

### 验收

1. 文档不再把公共 actor 通信能力描述成 `Scripting` 私有能力
2. 文档不再把已删除的 `IActorMessagingPort`、`gagent_send`、`gagent_query` 描述成当前代码事实

## 6. 建议任务分组

## Group A：协议基线

1. `T1`
2. `T2`
3. `T3`
4. `T4`
5. `T5`
6. `T6`

## Group B：公共通信面

1. `T7`
2. `T8`

## Group C：workflow 互通面

1. `T9`
2. `T10`
3. `T11`

## Group D：治理与收尾

1. `T12`
2. `T13`

## 7. 建议执行顺序

推荐顺序：

1. `T1 -> T2 -> T3 -> T6`
2. `T7 -> T8`
3. `T9 -> T10 -> T11`
4. `T4 -> T5 -> T6` 完整补齐
5. `T12 -> T13`

说明：

1. 先把静态样本和 contract tests 跑起来，最快得到可验证基线。
2. 再把公共边界收窄到 runtime/dispatch/context 原语。
3. 再让 workflow 通过 `actor_send` 和协议自有查询路径接入。
4. scripting 样本可以和端口中立化一起补齐。

## 8. 提交建议

建议按以下提交粒度拆分：

1. `feat/2026-03-12_protocol-contract-sample`
2. `refactor/2026-03-12_runtime-dispatch-context-boundary`
3. `feat/2026-03-12_workflow-actor-send`
4. `chore/2026-03-12_protocol-guards-and-docs`

## 9. 第一阶段验收命令建议

至少执行：

1. `dotnet build aevatar.slnx --nologo`
2. `dotnet test test/Aevatar.Hosting.Tests/Aevatar.Hosting.Tests.csproj --nologo --filter "ScriptCapabilityHostExtensionsTests"`
3. `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo --filter "FullyQualifiedName~ScriptAgentLifecycleCapabilitiesTests|FullyQualifiedName~ScriptRuntimeGAgentReplayContractTests|FullyQualifiedName~ScriptRuntimeExecutionOrchestratorTests|FullyQualifiedName~RoslynScriptPackageCompilerTests|FullyQualifiedName~ClaimRoleIntegrationTests|FullyQualifiedName~ScriptPackageRuntimeContractTests"`
4. `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --nologo --filter "FullyQualifiedName~TextNormalizationProtocolContractTests|FullyQualifiedName~ClaimComplexBusinessScenarioTests|FullyQualifiedName~ClaimOrchestrationIntegrationTests|FullyQualifiedName~ClaimScriptDocumentDrivenFlexibilityTests|FullyQualifiedName~ClaimLifecycleBoundaryTests|FullyQualifiedName~ScriptGAgentFactoryLifecycleBoundaryTests|FullyQualifiedName~WorkflowGAgentCoverageTests"`
5. `bash tools/ci/architecture_guards.sh`

若新增或修改测试，还必须执行：

1. `bash tools/ci/test_stability_guards.sh`

## 10. 收束性结论

第一阶段不要贪大。  
只要把以下三件先做对，后续大部分架构问题都会变得可控：

1. 用 contract tests 定义“什么叫同协议”
2. 用 `runtime/dispatch/context` 原语定住“如何互通”
3. 用 workflow 的 `actor_send` 和协议自有 typed contract 定住“如何编排互通”

这就是第一阶段的完整任务清单。
