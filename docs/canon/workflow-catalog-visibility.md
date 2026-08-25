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
| 公共模板目录（public template catalog） | 内建/文件导入的 `WorkflowGAgent`（`ScopeId` 为空） | `aevatar_list_workflow_templates` / `aevatar_get_workflow_template`；`ChatQueryEndpoints` 的 `/workflows` | 所有已认证调用方可见；**只含产品内置的共享模板**，不含任何 scope 私有数据 |
| Scope workflow（私有） | scope 拥有的 service revision / `scope-workflow:*` 定义 actor（`ScopeId` 非空） | `/{scopeId}/workflows`（`ScopeWorkflowEndpoints`） | 仅 `scope_id` claim 与路径 scope 一致的调用方可见（`AevatarScopeAccessGuard`），无 admin 旁路 |
| Team member | Team actor | Team/member 查询端点 | 按 Team/scope 归属鉴权 |
| Published service | service catalog | service 调用/查询端点 | 按 service 身份与部署边界鉴权 |

`workflowId`、`memberId`、`publishedServiceId` 是**各自独立**的资源标识，鉴权按各自边界执行，不得互相替代或跨类型复用。

## 安全不变量

- **单一权威边界在投影写入侧**：`WorkflowCatalogCurrentStateProjector` 只物化 `ScopeId` 为空的定义。scope 私有定义（`ScopeId` 非空、`SourceKind = "service_revision"` 等）**永不进入**公共目录 readmodel。因此公共目录按构造不含私有数据，无需在查询侧对共享 readmodel 做 per-scope 过滤（那会把私有数据混入共享 readmodel，违反 readmodel 单一语义）。
- **公共目录 readmodel 不携带 `scope_id` 字段**：它不是 scope 归属的事实源；scope 归属由 scope 私有资源的独立 readmodel/端点承载。
- **后端合约强制，不依赖 prompt**：可见性由投影边界 + 端点 scope guard + 工具目录合约共同保证，模型提示词不作为权限手段。

## Definition / Run 写边界

- **Definition provisioning 是唯一正常写入口**：文件导入、startup materialization、service revision activation 等明确拥有 Definition 生命周期的流程通过 `EnsureDefinitionAsync` 创建或更新 Definition。
- **Run 入口不更新已有 Definition**：`CreateRunAsync`、`EnsureRunAsync`、`EnsureRunAndDispatchAsync` 对非空 `DefinitionActorId` 只验证并复用已有 Definition，禁止写回 YAML、inline YAML、scope、source kind 或 admission plan，也禁止在请求路径修复 binding readmodel。
- **临时 Definition 只来自空 ID**：inline YAML、draft、fork 等确实需要临时 Definition 的入口必须传空 `DefinitionActorId`，由 Run provisioning 创建与该 Run 关联的隔离 Definition。
- **Run scope 与 Definition owner scope 分离**：调用 scope 始终写入 Run binding/execution context。global catalog Definition 的 owner scope 为空；scope-owned service revision Definition 的 owner scope 为其 tenant，二者不能因一次 Run 请求互相转换。
- **Admission 差异不授予写权限**：Run 请求复用已有 Definition 时只校验 workflow name、root YAML 和 inline YAML。request-side 缺少 startup admission digest 不构成重绑理由；admission plan 只能由 Definition provisioning 更新。
- **空 scope 修复使用 protobuf presence**：`BindWorkflowDefinitionEvent.scope_id` 是 optional 字段。字段缺席表示保留历史 scope，present-empty 表示由 Definition owner 显式清空。startup materializer 对公共目录 Definition 必须发送 present-empty。

## 公共目录合约（showInLibrary）

公共目录内部区分“可浏览模板”与“内部 primitive/demo 示例”：

- 前端 Console library 只展示 `showInLibrary = true` 项（`apps/aevatar-console-web/src/shared/workflows/catalogVisibility.ts`），但仍接收全部项以便对“当前选中但隐藏”的项做特殊标注。
- Agent 面向的 `aevatar_list_workflow_templates` 遵循同一合约：**只枚举 `showInLibrary = true` 的公共模板**，不列出隐藏的内部示例。
- 隐藏项不参与枚举，但可通过 `aevatar_get_workflow_template` 按精确名称寻址（与前端“隐藏但可按名引用”一致）；这些隐藏项同样是无 scope 归属的公共模板，不含私有数据。

## Chat 产品资源语义

Console Chat 的默认资源语义已经确定，不能再把公共模板目录回答成用户 workspace 中的 workflow：

| Chat 产品资源 | Agent tool | 事实源 |
| --- | --- | --- |
| Team-owned workspace workflow | `aevatar_list_workflows` | Studio member current-state read model |
| Public workflow template | `aevatar_list_workflow_templates` / `aevatar_get_workflow_template` | Global workflow catalog read model |

- 用户未使用 template/public/example/library 等限定词时，`workflow` 表示当前 scope 下 Team-owned workflow member。`aevatar_list_workflows` 只读 `IStudioMemberQueryPort`，支持 `team_id` 与分页；请求“全部”时必须沿 `next_page_token` 继续读取直至为空。
- 只有用户明确询问公共模板、示例或模板库时，才使用公共模板工具。公共模板工具没有旧名称兼容别名。
- workspace workflow 的 canonical editor URL 是 `/scopes/:scopeId/teams/:teamId/members/:memberId/workflow`。
- `memberId`、`workflowId`、`publishedServiceId` 是隔离身份；Chat tool、prompt、路由和测试均不得推导、替换或合并这些 ID。
- Prompt 只负责选择正确工具，不承担鉴权。事实边界仍由 projection-backed query port、投影写入约束与 Host composition 强制保证。

历史上已被错误物化进公共 catalog 的文档不在 query path 中修复。评估与清理必须走独立后台迁移，见 `docs/operations/2026-07-23-workflow-catalog-contamination-repair.md`。

## Scope workflow catalogue query

Console Workflow Activity vNext uses the scope-owned catalogue endpoint:

`GET /api/scopes/{scopeId}/workflow-catalogue?view=all|drafts|archived&query={text}&cursor={cursor}&take={take}`

Contract:

- `view=all` is the compatibility default and returns the non-archived scope catalogue keyed by exact `workflowId`, preserving `hasDraftSource` and `hasCommittedSource` when draft and published service facts converge on the same workflow row.
- `view=drafts` returns only non-archived rows with an authoritative draft source, while still returning published service facts and capabilities when the same `workflowId` also has an active published service source.
- `view=archived` returns only rows whose committed deployment status is `Deactivated`; archived rows keep their committed facts in the row payload, including deployment identity and status.
- The backend applies scope authorization, then reads the materialized `ScopeWorkflowCatalogueRowDocument` read model. The request path must not read Studio workspace drafts, Studio members, service catalogues, deployment catalogues, workflow actor bindings, event store, or write-model state to reconstruct catalogue rows.
- The aggregate row is keyed as `{scopeId}:workflow:{workflowId}`. Draft and published workflow service sources merge by exact `workflowId`; Team and Member state are not inputs to this catalogue surface.
- `ScopeWorkflowCatalogueSourceDocument` is an internal materialization input with two authority kinds only: `draft` from Studio workspace committed state and `service` from service/deployment committed state. Service facts carry committed actor, active revision, deployment ID, deployment status, and service identity.
- Source documents use deterministic source IDs (`{scopeId}:{workflowId}:draft`, `{scopeId}:{workflowId}:service`) and deterministic materialized actor IDs, because the same workflow-keyed source can be refreshed from different underlying workspace or service actors over time.
- Existing current-state read models are backfilled by host startup composition into draft/service source documents and refreshed aggregate rows before request-path reads rely on the catalogue. Backfill uses exact upserts/tombstones; it must not use search-based cleanup of just-written documents.
- Row materialization composes the latest draft source and latest service source: draft name/description wins for editable display, service facts drive Activity/run capability, `updatedAtUtc` is the maximum source update time, and the row is deleted only after both source documents are absent.
- View filtering happens before search and cursor pagination. The catalogue query port owns view filtering, search, deterministic ordering, and cursor pagination in that order. Clients must not join draft/committed lists or filter an unbounded catalogue in memory.
- Deterministic ordering is `updatedAtUtc DESC`, then `workflowId ASC` using ordinal comparison. `nextPageToken` is an opaque cursor token returned by the previous response.
- Search trims and normalizes the query with Unicode FormKC. Empty, omitted, or whitespace-only `query` values are equivalent to no search filter and do not create a separate freshness domain.
- Searchable fields are `name`, `description`, and `workflowId`. `name` and `description` use ordinal case-insensitive substring matching; `workflowId` supports exact or prefix matching only. Chinese and English text are both matched after the same normalization.
- Query length after trimming/normalization is capped by the response `search.maximumQueryLength`; invalid cursor or overlong query returns `400`.
- Rows expose typed capabilities for `open`, `activity`, `rename`, and `delete`. Unavailable actions carry a typed unavailable reason instead of requiring the client to infer from sources.
- Rows keep `workflowId`, `publishedServiceId`, committed `actorId`, deployment/service IDs, and other identities separate. `workflowId` is the workflow-native merge key; it must not be reused as Team or Member identity.
- `freshness.refreshWatermarkUtc` is the maximum authoritative source `UpdatedAt` materialized into the workflow catalogue row read model. Source and row write `StateVersion` values are derived from authority `UpdatedAt` watermarks; tombstones advance the same watermark so deletes cannot conflict with same-event upserts.

## Scope workflow archive command

Workflow Activity archives a published workflow through the scope-owned command endpoint:

`POST /api/scopes/{scopeId}/workflows/{workflowId}:archive`

The browser supplies only the independently typed `scopeId` and `workflowId`. After `AevatarScopeAccessGuard` validates the caller, the Application service resolves `publishedServiceId`, service app/namespace, and `deploymentId` from the authoritative scope workflow read model and dispatches deployment deactivation. The generic service-identity endpoint is not a browser fallback and its service-principal access requirements remain unchanged.

Archive is accepted-only and preserves the editable draft, published revisions, committed facts, and Activity. The client reports success only after the exact `workflowId` is observed with a deactivated deployment. Permanent deletion, if introduced, requires a separate explicitly named purge contract.

Workflow Activity presents one destructive list action according to the dominant row source:

- draft-only rows expose `Delete draft`;
- published rows expose `Archive`, whether or not a draft source also exists;
- archived rows expose neither Archive nor Delete draft.

Archived rows are removed from the default `all` and `drafts` catalogue views, but their committed facts remain available in the archived view for audit and lifecycle inspection.
