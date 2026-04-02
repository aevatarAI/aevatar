# GAgentService 与 Workflow 集成方案

## 目标

我们要把下面这条业务主链真正跑通：

1. 每个 scope 有外部分配的 `ScopeId`
2. scope 上传一份 workflow YAML，最终落到某个 `WorkflowGAgent` 的状态里
3. 同一个 scope 可以拥有多个 workflow definition，并且外部能通过 `ScopeId` 查到当前生效的 definition actor
4. 外部可以指定某个 definition actor，创建 `WorkflowRunGAgent` 并执行
5. 执行过程中可以实时拿到状态和事件，断线后还能继续从 readmodel 查询

## 合并结论

对比原始方案和当前框架现状后，最优解不是把 `user_id` 扩进 `WorkflowGAgent` 的领域事件或 `WorkflowActorBindingDocument`，而是：

- `GAgentService` 负责 definition 的归属、版本、激活和 scope 维度索引
- `Workflow Capability` 负责 run 的创建、执行、SSE/WS 实时事件和 run readmodel 查询
- scope 维度 API 只做一层薄编排，不直接读取 runtime state，也不在中间层维护 `ScopeId -> ActorId` 进程内字典

实现落点上，这层“用户 workflow 编排”不再放在 `workflow` 模块里，也不直接留在 `Hosting` 端点层。  
它现在下沉到 `platform/Aevatar.GAgentService.Application`，由 `platform/Aevatar.GAgentService.Hosting` 只做 HTTP 组合与转发。  
原因是它本质上是“GAgentService 控制面 + Workflow 执行面”的跨能力应用编排，不属于 workflow 自身领域，也不该由 Host 承担核心业务流程。

这条路径最贴合现有代码边界：

- `WorkflowGAgent` 本来就只承载 definition 事实
- `DefaultServiceRuntimeActivator` 已经会在激活 workflow revision 时创建并绑定真正的 `WorkflowGAgent`
- `ServiceCatalogReadModel` 已经会把当前激活 deployment 的 `PrimaryActorId` 投影出来
- `/api/chat` 已经能基于 definition actor 创建新的 `WorkflowRunGAgent` 并提供 SSE 实时事件

## 为什么不把 `user_id` 塞进 workflow 核心事件

原始文档里“给 `BindWorkflowDefinitionEvent / BindWorkflowRunDefinitionEvent / WorkflowActorBindingDocument` 增加 `user_id`”这个做法，不是当前仓库里最合理的主路径，原因有三点：

1. `user -> workflow definition` 是归属和索引语义，更适合落在 `GAgentService` 的 service identity/readmodel，不属于 workflow definition 本身的领域事实。
2. `WorkflowActorBindingDocument` 的职责是 `actorId -> workflow binding`，如果再承载 `user_id -> 多个 definition actor`，会和 `ServiceCatalogReadModel` 形成重复权威源。
3. 如果目标是“通过 GAgentService 跑通”，那就不应该再回退到“API 直接创建/绑定 `WorkflowGAgent`”这条旁路。

保留下来的部分是“面向用户的薄 API”这个思路，这一层是有价值的。

## 权威模型

### 1. 用户与 workflow definition 的映射

每个用户的 workflow definition 映射到一个 `GAgentService service`：

- `tenantId`：固定配置，例如 `user-workflows`
- `appId`：固定配置，例如 `workflow`
- `namespace`：`user:{user-scope-token}`
- `serviceId`：外部 workflow 标识，也就是用户 API 里的 `workflowId`

其中：

- `scope-token` 不是原始 `ScopeId`，而是一个可逆需求外、内部稳定的安全 token，用来避免把任意外部 `ScopeId` 直接放进 `service key / actor id`
- 当前实现采用“slug + hash”生成 token，既稳定，又能在 actor/service key 里安全使用

### 2. definition actor 的生成

每次激活 workflow revision 时，`GAgentService` 会创建一个真正的 `WorkflowGAgent`。  
当前激活 actor id 不是原始业务键，而是 deployment-scoped：

- `definitionActorIdPrefix = user-workflow:{user-token}:{workflow-token}`
- `primaryActorId = {definitionActorIdPrefix}:{deploymentId}`

这意味着：

- 对外稳定业务标识应该是 `workflowId`
- 当前生效的 `actorId` 必须从 readmodel 读取
- actor id 对调用方仍然是不透明地址，不允许客户端自己拼装

## 最终 API 设计

### 1. 创建或更新用户 workflow definition

`PUT /api/scopes/{scopeId}/workflows/{workflowId}`

请求体：

```json
{
  "workflowYaml": "name: approval",
  "workflowName": "approval",
  "displayName": "Approval Flow",
  "inlineWorkflowYamls": {
    "child.yaml": "name: child"
  },
  "revisionId": "rev-001"
}
```

内部顺序：

1. 计算 service identity
2. 如果 service 不存在，调用 `CreateService`
3. 如果 display name 变更，调用 `UpdateService`
4. 调用 `CreateRevision(implementationKind = workflow)`
5. `PrepareRevision`
6. `PublishRevision`
7. `SetDefaultServingRevision`
8. `ActivateServiceRevision`

结果：

- YAML 最终通过激活链路绑定进真正的 `WorkflowGAgent.State`
- 返回当前 revision、definition actor prefix、预期 active actor id，以及当前 service summary

### 2. 查询某个 scope 拥有的 workflow definitions

`GET /api/scopes/{scopeId}/workflows`

内部查询：

1. 通过 `namespace = user:{user-token}` 调用 `IServiceLifecycleQueryPort.ListServicesAsync`
2. 从 `ServiceCatalogSnapshot` 读取：
   - `serviceId`
   - `displayName`
   - `activeServingRevisionId`
   - `deploymentId`
   - `primaryActorId`
   - `deploymentStatus`
3. 如果 `primaryActorId` 存在，再通过 `IWorkflowActorBindingReader.GetAsync(actorId)` 补充 `workflowName`

返回的 `actorId` 是当前生效 definition actor 的权威读值。

### 3. 从指定 definition actor 启动 run 并以 SSE 实时返回

`POST /api/scopes/{scopeId}/workflow-runs:stream`

请求体：

```json
{
  "actorId": "user-workflow:...:deployment...",
  "prompt": "run it",
  "sessionId": "session-1",
  "headers": {
    "source": "user-api"
  }
}
```

内部顺序：

1. 先用 `ScopeId + actorId` 做 ownership 校验
2. 校验通过后，直接转发到现有 `/api/chat` 主链：
   - `Prompt -> prompt`
   - `ActorId -> agentId`
   - `SessionId -> sessionId`
   - `Headers -> headers`
3. `WorkflowRunActorResolver` 会基于 definition actor 的 binding 创建新的 `WorkflowRunGAgent`
4. SSE 第一帧返回 `aevatar.run.context`，其中包含新的 `runActorId`

这一步没有新造第二套 run 创建逻辑，只是把用户维度的 ownership 校验包在外面。

### 4. 运行态查询与断线恢复

run 创建后，继续复用现有查询端点：

- `GET /api/actors/{runActorId}`
- `GET /api/actors/{runActorId}/timeline`
- `GET /api/actors/{runActorId}/graph-subgraph`

如果后续需要做用户级 resume/signal，同样建议在用户 API 外层增加 ownership 校验后，再转发到已有 `/api/workflows/resume` 和 `/api/workflows/signal`。

## 实时事件方案

### 主路径

继续复用现有 workflow projection/live pipeline：

- `POST /api/chat` 本身就是 SSE
- `WorkflowRunCommandTargetBinder` 会在 dispatch 前挂好 projection session 与 live sink
- `WorkflowExecutionRunEventProjector` 会把 committed workflow events 投影成 `WorkflowRunEventEnvelope`

### 这次补的增强

现有 mapper 已经覆盖：

- `RUN_STARTED / RUN_FINISHED / RUN_ERROR`
- `STEP_STARTED / STEP_FINISHED`
- `TEXT_MESSAGE_*`
- `TOOL_CALL_*`
- `WAITING_SIGNAL / SIGNAL_BUFFERED`
- 一部分 `custom` 事件

但它之前不会把“未显式映射的 observed event”继续发出来。  
这次实现新增了一个兜底行为：

- 如果某个 committed event 没有命中前面的专用 mapper
- 会自动发出一个 `custom` 事件：`aevatar.raw.observed`
- 载荷是强类型的 `WorkflowObservedEnvelopeCustomPayload`

里面包含：

- `eventId`
- `payloadTypeUrl`
- `publisherActorId`
- `correlationId`
- `stateVersion`
- 原始 `payload`

这样就满足了“执行过程中抛出来的任何事件，都能在统一事件流里被监听到”。

## 当前实现落点

本次代码实现已经落在以下位置：

- `src/platform/Aevatar.GAgentService.Application/Workflows/UserWorkflowCommandApplicationService.cs`
- `src/platform/Aevatar.GAgentService.Application/Workflows/UserWorkflowQueryApplicationService.cs`
- `src/platform/Aevatar.GAgentService.Hosting/Endpoints/UserWorkflowEndpoints.cs`
- `src/workflow/Aevatar.Workflow.Presentation.AGUIAdapter/EventEnvelopeToWorkflowRunEventMapper.cs`
- `src/workflow/Aevatar.Workflow.Application.Abstractions/Runs/workflow_run_events.proto`

## 验证结果

已完成：

- `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo`
- `bash tools/ci/test_stability_guards.sh`
- `bash tools/ci/workflow_binding_boundary_guard.sh`

## 后续建议

这次先把主流程跑通了。下一步如果要继续补齐，可以按这个顺序推进：

1. 增加 `GET /api/scopes/{scopeId}/workflows/{workflowId}` 单项查询
2. 增加用户级 `resume/signal` 包装端点
3. 如果外部需要纯 JSON 启动而不是 SSE，再补一个 detached run 启动入口
4. 如果后续要支持非 chat 语义的 workflow run，再扩展 `GAgentService` 的 workflow endpoint 模型，而不是把新语义塞进现有 `invoke/chat`
