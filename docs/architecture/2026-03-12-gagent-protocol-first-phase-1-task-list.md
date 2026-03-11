# GAgent 协议优先第一阶段任务清单（2026-03-12）

## 1. 文档元信息

- 状态：Completed
- 版本：R2
- 日期：2026-03-12
- 关联文档：
  - `docs/architecture/2026-03-12-gagent-implementation-source-unification-blueprint.md`
  - `docs/architecture/2026-03-12-gagent-protocol-first-implementation-plan.md`
  - `docs/architecture/2026-03-12-gagent-protocol-first-minimal-implementation-plan.md`
  - `AGENTS.md`
- 文档定位：
  - 本文把“最小化实施文档”的第一阶段拆成可直接执行的任务清单。
  - 本文只覆盖第一阶段，不扩展到统一创建入口、definition schema、热替换或 Mainnet 全量改造。

## 1.1 完成结果

- `T1` 到 `T13` 已完成。
- 后续已按 runtime/disptach 分责进一步收窄公共接口：第一阶段落地的 fat port 已演进为 `IActorMessagingPort + IActorRuntime` 双边界。
- 已落地产物：
  1. `src/Aevatar.Foundation.Abstractions/text_normalization_protocol.proto`
  2. `src/Aevatar.Foundation.Abstractions/IActorMessagingPort.cs`
  3. `src/Aevatar.Foundation.Runtime/Actors/RuntimeActorMessagingPort.cs`
  4. `src/workflow/Aevatar.Workflow.Core/Modules/GAgentSendModule.cs`
  5. `src/workflow/Aevatar.Workflow.Core/Modules/GAgentQueryModule.cs`
  6. `test/Aevatar.Integration.Tests/TextNormalizationProtocolContractTests.cs`
- 已验证命令：
  1. `dotnet build aevatar.slnx --nologo`
  2. `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --nologo --filter TextNormalizationProtocolContractTests`
  3. `dotnet test test/Aevatar.Hosting.Tests/Aevatar.Hosting.Tests.csproj --nologo --filter "RuntimeActorMessagingPortTests|ScriptCapabilityHostExtensionsTests"`
  4. `bash tools/ci/architecture_guards.sh`

## 2. 第一阶段目标

第一阶段只达成四个结果：

1. 建立第一组跨来源协议 `contract tests`
2. 将通用 actor 通信能力从 `Scripting` 私有层中立化
3. 为 workflow 增加最小通用 actor 通信面
4. 增加最小治理守卫，阻止继续按来源分叉

## 3. 完成定义

第一阶段完成时，必须满足：

1. 至少有一个真实协议同时被静态 `GAgent`、workflow、scripting 中的至少两种来源实现，并通过同一套 contract tests。
2. workflow 可以通过通用通信模块直接与协议兼容实例通信。
3. 通用 actor 通信接口不再挂在 `Aevatar.Scripting.*` 私有语义下。
4. Host/Application 中新增代码不能再解析 `actorId` 或按 workflow/script/static 来源分支。

## 4. 执行顺序

严格按以下顺序推进：

1. 先完成协议 contract test 样本设计
2. 再中立化 actor 通信端口
3. 再补 workflow 通用通信模块
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

1. `src/Aevatar.Foundation.Abstractions` 或新的中立 abstractions 项目
2. `test/Aevatar.Integration.Tests/`

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

建议新增：

1. `src/Aevatar.Foundation.Abstractions/Protocols/`

或按仓库现有习惯放在：

1. 新的中立 `Abstractions` 项目

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
2. 若现有步骤不够，优先为后续 `gagent_send/gagent_query` 模块预留最小能力

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

## T7. 中立化公共 actor 消息口

### 目标

把当前 `Scripting` 私有的通用 actor 通信能力迁移到公共层。

### 第一阶段起点代码（历史）

1. `src/Aevatar.Scripting.Core/Ports/IGAgentRuntimePort.cs`
2. `src/Aevatar.Scripting.Infrastructure/Ports/RuntimeGAgentRuntimePort.cs`

### 最小改法

第一阶段先做中立化；后续已进一步收窄为消息语义专用端口：

1. 消息语义上移到 Foundation 公共层
2. 生命周期/拓扑继续回归 `IActorRuntime`
3. 调整 DI 注册位置
4. 让 workflow 可直接引用

### 当前保留的方法

1. `PublishAsync`
2. `SendToAsync`
3. `InvokeAsync`

### 禁止

1. 第一阶段不要往接口里新增来源识别字段
2. 第一阶段不要引入统一来源注册模型

### 验收

1. 公共消息接口与实现已经不再带 `Scripting` 私有语义
2. 生命周期/拓扑不再与消息口混放
3. workflow 项目可引用并编译通过

## T8. 更新 scripting 对公共 actor 通信端口的依赖

### 目标

让 scripting 继续工作，但依赖新的公共端口。

### 涉及位置

1. `src/Aevatar.Scripting.Application/Runtime/ScriptRuntimeCapabilityComposer.cs`
2. `src/Aevatar.Scripting.Core/Runtime/ScriptInteractionCapabilities.cs`
3. `src/Aevatar.Scripting.Core/Runtime/ScriptAgentLifecycleCapabilities.cs`
4. `src/Aevatar.Scripting.Hosting/DependencyInjection/ServiceCollectionExtensions.cs`

### 改造要求

1. 只改依赖归属和命名
2. 不改变现有运行语义

### 验收

1. scripting 测试不因端口中立化而退化

## T9. 为 workflow 新增 `gagent_send` 模块

### 目标

提供最小通用 actor 发送能力。

### 模块职责

1. 接收目标 `actorId`
2. 接收 typed payload
3. 通过公共 actor 通信端口 `SendToAsync`
4. 回写最小执行结果

### 涉及位置

1. `src/workflow/Aevatar.Workflow.Core/Modules/`
2. `src/workflow/Aevatar.Workflow.Core/WorkflowCoreModulePack.cs`

### 验收

1. workflow 可通过 `gagent_send` 与静态或 script actor 通信

## T10. 为 workflow 新增 `gagent_query` 模块

### 目标

提供最小通用 actor 查询能力。

### 模块职责

1. 接收目标 `actorId`
2. 接收 typed query payload
3. 等待 typed reply
4. 将回复写回 workflow execution state

### 依赖

1. 公共 actor 通信端口
2. workflow execution state host

### 涉及位置

1. `src/workflow/Aevatar.Workflow.Core/Modules/`
2. `src/workflow/Aevatar.Workflow.Core/WorkflowCoreModulePack.cs`
3. 必要时新增最小 query/reply 等待辅助对象

### 验收

1. workflow 可通过 `gagent_query` 完成一个真实协议 query/reply 循环

## T11. 用协议样本接通 workflow 通用通信模块

### 目标

证明新模块不是空壳。

### 任务

1. 用 `gagent_send` 或 `gagent_query` 驱动 workflow 协议样本
2. 让 workflow 样本通过协议 contract tests

### 验收

1. workflow 样本不再完全依赖专用通信步骤完成协议交互

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
4. 本任务清单状态

### 验收

1. 文档不再把公共 actor 通信能力描述成 `Scripting` 私有能力

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
2. 再做公共 actor 通信端口中立化。
3. 再让 workflow 通过新通信面接入。
4. scripting 样本可以和端口中立化一起补齐。

## 8. 提交建议

建议按以下提交粒度拆分：

1. `feat/2026-03-12_protocol-contract-sample`
2. `refactor/2026-03-12_common-gagent-runtime-port`
3. `feat/2026-03-12_workflow-gagent-send-query`
4. `chore/2026-03-12_protocol-guards-and-docs`

## 9. 第一阶段验收命令建议

至少执行：

1. `dotnet build aevatar.slnx --nologo`
2. `dotnet test aevatar.slnx --nologo --filter "FullyQualifiedName~Protocol|FullyQualifiedName~Workflow|FullyQualifiedName~Scripting"`
3. `bash tools/ci/architecture_guards.sh`

若新增或修改测试，还必须执行：

1. `bash tools/ci/test_stability_guards.sh`

## 10. 收束性结论

第一阶段不要贪大。  
只要把以下三件先做对，后续大部分架构问题都会变得可控：

1. 用 contract tests 定义“什么叫同协议”
2. 用公共 actor 通信端口定住“如何互通”
3. 用 workflow 最小通用通信模块定住“如何编排互通”

这就是第一阶段的完整任务清单。
