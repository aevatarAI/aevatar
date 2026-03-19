# 2026-03-19 `feat/scripts` 相对 `origin/dev` 变更审计评分卡

## 1. 审计范围与方法

- 审计对象：当前分支 `feat/scripts`
- 对比基线：`origin/dev`
- 对比方式：`git fetch origin dev --prune` 后执行 `git diff origin/dev...HEAD`
- 变更规模：`144 files changed, 10598 insertions(+), 514 deletions(-)`
- 主要变更面：`Aevatar.Scripting.*`、`Aevatar.GAgentService.*`、`Aevatar.Workflow.*`、`tools/Aevatar.Tools.Cli`、对应测试与 App Studio 前端

本次属于“分支相对 `origin/dev` 的定向审计”，评分口径遵循 [`docs/audit-scorecard/TEMPLATE.md`](../audit-scorecard/TEMPLATE.md)。

## 2. 客观验证结果

### 2.1 门禁

1. `bash tools/ci/architecture_guards.sh`
   结果：通过
2. `bash tools/ci/workflow_binding_boundary_guard.sh`
   结果：通过
3. `bash tools/ci/query_projection_priming_guard.sh`
   结果：通过
4. `bash tools/ci/projection_state_version_guard.sh`
   结果：通过
5. `bash tools/ci/projection_state_mirror_current_state_guard.sh`
   结果：通过
6. `bash tools/ci/projection_route_mapping_guard.sh`
   结果：通过
7. `bash tools/ci/test_stability_guards.sh`
   结果：通过

### 2.2 定向测试

1. `dotnet test test/Aevatar.GAgentService.Integration.Tests/Aevatar.GAgentService.Integration.Tests.csproj --nologo`
   结果：`Passed 63/63`
2. `dotnet test test/Aevatar.Tools.Cli.Tests/Aevatar.Tools.Cli.Tests.csproj --nologo`
   结果：`Passed 55/55`
3. `dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --nologo`
   结果：`Passed 163/163`
4. `dotnet test test/Aevatar.App.Tests/Aevatar.App.Tests.csproj --nologo`
   结果：`Passed 10/10`
5. `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo`
   结果：`Passed 388/388`

结论：这次分支在门禁和定向测试层面是干净的，可验证性表现强。

## 3. 整体评分

### 3.1 总分

| 总分 | 等级 | 结论 |
|---|---|---|
| **92 / 100** | **A** | 主方向正确，scope 语义已经被强类型化并贯通 scripting/workflow/projection；主要扣分集中在 App Studio 新链路的 runtime-neutral 性和读写分离诚实性。 |

### 3.2 六维评分

| 维度 | 分数 | 说明 |
|---|---:|---|
| 分层与依赖反转 | 18 / 20 | 新增 `IScopeScript*Port` 与对应 Application/Hosting 落位正确；但脚本生成链路仍直接依赖具体 actor 实例与本地 runtime 形态。 |
| CQRS 与统一投影链路 | 19 / 20 | `scope_id` 已进入 script/workflow proto、状态和 projector，统一投影链路保持一致；未见双轨 projection。 |
| Projection 编排与状态约束 | 20 / 20 | 相关 guards 全通过，未引入中间层事实态字典，workflow binding/scope 投影保持 actor-owned + projector materialization 模型。 |
| 读写分离与会话语义 | 12 / 15 | scoped script 保存成功后仍依赖立即读回 detail，命令成功与 readmodel 新鲜度被重新耦合。 |
| 命名语义与冗余清理 | 8 / 10 | 大部分命名清晰，scope 被补到 typed field；但 workflow 入口同时维护 `workflow.scope_id` 与 `scope_id` 两个 key，语义仍有重复。 |
| 可验证性 | 15 / 15 | 架构门禁与 5 组定向测试均通过，覆盖面与证据链完整。 |

## 4. 分模块评分

| 模块 | 分数 | 一句话结论 |
|---|---:|---|
| `Aevatar.Scripting.*` | 93 | scope 语义补得最完整，proto/state/readmodel/projector 一致性较好。 |
| `Aevatar.Workflow.*` | 92 | workflow definition/run/binding projection 已接入 scope；但入口 metadata 仍保留双 key 兼容。 |
| `Aevatar.GAgentService.*` | 91 | 新增 scope script 端口和能力路由清晰，基本守住了 Application/Hosting 边界。 |
| `tools/Aevatar.Tools.Cli` / App Studio | 86 | 功能很完整，测试也到位，但这里聚集了本次最主要的两处架构债。 |
| `Docs + Guards + Tests` | 95 | 测试增量大、门禁无回退，验证面明显提升。 |

## 5. 关键加分项

1. `scope` 没有被继续停留在临时 bag，而是进入了强类型契约与 readmodel。
   证据：
   `src/Aevatar.Scripting.Abstractions/script_host_messages.proto`
   `src/Aevatar.Scripting.Abstractions/CorePorts/script_projection_snapshots.proto`
   `src/Aevatar.Scripting.Projection/script_projection_read_models.proto`
   `src/workflow/Aevatar.Workflow.Abstractions/workflow_execution_messages.proto`
   `src/workflow/Aevatar.Workflow.Core/workflow_state.proto`

2. Projection 端对 scope 的物化是沿现有主链路做的，没有新造第二套查询模型。
   证据：
   `src/Aevatar.Scripting.Projection/Projectors/ScriptCatalogEntryProjector.cs:53`
   `src/Aevatar.Scripting.Projection/Projectors/ScriptDefinitionSnapshotProjector.cs:45`
   `src/workflow/Aevatar.Workflow.Projection/Projectors/WorkflowActorBindingProjector.cs:38`

3. GAgentService 层新增 `IScopeScriptCommandPort` / `IScopeScriptQueryPort`，分层落位正确。
   证据：
   `src/platform/Aevatar.GAgentService.Abstractions/Ports/IScopeScriptCommandPort.cs:1`
   `src/platform/Aevatar.GAgentService.Abstractions/Ports/IScopeScriptQueryPort.cs:1`
   `src/platform/Aevatar.GAgentService.Application/Scripts/ScopeScriptCommandApplicationService.cs:10`
   `src/platform/Aevatar.GAgentService.Application/Scripts/ScopeScriptQueryApplicationService.cs:8`

4. 本次分支不是“只改实现不补验证”，而是同步补了 App、Workflow、Scripting、GAgentService、CLI 测试。
   证据：
   `test/Aevatar.App.Tests/AppScriptsStudioApiTests.cs`
   `test/Aevatar.GAgentService.Integration.Tests/ScopeScriptCapabilityServiceTests.cs`
   `test/Aevatar.GAgentService.Integration.Tests/ScopeScriptEndpointsTests.cs`
   `test/Aevatar.Tools.Cli.Tests/AppScopedScriptServiceTests.cs`
   `test/Aevatar.Tools.Cli.Tests/ScriptGenerateOrchestratorTests.cs`
   `test/Aevatar.Workflow.Application.Tests/WorkflowRunActorResolverTests.cs`

## 6. 主要扣分项

### 6.1 Major: App Studio 脚本生成链路直接依赖本地 runtime 形态与具体 actor 实例

- 证据：
  `tools/Aevatar.Tools.Cli/Hosting/ScriptGenerateActorService.cs:75`
  `tools/Aevatar.Tools.Cli/Hosting/ScriptGenerateActorService.cs:77`
  `tools/Aevatar.Tools.Cli/Hosting/ScriptGenerateActorService.cs:80`
- 事实：
  `ScriptGenerateActorService` 通过 `_runtime.GetAsync(ActorId)` 取 actor 后，直接判断 `actor.Agent is ScriptGenerateGAgent`，并调用 `agent.ResetConversation()` 与 `agent.GenerateWithReasoningAsync(...)`。
- 影响：
  这条链路要求 runtime 暴露本地具体实例，无法自然迁移到代理型、远程型或分布式 runtime。它违背了“运行时形态不是业务事实”和“Dispatch/Runtime 分责”的仓库最高约束。
- 扣分：
  `分层与依赖反转 -2`

### 6.2 Major: scoped script 保存路径把“命令已成功”重新绑定到“读模型已立即可见”

- 证据：
  `tools/Aevatar.Tools.Cli/Hosting/AppScopedScriptService.cs:109`
  `tools/Aevatar.Tools.Cli/Hosting/AppScopedScriptService.cs:132`
  `tools/Aevatar.Tools.Cli/Hosting/AppScopedScriptService.cs:133`
- 事实：
  `SaveAsync` 在 `_scriptCommandPort.UpsertAsync(...)` 成功后，不直接使用 command result 或 upsert snapshot 构造响应，而是强制再走一次 `GetAsync(...)`。一旦 definition snapshot/readmodel 传播滞后，就会出现“命令已成功但 API 返回失败”的假阴性。
- 影响：
  这会把 write-side ACK 与 readmodel freshness 重新耦合，破坏读写分离语义，也让客户端很难理解“保存失败”到底是写失败还是读侧未刷新。
- 扣分：
  `读写分离与会话语义 -3`

### 6.3 Medium: workflow scope 在入口层仍通过双 key 兼容，语义未完全收敛为单一契约

- 证据：
  `src/workflow/Aevatar.Workflow.Application/Runs/WorkflowChatRequestEnvelopeFactory.cs:22`
  `src/workflow/Aevatar.Workflow.Application/Runs/WorkflowChatRequestEnvelopeFactory.cs:60`
  `src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunActorResolver.cs:291`
- 事实：
  入口侧会在 `workflow.scope_id` 与 `scope_id` 间相互镜像，resolver 也继续接受两个 key。虽然这是兼容性做法，但在内部主链路里仍保留了双重语义入口。
- 影响：
  仓库规则要求“单一语义字段”与“核心语义强类型化”。当前做法比纯 bag 好很多，但还没有完全收敛到单一稳定契约。
- 扣分：
  `命名语义与冗余清理 -2`

## 7. 非扣分观察项

1. `ScopeScriptCapabilityOptions.BuildDefinitionActorId(string scopeId, string scriptId, string revisionId)` 当前并未使用 `revisionId`。
   证据：
   `src/platform/Aevatar.GAgentService.Application/Scripts/ScopeScriptCapabilityOptions.cs:19`
   这更像命名/签名纯度问题，目前测试与实现都按“同 scope+script 复用 definition actor”工作，暂不单独扣分。

2. 定向测试过程中有少量编译器/分析器 warning，但未见本次分支新增的失败门禁。
   这次评分不将 warning 本身计入扣分项。

## 8. 修复优先级建议

### P1

1. 为脚本生成链路补一个 runtime-neutral 的 dispatch/port 抽象，去掉 `actor.Agent is ScriptGenerateGAgent` 这类本地实例依赖。
2. 调整 `AppScopedScriptService.SaveAsync` 的返回策略。
   可选方案：
   直接基于 `UpsertAsync` 的 command result 和 returned snapshot 组装响应；
   或把响应语义收敛为 honest ACK，再让前端异步刷新 detail/readmodel。

### P2

1. 将 workflow scope 输入收敛为单一内部 key/typed field。
2. 把兼容旧 key 的逻辑压缩到真正的 adapter/boundary 边缘，不继续扩散到 application/core 读取逻辑。

## 9. 审计结论

这次分支的主干判断是正确的：`scope` 从 scripting 到 workflow 再到 projection/readmodel，整体是在向“强类型主链路”收敛，而不是再造第二套旁路。  

因此我给这次分支 **92 / 100（A）**。如果只看“能不能合并”，当前门禁和定向测试都支持合并；如果看“是否已经达到仓库顶层架构要求的理想形态”，则建议至少优先收掉 App Studio 新链路上的两处架构债，再作为后续默认模式推广。
