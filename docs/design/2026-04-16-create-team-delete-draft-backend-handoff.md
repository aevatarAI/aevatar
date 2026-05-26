---
title: "Create Team Delete Draft 后端交接"
status: draft
owner: tbd
last_updated: 2026-04-22
references:
  - "./2026-04-22-team-member-first-prd.md"
---

# Create Team Delete Draft 后端交接

## 1. 背景

`Create Team -> Studio` 这条链路里，前端已经支持：

1. 保存创建 team 流程关联的实现草稿
2. 在 `Create Team` 页面展示 `Saved Draft`
3. 再次点击 `Open Studio` 时恢复这份草稿

但前端当前没有把 `Delete Draft` 接成真实删除，而是先禁用。

原因不是前端不想接，而是仓库当前没有正式的删除 workflow 草稿后端接口。

---

## 2. 当前前端状态

当前前端实际行为：

1. `Continue Draft` 可用
2. `Delete Draft` 按钮展示但禁用
3. 页面明确提示：等待后端删除 workflow 接口后再接真删除

这个状态是有意为之，目的是避免前端做出“看起来像删除、实际上只是清关联”的假删除行为。

---

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

---

## 4. 禁止误删的事实边界

`Delete Draft` 的语义必须严格限制为“删除 Studio draft”。

不能做的事：

1. 不能删除已创建的 team
2. 不能删除已创建的 member
3. 不能删除已配置的 team router
4. 不能删除已发布 binding
5. 不能删除 service
6. 不能 retire revision

一句话：

`Delete Draft` 只能删实现草稿，不得碰 team / member / runtime 事实。

---

## 5. 当前缺口确认

当前仓库里没有现成删除接口。

### 5.1 Controller 层

[WorkspaceController.cs](/Users/xiezixin/Documents/work/aevatar/src/Aevatar.Studio.Hosting/Controllers/WorkspaceController.cs:96) 到 [WorkspaceController.cs](/Users/xiezixin/Documents/work/aevatar/src/Aevatar.Studio.Hosting/Controllers/WorkspaceController.cs:188) 目前只有：

1. `GET /api/workspace/workflows`
2. `GET /api/workspace/workflows/{workflowId}`
3. `POST /api/workspace/workflows`

没有 `DELETE /api/workspace/workflows/{workflowId}`。

### 5.2 前端 API 层

[api.ts](/Users/xiezixin/Documents/work/aevatar/apps/aevatar-console-web/src/shared/studio/api.ts:827) 到 [api.ts](/Users/xiezixin/Documents/work/aevatar/apps/aevatar-console-web/src/shared/studio/api.ts:867) 目前只有：

1. `listWorkflows`
2. `getWorkflow`
3. `saveWorkflow`

没有 `deleteWorkflow`。

---

## 6. 建议后端实现清单

### 6.1 Controller

在 [WorkspaceController.cs](/Users/xiezixin/Documents/work/aevatar/src/Aevatar.Studio.Hosting/Controllers/WorkspaceController.cs:14) 新增：

1. `DELETE /api/workspace/workflows/{workflowId}`

### 6.2 Application

在 [WorkspaceService.cs](/Users/xiezixin/Documents/work/aevatar/src/Aevatar.Studio.Application/Studio/Services/WorkspaceService.cs:8) 新增：

1. `DeleteWorkflowAsync(string workflowId, CancellationToken ct = default)`

在 [AppScopedWorkflowService.cs](/Users/xiezixin/Documents/work/aevatar/src/Aevatar.Studio.Application/AppScopedWorkflowService.cs:15) 新增：

1. `DeleteAsync(string scopeId, string workflowId, CancellationToken ct = default)`

### 6.3 Storage Abstractions

在 [IStudioWorkspaceStore.cs](/Users/xiezixin/Documents/work/aevatar/src/Aevatar.Studio.Application/Studio/Abstractions/IStudioWorkspaceStore.cs:5) 新增：

1. `DeleteWorkflowFileAsync(string workflowId, CancellationToken cancellationToken = default)`

在 [IWorkflowStoragePort.cs](/Users/xiezixin/Documents/work/aevatar/src/Aevatar.Studio.Application/Studio/Abstractions/IWorkflowStoragePort.cs:6) 新增：

1. `DeleteWorkflowYamlAsync(string workflowId, CancellationToken ct)`

---

## 7. 建议返回语义

建议接口删除成功时返回：

1. `204 No Content`

核心不是 payload，而是：

1. 成功后 `getWorkflow` 应返回 `404`
2. `listWorkflows` 不再出现该项

---

## 8. 建议测试

至少补以下几类测试：

1. 删除 workflow 文件会同步删除 layout
2. 删除后 `ListWorkflowsAsync` 不再返回该项
3. 删除后 `GetAsync` 返回 `null`
4. scoped 模式下不影响 team / member / router / runtime facts
