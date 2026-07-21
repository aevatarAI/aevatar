---
title: "NyxID Connected-Service LLM Tools"
status: active
owner: eanzhao
---

# NyxID Connected-Service LLM Tools

把一个 Aevatar service 发布成 NyxID service 后，只要在它的 live OpenAPI operation 上加显式标记，Aevatar 就能在直连 NyxID 的会话里把这些 endpoint 动态注册成独立 LLM 工具。工具调用经 NyxID proxy 下发，凭证注入、proxy/broker 侧审计、approval、node routing、delegation 仍由 NyxID 负责；Aevatar 侧只记录自己的平台 tool invocation 与 typed receipt 审计。

NyxID 始终是唯一真实源：connected instance、catalog definition 与 OpenAPI spec 每次发现都从 NyxID live surface 读取，仓库内不保留 service/endpoint 影子目录，执行始终回到 NyxID proxy。

## 1. 显式标记：`x-aevatar-tool`

注册是 **allow-list**：没有标记的 operation 永远不会变成工具。标记写在 NyxID 返回的 proxy-aware OpenAPI 文档里（vendor extension），分两级：

- **service 级**：写在文档根（或 `info`）。表示该 spec 下的 operation 默认进入候选集。
- **operation 级**：写在单个 operation 上。用于精确开放某些 endpoint，或在 service 级开放的前提下显式排除某个 operation（`enabled: false`）。

标记可以是布尔，也可以是对象：

```yaml
# service 级：整个 spec 的 operation 默认进入候选集
x-aevatar-tool: true

paths:
  /orders/search:
    post:
      operationId: search_orders
      summary: Search orders
      # operation 级：对象形式，可覆盖名称与审批语义
      x-aevatar-tool:
        enabled: true
        name: search_orders     # 可选，工具名后缀；缺省用 operationId
        readOnly: true          # 可选，缺省由 HTTP 方法推断
        destructive: false      # 可选，缺省 false
        approval: auto          # 可选：auto | always | never
      requestBody:
        required: true
        content:
          application/json:
            schema:
              $ref: '#/components/schemas/SearchQuery'
```

准入规则（默认拒绝）：

| operation 标记 | service 标记 | 结果 |
|---|---|---|
| `enabled: true`（或 `true`） | 任意 | 注册 |
| `enabled: false`（或 `false`） | 任意 | 不注册（显式 opt-out） |
| 无 | `enabled: true` | 注册（继承 service 级） |
| 无 | 无 / `false` | 不注册 |

## 2. Caller-scoped discovery authority

执行前的 effective tool discovery 复用同一条 caller-scoped 路径，不建立第二份目录：

1. `GET /api/v1/keys` 是 connected-instance authority，active key 本身就是连接证据。`connected=false` 是显式否决；字段缺失或为 `true` 时，仍须满足 `is_active=true`、`status=active|ready`、caller `allowed != false`、`credential_source.allowed != false` 且具有稳定 `id + slug`，实例才进入 executable tools。
2. `GET /api/v1/catalog` 是 connector definition、canonical name、description 与 icon authority。
3. `GET /api/v1/proxy/services/{connectedServiceId}/openapi.json` 按 connected instance ID 获取 proxy-aware operation contract；不得从 `toolName` 或 slug 前缀猜 connected ID。
4. 未连接、停用、待授权或 caller 无权执行的 catalog service 不进入 effective executable tools。若其他产品表面需要展示，必须作为独立 `unavailable` presentation item 返回，而不能伪装成可执行工具。

`NyxIdConnectedServiceToolSource` 使用 typed DTO 保留以下身份，不把它们塞进 metadata bag：`connectedServiceId`、实例 `serviceSlug`、`catalogServiceSlug`、`connectionLabel`、`connectorDisplayName`。user/org token 仍按当前 caller scope 分别发现并执行，结果不进入进程级缓存。

## 3. 工具形态

每个准入的 operation 映射为一个独立 `IAgentTool`：

- **Name / `toolName`**：每个 operation 一个调用协议 ID，格式为 `nyxid_{service_slug}__{name|operationId}`，稳定可预测；超长时按稳定哈希截断。同名冲突时保留第一个并打 `LogWarning`，其余丢弃（避免给模型歧义工具）。它不是 connector、connection 或 catalog identity，消费方不得解析其前缀。不提供 caller-facing `slug/method/path/body` 通用代理工具，也不新增 `nyxid_service_request` / `NyxIdServiceRequestTool`。
- **Description**：取 OpenAPI `summary`/`description`，附带 service slug + `METHOD path`。
- **ParametersSchema**：从结构化 OpenAPI 生成，不做字符串拼装。
  - `path` / `query` / `header` 参数各自成为顶层属性；`path` 参数恒为 required。
  - JSON `requestBody` 放在 `body` 属性下（若已有名为 `body` 的参数则退化为 `request_body`）。
  - `#/components/schemas/*` 的 `$ref` 会被内联成自包含 JSON Schema（带环保护）。
- **审批语义**：默认 `ApprovalMode.Auto`；`GET`/`HEAD` 默认 `IsReadOnly=true`；写操作默认非只读。标记里的 `readOnly` / `destructive` / `approval` 可覆盖。NyxID 仍在服务端做自己的 approval 判定。

### Typed tool-card presentation

每个 `IAgentTool` 另行提供 provider-owned `ToolPresentationDescriptor`。它至少包含 `invocation_name`、`display_name`、`description`、typed `kind`、typed `availability` / `unavailable_reason`，以及下列 `source_ref` oneof 之一：

- `BuiltInToolRef`
- `NyxIdOperationRef`
- `McpToolRef`
- `SkillRef`

`NyxIdOperationRef` 显式保留 `connected_service_id`、`service_slug`、`catalog_service_slug`、`connection_label`、`connector_display_name`、operation ID、HTTP method 与 path template。`web_fetch`、NyxID account/catalog/proxy 等 repository-owned built-in、MCP 与 skill tool 使用同一 descriptor 模型；只有未知实现使用 generic fallback。

`TOOL_CALL_START` 在 committed progress 中快照 descriptor，并把同一 clone 写入 completion snapshot。参数决定展示身份的 provider 使用 `IAgentTool.ResolvePresentation(argumentsJson)`；例如 `use_skill` 保持 `invocation_name=use_skill`，同时从结构化 `skill` 参数快照实际 `SkillRef.skill_name`。后续 provider 在执行中或执行后重命名 connection、connector 或 skill 时，既有历史卡片继续显示调用开始时的身份；`TOOL_CALL_END` 和 replay 只消费已提交快照，不重新发现或重算展示身份。

## 4. 执行链路

```
LLM tool_call
  -> ConnectedServiceProxyTool.ExecuteAsync
       从 AgentToolRequestContext 读取 NyxID token（user / org 双 token）
       用 tool args 还原 path/query/header/body
  -> NyxIdApiClient.ProxyRequestAsync(token, slug, path, method, body, headers)
  -> NyxID /api/v1/proxy/s/{slug}/{path}
  -> NyxID 注入凭证 / 审计 / approval / node routing / delegation
  -> 下游 service
```

token 可见性与 `NyxIdProxyTool` 一致：user token 优先，org-only 的 service 用 org token 下发。token 只从 `AgentToolRequestContext` 读取，不落盘、不缓存。

### Typed authorization blocker

NyxID proxy 的权限错误只按真实 structured contract 分类：仅 HTTP `401` + `unauthorized` + `1001` 这一精确组合表示 credential 无效或过期。`ConnectedServiceProxyTool` 与 `NyxIdProxyTool` 都通过同一个 result-receipt 边界生成 `AgentToolReceipt(status=AUTHORIZATION_REQUIRED)`，其中携带 typed `NyxIdAuthorizationRequiredEvent`。不使用 exception message、LLM 文案或 JSON substring 猜测权限状态。

HTTP `403` / `forbidden` / `1002` 本身不表示需要重新连接。approval policy denial、approval timeout、scoped permission denial 与普通 upstream `403` 都保持为 safe typed `AgentToolReceipt(status=ERROR)`，不会生成 authorization blocker。只有 connected-service discovery 确认 service 缺失，或模型调用 `nyxid_require_service` 提供 Aevatar-owned positive evidence 时，才生成 typed connection blocker。

失败、拒绝或阻塞 receipt 只保留 `service_slug`、可选 `service_label`、去除 query/fragment 的可选 `resource_uri`、`reason_code` 与 `safe_message`。这类调用的上游 raw error body、credential、token 与 secret-bearing arguments 不进入 receipt、actor state、history、Role completion、AGUI payload 或日志；非成功 tool result 统一替换成 receipt 的 safe `result_json`。

NyxIdChat 收到该 receipt 后提交 `RoleChatSessionOutcome.BLOCKED`，Projection Pipeline 依次映射为 `CUSTOM nyxid.authorization.required` 与 `RUN_FINISHED(status=blocked)`。该事实只终止当前 turn，不创建 `PendingToolApprovalState`，也不触发 `:approve` continuation。缺少整个 connected service 时，`nyxid_require_service` 提供相同的 deterministic typed blocker 路径。

平台审计只在 canonical tool chain 的 `ToolExecutionAuditMiddleware` 中完成。它消费
typed `AgentToolExecutionContext`、`ToolCallContext.CredentialSource` 和最终
`AgentToolReceipt`，写入 Aevatar 的 `AuditRecord`；默认不记录完整 tool
arguments、完整 result 或 `receipt.result_json`。connected-service proxy 不
复制 NyxID broker 的 credential-injection/proxy-level audit，也不从 metadata
bag 或 telemetry span 推导审计事实。

## 5. 启用方式（route policy 决定）

动态工具放在独立 tool set `nyxid.connected_services`（`ToolSetNames.NyxIdConnectedServices`），**默认不并入 `workspace.default`**，避免把每个用户的 connected service 默认注入给模型。要启用时，让 chat route policy 的 `forward_to_model.tool_set_ref` 指向该 tool set，或指向一个 include 了它的组合 tool set。

发现发生在请求期的工具分类阶段：tool-set 边界（`ToolSetResponsesToolProvider`）会把请求的 `AgentToolExecutionContext` 通过 `AgentToolContextScope` 发布到 AsyncLocal，`NyxIdConnectedServiceToolSource.DiscoverToolsAsync` 据此拿到当前用户的 NyxID token 并 live 发现。未配置 NyxID base URL 或上下文里没有 token 时，不暴露任何动态工具。

Voice realtime attach 也遵循同一边界。若 live transport lease 带有可用的
`voice-tool:` credential ref，session-readiness discovery 会解析该 ref，并为
该 lease 构建 caller-scoped snapshot；它不得读取或写入匿名的进程级 voice
catalog cache。匿名 voice session 仍可复用 no-token catalog cache；带 caller
token 的发现必须保持 request/lease scoped，因为 connected-service 可见性由
bearer 决定。

## 6. 架构边界

- 发现期可请求 NyxID live surface，但**不在中间层保存 service/endpoint 事实状态**；没有进程内 catalog。
- 不新增 NyxID endpoint / 字段 / 协议；只消费现有 `/keys`、`/catalog`、`proxy/services/{connectedServiceId}/openapi.json`、`proxy/s/{slug}/...`。
- 不绕过 NyxID proxy 直接打下游 base URL。
- 不引入新的投影主链或 read model。
- 不在 Aevatar 侧维护 shadow service catalog；tool schema、path、query、header、body 均来自该 operation 的 live OpenAPI contract。

## 7. `QuotaLedger` profile

审批额度账本使用同一条 connected-service 边界。`QuotaLedger` 不是 Aevatar 内部账本，也不是 NyxID 新能力；它只是一个外部 REST service profile，契约见 [approval-quota-ledger.openapi.yaml](../contracts/approval-quota-ledger.openapi.yaml)，权威口径见 [approval-quota-ledger.md](approval-quota-ledger.md)。

注册时把该 OpenAPI spec 挂到现有 NyxID service 上，Aevatar 仍通过 NyxID live discovery 读取 spec，并通过 `proxy/s/{slug}/...` 调用 `GET /balances`、`POST /balances/reserve`、`POST /balances/deduct` 和 `POST /balances/release`。余额、reservation、deduction transaction 都归外部 ledger 或渠道原生账本拥有；Aevatar 只传递强类型字段和稳定 idempotency key。

如果目标渠道已经原生维护额度，优先记录渠道 API、scope 和真实 subject probe，再直接使用渠道原生账本。渠道自动扣减时不得再调用 `QuotaLedger` deduct。

## 8. 相关代码

- `src/Aevatar.AI.ToolProviders.NyxId/NyxIdConnectedServiceToolSource.cs`
- `src/Aevatar.AI.ToolProviders.NyxId/ConnectedServices/`（marker / 解析 / schema 内联 / 命名 / proxy tool）
- `src/Aevatar.AI.ToolProviders.NyxId/NyxIdApiClient.cs`（`GetProxyServiceOpenApiAsync`）
- `src/Aevatar.AI.ToolProviders.ToolSetRegistry/ToolSetNames.cs`
- `src/platform/Aevatar.GAgentService.Application/Responses/ResponsesDirectToolPlanService.cs`（context-scope seam）
- `src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs`（tool set 注册）
