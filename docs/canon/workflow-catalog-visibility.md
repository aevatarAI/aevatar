---
title: "Workflow 可见性模型：公共模板目录 vs scope 私有资源"
status: active
owner: eanzhao
---

# Workflow 可见性模型

本文定义 workflow 相关资源的可见性边界，作为权威口径。背景见 issue #2913（scope chat 枚举全局 workflow 的越权疑问）与 #2925（scope 私有定义泄漏进全局目录的修复）。

## 资源分层与边界

| 资源 | 事实拥有者 | 查询入口 | 可见性边界 |
| --- | --- | --- | --- |
| 公共模板目录（public template catalog） | 内建/文件导入的 `WorkflowGAgent`（`ScopeId` 为空） | `aevatar_list_workflows` / `aevatar_get_workflow`；`ChatQueryEndpoints` 的 `/workflows` | 所有已认证调用方可见；**只含产品内置的共享模板**，不含任何 scope 私有数据 |
| Scope workflow（私有） | scope 拥有的 service revision / `scope-workflow:*` 定义 actor（`ScopeId` 非空） | `/{scopeId}/workflows`（`ScopeWorkflowEndpoints`） | 仅 `scope_id` claim 与路径 scope 一致的调用方可见（`AevatarScopeAccessGuard`），无 admin 旁路 |
| Team member | Team actor | Team/member 查询端点 | 按 Team/scope 归属鉴权 |
| Published service | service catalog | service 调用/查询端点 | 按 service 身份与部署边界鉴权 |

`workflowId`、`memberId`、`publishedServiceId` 是**各自独立**的资源标识，鉴权按各自边界执行，不得互相替代或跨类型复用。

## 安全不变量

- **单一权威边界在投影写入侧**：`WorkflowCatalogCurrentStateProjector` 只物化 `ScopeId` 为空的定义。scope 私有定义（`ScopeId` 非空、`SourceKind = "service_revision"` 等）**永不进入**公共目录 readmodel。因此公共目录按构造不含私有数据，无需在查询侧对共享 readmodel 做 per-scope 过滤（那会把私有数据混入共享 readmodel，违反 readmodel 单一语义）。
- **公共目录 readmodel 不携带 `scope_id` 字段**：它不是 scope 归属的事实源；scope 归属由 scope 私有资源的独立 readmodel/端点承载。
- **后端合约强制，不依赖 prompt**：可见性由投影边界 + 端点 scope guard + 工具目录合约共同保证，模型提示词不作为权限手段。

## 公共目录合约（showInLibrary）

公共目录内部区分“可浏览模板”与“内部 primitive/demo 示例”：

- 前端 Console library 只展示 `showInLibrary = true` 项（`apps/aevatar-console-web/src/shared/workflows/catalogVisibility.ts`），但仍接收全部项以便对“当前选中但隐藏”的项做特殊标注。
- Agent 面向的 `aevatar_list_workflows` 遵循同一合约：**只枚举 `showInLibrary = true` 的公共模板**，不列出隐藏的内部示例。
- 隐藏项不参与枚举，但可通过 `aevatar_get_workflow` 按精确名称寻址（与前端“隐藏但可按名引用”一致）；这些隐藏项同样是无 scope 归属的公共模板，不含私有数据。

## 产品待确认项

公共模板库“默认在 scope chat 中可被发现/枚举”是当前设计（模板画廊 + 授权编排流程依赖 `aevatar_list_workflows` 发现可运行 workflow）。是否要进一步在 scope 会话中收窄公共库的默认可见性（例如默认只回当前 scope 资源、公共库改为显式请求），属于产品可见性模型决策，须由 owner 确认后再调整，不在本次安全修复范围内。
