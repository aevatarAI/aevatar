---
title: "Create Team Delete Draft 后端交接"
status: draft
owner: tbd
last_updated: 2026-04-16
---

# Create Team Delete Draft 后端交接

## 1. 背景

`Create Team -> Studio` 这条链路里，前端已经支持：

1. 保存创建团队流程关联的 workflow 草稿
2. 在 `Create Team` 页面展示 `Saved Draft`
3. 再次点击 `Open Studio` 时恢复这份草稿

但前端当前没有把 `Delete Draft` 接成真实删除，而是先禁用。

原因不是前端不想接，而是仓库当前没有正式的删除 workflow 后端接口。

## 2. 当前前端状态

当前前端实际行为：

1. `Continue Draft` 可用
2. `Delete Draft` 按钮展示但禁用
3. 页面明确提示：等待后端删除 workflow 接口后再接真删除

这个状态是有意为之，目的是避免前端做出“看起来像删除、实际上只是清关联”的假删除行为。

## 3. 需要补的后端能力

### 3.1 HTTP 接口

需要新增正式删除接口：

1. `DELETE /api/workspace/workflows/{workflowId}`
2. 支持 `?scopeId=...`

语义要求：

1. 删除当前 workflow 草稿本身
2. 不只是删除 `Create Team` 对它的关联
3. 删除后再次进入 `Create Team`，前端才应该允许清空草稿恢复指针

### 3.2 Workspace 模式删除

当没有 `scopeId` 时，删除的是 workspace 文件型 workflow 草稿：

1. 删除 YAML 文件
2. 删除对应 layout 文件
3. 保证后续 `listWorkflows` 不再返回该项

### 3.3 Scoped 模式删除

当存在 `scopeId` 时，删除的是 scope draft storage 中保存的 workflow 草稿：

1. 删除存储中的 workflow YAML
2. 删除对应 persisted layout
3. 保证后续 `ListAsync/GetAsync` 不再返回该项

### 3.4 禁止误删 runtime 事实

删除 draft 的语义必须严格限制为“删除 Studio draft”。

不能做的事：

1. 不能删除已发布 binding
2. 不能删除 service
3. 不能 retire revision
4. 不能影响当前 scope 的默认入口

一句话：`Delete Draft` 只能删草稿存储，不得碰 runtime/service 事实。

## 4. 当前缺口确认

我已经确认当前仓库里没有现成删除接口。

### 4.1 Controller 层

[WorkspaceController.cs](/Users/xiezixin/Documents/work/aevatar/src/Aevatar.Studio.Hosting/Controllers/WorkspaceController.cs:96) 到 [WorkspaceController.cs](/Users/xiezixin/Documents/work/aevatar/src/Aevatar.Studio.Hosting/Controllers/WorkspaceController.cs:188) 目前只有：

1. `GET /api/workspace/workflows`
2. `GET /api/workspace/workflows/{workflowId}`
3. `POST /api/workspace/workflows`

没有 `DELETE /api/workspace/workflows/{workflowId}`。

### 4.2 前端 API 层

[api.ts](/Users/xiezixin/Documents/work/aevatar/apps/aevatar-console-web/src/shared/studio/api.ts:827) 到 [api.ts](/Users/xiezixin/Documents/work/aevatar/apps/aevatar-console-web/src/shared/studio/api.ts:867) 目前只有：

1. `listWorkflows`
2. `getWorkflow`
3. `saveWorkflow`

没有 `deleteWorkflow`。

### 4.3 Application 层

[WorkspaceService.cs](/Users/xiezixin/Documents/work/aevatar/src/Aevatar.Studio.Application/Studio/Services/WorkspaceService.cs:85) 到 [WorkspaceService.cs](/Users/xiezixin/Documents/work/aevatar/src/Aevatar.Studio.Application/Studio/Services/WorkspaceService.cs:145) 目前只有：

1. `ListWorkflowsAsync`
2. `GetWorkflowAsync`
3. `SaveWorkflowAsync`

[AppScopedWorkflowService.cs](/Users/xiezixin/Documents/work/aevatar/src/Aevatar.Studio.Application/AppScopedWorkflowService.cs:44) 到 [AppScopedWorkflowService.cs](/Users/xiezixin/Documents/work/aevatar/src/Aevatar.Studio.Application/AppScopedWorkflowService.cs:208) 目前也只有：

1. `ListAsync`
2. `GetAsync`
3. `SaveAsync`

都没有 delete。

### 4.4 存储抽象层

[IStudioWorkspaceStore.cs](/Users/xiezixin/Documents/work/aevatar/src/Aevatar.Studio.Application/Studio/Abstractions/IStudioWorkspaceStore.cs:5) 目前没有 `DeleteWorkflowFileAsync(...)`。

[IWorkflowStoragePort.cs](/Users/xiezixin/Documents/work/aevatar/src/Aevatar.Studio.Application/Studio/Abstractions/IWorkflowStoragePort.cs:6) 目前没有 `DeleteWorkflowYamlAsync(...)`。

## 5. 建议后端实现清单

### 5.1 Controller

在 [WorkspaceController.cs](/Users/xiezixin/Documents/work/aevatar/src/Aevatar.Studio.Hosting/Controllers/WorkspaceController.cs:14) 新增：

1. `DELETE /api/workspace/workflows/{workflowId}`

分支逻辑与现有 `Get/Save` 保持一致：

1. 有 `scopeId` 时走 `AppScopedWorkflowService`
2. 没有 `scopeId` 时走 `WorkspaceService`

### 5.2 Application

在 [WorkspaceService.cs](/Users/xiezixin/Documents/work/aevatar/src/Aevatar.Studio.Application/Studio/Services/WorkspaceService.cs:8) 新增：

1. `DeleteWorkflowAsync(string workflowId, CancellationToken ct = default)`

在 [AppScopedWorkflowService.cs](/Users/xiezixin/Documents/work/aevatar/src/Aevatar.Studio.Application/AppScopedWorkflowService.cs:15) 新增：

1. `DeleteAsync(string scopeId, string workflowId, CancellationToken ct = default)`

### 5.3 Storage Abstractions

在 [IStudioWorkspaceStore.cs](/Users/xiezixin/Documents/work/aevatar/src/Aevatar.Studio.Application/Studio/Abstractions/IStudioWorkspaceStore.cs:5) 新增：

1. `DeleteWorkflowFileAsync(string workflowId, CancellationToken cancellationToken = default)`

在 [IWorkflowStoragePort.cs](/Users/xiezixin/Documents/work/aevatar/src/Aevatar.Studio.Application/Studio/Abstractions/IWorkflowStoragePort.cs:6) 新增：

1. `DeleteWorkflowYamlAsync(string workflowId, CancellationToken ct)`

### 5.4 Storage Implementations

需要同步补实现：

1. [FileStudioWorkspaceStore.cs](/Users/xiezixin/Documents/work/aevatar/src/Aevatar.Studio.Infrastructure/Storage/FileStudioWorkspaceStore.cs:10)
2. [ChronoStorageWorkflowStoragePort.cs](/Users/xiezixin/Documents/work/aevatar/src/Aevatar.Studio.Infrastructure/Storage/ChronoStorageWorkflowStoragePort.cs:6)

其中：

1. 文件型存储要删 YAML + layout
2. chrono-storage 要删 `workflows/{workflowId}.yaml`
3. 如果 scoped layout 有单独持久化，也要一起删

## 6. 语义边界

后端实现时要特别注意：

1. `Delete Draft` 删除的是 Studio draft
2. 不是删除 workflow runtime 定义事实
3. 不是删除 scope binding
4. 不是删除 service revision
5. 不是删除 deployment

如果当前 scope 已经把这份 workflow 发布成入口，删除 draft 后：

1. 已发布入口仍应保持现状
2. 只是 Studio draft 不再存在

## 7. 建议返回语义

建议接口删除成功时返回：

1. `204 No Content`

如果希望和现有前端更顺手，也可以返回：

1. `200 OK`
2. `{ workflowId, deleted: true }`

但核心不是 payload，而是：

1. 成功后 `getWorkflow` 应返回 `404`
2. `listWorkflows` 不再出现该项

## 8. 建议测试

至少补以下几类测试：

### 8.1 WorkspaceService

1. 删除 workflow 文件会同步删除 layout
2. 删除后 `ListWorkflowsAsync` 不再返回该项
3. 删除不存在的 workflow 时语义清晰且幂等

### 8.2 AppScopedWorkflowService

1. 删除 scoped draft storage 中的 YAML
2. 删除后 `GetAsync` 返回 `null`
3. 删除后 `ListAsync` 不再合并出这条 stored workflow
4. 不影响 runtime/service facts

### 8.3 Controller

1. `DELETE /api/workspace/workflows/{workflowId}` 在 workspace 模式可用
2. `DELETE /api/workspace/workflows/{workflowId}?scopeId=...` 在 scoped 模式可用
3. 登录 scope 不匹配时返回正确错误

## 9. 前端接入点

后端接口补齐后，前端只需要接这三步：

1. 在 `studioApi` 增加 `deleteWorkflow(workflowId, scopeId?)`
2. `Create Team` 页启用 `Delete Draft`
3. 删除成功后清空：
   `teamDraftWorkflowId`
   `teamDraftWorkflowName`

然后再让 `Open Studio` 回退到空白草稿。

## 10. 当前结论

当前前端禁用 `Delete Draft` 是正确的。

原因不是交互没想清楚，而是后端删除 workflow 草稿链路目前确实不存在。

这份文档的目标就是把后端需要补的范围单独隔离出来，避免前端继续承担一个“假删除”语义。
