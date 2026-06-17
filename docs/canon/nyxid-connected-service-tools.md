---
title: "NyxID Connected-Service LLM Tools"
status: active
owner: eanzhao
---

# NyxID Connected-Service LLM Tools

把一个 Aevatar service 发布成 NyxID service 后，只要在它的 OpenAPI operation 上加显式标记，Aevatar 就能在直连 NyxID 的会话里把这些 endpoint 动态注册成独立 LLM 工具。工具调用经 NyxID proxy 下发，凭证注入、审计、approval、node routing、delegation 仍由 NyxID 负责。

NyxID 始终是唯一真实源：service 列表与 OpenAPI spec 每次发现都从 NyxID live surface 读取，仓库内不保留 service/endpoint 影子目录，执行始终回到 NyxID proxy。

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

## 2. 工具形态

每个准入的 operation 映射为一个独立 `IAgentTool`：

- **Name**：`nyxid_{service_slug}__{name|operationId}`，稳定可预测；超长时按稳定哈希截断。同名冲突时保留第一个并打 `LogWarning`，其余丢弃（避免给模型歧义工具）。
- **Description**：取 OpenAPI `summary`/`description`，附带 service slug + `METHOD path`。
- **ParametersSchema**：从结构化 OpenAPI 生成，不做字符串拼装。
  - `path` / `query` / `header` 参数各自成为顶层属性；`path` 参数恒为 required。
  - JSON `requestBody` 放在 `body` 属性下（若已有名为 `body` 的参数则退化为 `request_body`）。
  - `#/components/schemas/*` 的 `$ref` 会被内联成自包含 JSON Schema（带环保护）。
- **审批语义**：默认 `ApprovalMode.Auto`；`GET`/`HEAD` 默认 `IsReadOnly=true`；写操作默认非只读。标记里的 `readOnly` / `destructive` / `approval` 可覆盖。NyxID 仍在服务端做自己的 approval 判定。

## 3. 执行链路

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

## 4. 启用方式（route policy 决定）

动态工具放在独立 tool set `nyxid.connected_services`（`ToolSetNames.NyxIdConnectedServices`），**默认不并入 `workspace.default`**，避免把每个用户的 connected service 默认注入给模型。要启用时，让 chat route policy 的 `forward_to_model.tool_set_ref` 指向该 tool set，或指向一个 include 了它的组合 tool set。

发现发生在请求期的工具分类阶段：tool-set 边界（`ToolSetResponsesToolProvider`）会把请求的 `AgentToolExecutionContext` 通过 `AgentToolContextScope` 发布到 AsyncLocal，`NyxIdConnectedServiceToolSource.DiscoverToolsAsync` 据此拿到当前用户的 NyxID token 并 live 发现。未配置 NyxID base URL 或上下文里没有 token 时，不暴露任何动态工具。

## 5. 架构边界

- 发现期可请求 NyxID live surface，但**不在中间层保存 service/endpoint 事实状态**；没有进程内 catalog。
- 不新增 NyxID endpoint / 字段 / 协议；只消费现有 `proxy/services`、`proxy/services/{id}/openapi.json`、`proxy/s/{slug}/...`。
- 不绕过 NyxID proxy 直接打下游 base URL。
- 不引入新的投影主链或 read model。

## 6. `QuotaLedger` profile

审批额度账本使用同一条 connected-service 边界。`QuotaLedger` 不是 Aevatar 内部账本，也不是 NyxID 新能力；它只是一个外部 REST service profile，契约见 [approval-quota-ledger.openapi.yaml](../contracts/approval-quota-ledger.openapi.yaml)，权威口径见 [approval-quota-ledger.md](approval-quota-ledger.md)。

注册时把该 OpenAPI spec 挂到现有 NyxID service 上，Aevatar 仍通过 NyxID live discovery 读取 spec，并通过 `proxy/s/{slug}/...` 调用 `GET /balances`、`POST /balances/reserve`、`POST /balances/deduct` 和 `POST /balances/release`。余额、reservation、deduction transaction 都归外部 ledger 或渠道原生账本拥有；Aevatar 只传递强类型字段和稳定 idempotency key。

如果目标渠道已经原生维护额度，优先记录渠道 API、scope 和真实 subject probe，再直接使用渠道原生账本。渠道自动扣减时不得再调用 `QuotaLedger` deduct。

## 7. 相关代码

- `src/Aevatar.AI.ToolProviders.NyxId/NyxIdConnectedServiceToolSource.cs`
- `src/Aevatar.AI.ToolProviders.NyxId/ConnectedServices/`（marker / 解析 / schema 内联 / 命名 / proxy tool）
- `src/Aevatar.AI.ToolProviders.NyxId/NyxIdApiClient.cs`（`GetProxyServiceOpenApiAsync`）
- `src/Aevatar.AI.ToolProviders.ToolSetRegistry/ToolSetNames.cs`
- `src/platform/Aevatar.GAgentService.Application/Responses/ResponsesDirectToolPlanService.cs`（context-scope seam）
- `src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs`（tool set 注册）
