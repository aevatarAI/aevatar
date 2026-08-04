---
title: "Aevatar /status 状态面板架构"
status: active
owner: eanzhao
---

# Aevatar /status 状态面板架构

## 1. 目的

本文定义 Mainnet Host 的 `/status` 状态面板架构与实现边界。

核心口径：

1. `/status` 是展示面，只返回静态 HTML。
2. `/api/status` 是查询面，只读取按 target 覆盖写入的 `HealthProbeOperationalSnapshot`。
3. 探针执行由 `HealthProbeTargetGAgent` 按 target 独立运行，不在 HTTP 请求路径里现场执行。
4. 只有 target 配置是持久化业务事实；采样结果是可在重启后清空的运维快照，不进入 EventStore 或 Projection Pipeline。
5. 这套机制用于持续可用性探测与诊断，不替代 CI 中的行为回归测试。

## 2. 模块位置

| 模块 | 路径 | 职责 |
|---|---|---|
| Host endpoint | `src/Aevatar.Mainnet.Host.Api/Status/StatusEndpoints.cs` | 暴露 `/status` 与 `/api/status` |
| HTML dashboard | `src/Aevatar.Mainnet.Host.Api/Status/StatusHtml.cs` | 自包含页面，轮询 `/api/status` 渲染状态 |
| Host composition | `src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs` | 注册 `AddStatusDashboard` 与 host 侧 freshness source |
| StatusDashboard agent module | `agents/Aevatar.GAgents.StatusDashboard/` | 探针 actor、执行器、operational snapshot port、查询端口 |
| Proto contract | `agents/Aevatar.GAgents.StatusDashboard/protos/status_dashboard.proto` | target descriptor、配置事件、运行时消息、operational snapshot 契约 |
| Mainnet snapshot adapter | `src/Aevatar.Mainnet.Host.Api/Status/ElasticsearchHealthProbeOperationalSnapshotStore.cs` | 通过独立 Elasticsearch alias 覆盖读写运维快照 |
| Channel freshness source | `src/Aevatar.Mainnet.Host.Api/Status/ChannelBotRegistrationFreshnessSource.cs` | 将 channel-bot registration readmodel 暴露为 freshness source |

## 3. 总体链路

```mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
flowchart LR
    CFG["Aevatar:Status 配置"] --> MAN["StatusDashboardManifest"]
    MAN --> START["HealthProbeStartupService 启动 + 每分钟 reconcile"]
    START --> DISPATCH["IActorDispatchPort"]
    DISPATCH --> ACT["HealthProbeTargetGAgent"]
    ACT --> TICK["Ephemeral delayed self-message"]
    TICK --> ACT
    ACT --> EXEC["IHealthProbeExecutor"]
    EXEC --> OUT["HealthProbeOutcome"]
    OUT --> STATE["Actor runtime sampling state"]
    STATE --> SNAP["HealthProbeOperationalSnapshot overwrite"]
    SNAP --> API["/api/status"]
    API --> HTML["/status"]
```

这条链路的关键点是：HTTP endpoint 不执行探针，也不触发投影补跑。它只读取当前 manifest slug 对应的 operational snapshot。snapshot 没有 `StateVersion`、event id、reducer、projection scope 或 watermark，不是 readmodel，也不是权威业务事实。

## 4. 启动与配置

`AddStatusDashboard(configuration)` 注册整套状态面板能力：

1. 绑定 `Aevatar:Status` 到 `StatusDashboardOptions`。
2. 注册默认 executor：`HttpStatusProbeExecutor` 与 `ReadmodelFreshnessProbeExecutor`。
3. 注册 `IHealthProbeExecutorRegistry`。
4. 注册默认的 `InMemoryHealthProbeOperationalSnapshotStore`。
5. 注册 `IHealthStatusQueryPort`。
6. 注册 `HealthProbeStartupService`。
7. Mainnet Host 按 document provider 将 snapshot store 替换为 Elasticsearch adapter，或显式保留 InMemory adapter。
8. Elasticsearch 分支把 dedicated alias 注册为 startup reconcile target；actor 读写路径不执行 index lifecycle。

`HealthProbeStartupService` 在 host 启动时读取 manifest，并每分钟对有效 target 重申一次同一份配置。每个有效 target 会执行：

1. 用 `HealthProbeStoreCommands.BuildActorId(slug)` 得到稳定 actor id。
2. 仅在旧 health projection scope 已存在时直接投递 typed release command；通过 typed attach-existing lease lookup 清理其嵌套 `projection-scope-status` scope。迁移清理绝不创建新 scope，集合同时覆盖当前 manifest 与 `RetiredStatusProbeTargets`。
3. 通过 `HealthProbeStoreCommands.DispatchConfigureAsync(...)` 创建或获取 `HealthProbeTargetGAgent`，再投递 `HealthProbeConfigureCommand`；周期 reconcile 只重复这一步。
4. actor activation best-effort 清理旧实现遗留的 durable callbacks，然后启动新的 ephemeral tick 链。

启动 pass 负责一次性历史清理与 configure dispatch；周期 reconcile 不执行探针、不缓存 target 运行态，只用幂等 configure 在滚动发布后重新激活丢失进程内 tick 的 actor。Actor 仍独占长期探测调度和采样状态。正常路径不注册 health projection hook、materialization runtime 或 readmodel descriptor。

## 5. Probe Actor

每个 target 对应一个 `HealthProbeTargetGAgent`。

Actor 的持久化事实只有 `HealthProbeTargetDescriptor`。Actor 运行时持有：

1. `HealthProbeTargetDescriptor`：target slug、显示名、类别、probe kind、interval、timeout、enabled、executor 参数。
2. `LastOutcome`：最近一次探针结果。
3. `ConsecutiveFailures`：连续失败次数。
4. `LastSuccessAt` / `LastCheckAt`。
5. `RecentOutcomes`：最近窗口内的样本，当前最多保留 120 条，并按两小时窗口裁剪。

其中第 1 项通过 `HealthProbeConfigured` 恢复；第 2–5 项只存在于 actor-owned runtime state，并覆盖写入 operational snapshot。

Actor 处理两类消息：

| 消息 | 来源 | 行为 |
|---|---|---|
| `HealthProbeConfigureCommand` | startup / reconcile service | descriptor 变化时持久化 `HealthProbeConfigured`；actor 激活时安排下一次 self tick |
| `HealthProbeTickRequested` | ephemeral delayed continuation | 在 actor turn 中登记 active execution 并调用 executor |
| `HealthProbeCompletedEvent` | executor completion continuation | 按 `operation_id` 对账、更新 runtime state、覆盖 snapshot、安排下一次 tick |
| `HealthProbeTimeoutFiredEvent` | ephemeral delayed continuation | 按 `operation_id` 对账 timeout、覆盖 snapshot、安排下一次 tick |

tick 与 timeout 使用 `Task.Delay(..., TimeProvider, actorLifetimeToken)`。延迟 continuation 只发布 typed self-message，不读写 actor state；所有状态变化仍在 actor 单线程 handler turn 中完成。禁用 target 时不再重排 tick，actor 停用会取消 tick、timeout 和正在执行的 executor。

changed configure 只提交一个 `HealthProbeConfigured`；未变化的 configure 不提交事件。正常 sampling 不提交 `HealthProbeObserved`、`HealthProbeExecutionStarted` 或 `HealthProbeExecutionCleared`，不注册 durable callback，也不启动 projection scope。旧消息和 reducer 仅保留用于历史事件流回放；activation 回放后丢弃旧 sampling 字段，以空 history 开始运行。

## 6. Executor 边界

`IHealthProbeExecutor` 是 probe kind 的执行策略接口。Executor 是普通 DI 服务，但只由 `HealthProbeTargetGAgent` 在 tick 里调用。

默认 executor：

| Kind | 实现 | 用途 |
|---|---|---|
| `http_status` | `HttpStatusProbeExecutor` | 发送一次 HTTP 请求，按 status code 与 body assertion 分类结果 |
| `readmodel_freshness` | `ReadmodelFreshnessProbeExecutor` | 读取某个注册的 `IReadmodelFreshnessSource`，检查数量与更新时间 |

Mainnet Host 额外注册 `aevatar_core_loop` 与 `audit_query_index` executor。前者不调用 LLM、不创建 run、不调用固定 actor/team，只验证 host 组合层是否仍具备 core-loop 所需能力：

1. `workspace.default` tool set 可解析。
2. 四个 Aevatar invocation tools 可发现，且具备 description、parameters schema 与 `IAevatarInvocationTool` 契约。
3. route policy 使用 `ForwardToModel + tool_choice_hint` 表达 `aevatar_invoke_gagent`、`aevatar_invoke_team`、`aevatar_start_workflow` 目标；旧 GAgent/team wire action 已删除。
4. `wait=complete` 在 `aevatar_invoke_gagent`、`aevatar_invoke_team`、`aevatar_start_workflow` 上只保留公开参数与 accepted/streaming receipt 语义；完成态由 typed `aevatar_observe_run` target 读取单一 readmodel 获取，不再进入 ChatRun completion 协调。普通 workflow 查询走 workflow-owned `workflow_actor_current_state`、`workflow_status`、`event_query`。

`audit_query_index` 通过 `IAuditTrailQueryPort` 执行一次有界未来时间窗查询，同时验证 audit alias、mapping、Elasticsearch query 与空结果反序列化。它只返回脱敏后的成功/失败分类，不把 Elasticsearch URL、凭证或原始异常写入 health actor state。

`http_status` 支持的常用参数：

| 参数 | 含义 |
|---|---|
| `Url` | 必填，绝对 URL，支持 `${configuration:Key}` 占位符 |
| `Method` | 默认 `GET` |
| `ExpectedStatuses` | 默认 `200`，逗号分隔 |
| `ContentType` | 默认 `application/json` |
| `Body` | 请求体，支持配置占位符 |
| `Header.{name}` | 请求头，支持配置占位符 |
| `ExpectedBodyContains` / `ExpectedBodyRegex` | 成功响应必须包含的 body 条件 |
| `ForbiddenBodyContains` / `ForbiddenBodyRegex` | 成功响应不得包含的 body 条件 |
| `DegradedOnNon2xx` | unexpected non-2xx 是否标为 degraded，而不是 down |
| `Auth.Mode` | 可选认证模式：`none`、`static_bearer`、`client_credentials`、`scope_service_token`、`auto` |
| `Auth.StaticBearerConfigurationKey` | `static_bearer` 使用的配置键 |
| `Auth.TokenEndpoint` | `client_credentials` 使用的 OAuth token endpoint |
| `Auth.ClientIdConfigurationKey` / `Auth.ClientSecretConfigurationKey` | `client_credentials` 使用的 client id / secret 配置键 |
| `Auth.ClientCredentialsScope` | `client_credentials` 请求的 OAuth scope |
| `Auth.ScopeId` | `scope_service_token` 使用的 scope id（自签 token 的 `scope_id` claim，须与被探端点路径一致） |

`scope_service_token` 让 host 用 `IScopeServiceTokenIssuer` 自签一个短生命周期 scope service token（Bearer，带 `scope_id` claim），用于探测 host 自身的鉴权端点并断言真实成功（200）。token 由 host 侧 `IProbeServiceTokenProvider` 按 scope 缓存、临到期前刷新。当 scope service tokens 未启用（provider 缺失）或拿不到 token 时，executor 把该探针记为 `unknown`（detail `credential_unavailable`），绝不发未鉴权请求、绝不误报 `down`。解析出的 Bearer 即使 target 未声明 `Header.Authorization` 也会自动附加。

`static_bearer` 用于长期机器 bearer，例如 NyxID API key / Agent Key。不要把人工登录产生的短期 access token 配成这里的生产值；它会过期，过期后整组探针会一起 401。

`client_credentials` 用于上游支持的 service account token。若某个业务入口需要用 bearer 解析真实调用者身份，探针必须先确认该入口支持 service account 语义，否则不要把它放入默认状态面板。

`readmodel_freshness` 支持的参数：

| 参数 | 含义 |
|---|---|
| `Source` | 必填，对应 `IReadmodelFreshnessSource.Name` |
| `MinCount` | 最小记录数，低于该值返回 degraded |
| `StaleAfterSeconds` | 若 source 提供 `LastUpdatedAt`，超过阈值返回 degraded |

## 7. Operational snapshot 与查询

`HealthProbeOperationalSnapshot` 保存 target descriptor、最后结果、连续失败数、成功/检查时间、最多 120 条且不超过两小时的近期结果，以及 overwrite 时间。它只服务状态面板查询，不具备领域事实或 readmodel 语义。

存储选择：

1. Mainnet Elasticsearch：alias 为 `{normalized-prefix}-health-probe-operational-snapshots`，按 slug 执行无条件 `PUT /{alias}/_doc/{slug}` 覆盖；alias 由现有 startup reconcile hosted service 创建或迁移。
2. Development/test：`InMemoryHealthProbeOperationalSnapshotStore`，读写均 clone；进程重启自然清空。
3. actor 读写不会触发 index 创建、mapping repair、reindex 或 projection lifecycle。

`HealthStatusQueryPort` 只读 snapshot store：

1. `ListAllAsync` 只查询当前 manifest 仍声明的 slug。
2. `GetBySlugAsync` 只在 slug 属于当前 manifest 时读取。
3. 不创建 actor、不激活 projection、不读取 EventStore、不执行 replay 或 priming。
4. 缺失 snapshot 会被诚实省略，storage failure 会向上返回，不能伪造健康结果。

actor/backend 重启会把 last outcome、连续失败数和近期 history 重置为空；配置从 `HealthProbeConfigured` 重放后重新开始采样。这是明确的产品语义，不需要从旧 readmodel 或 EventStore 恢复。

## 8. HTTP Surface

`/status`：

1. 返回 `StatusHtml.Page`。
2. 页面无构建步骤、无外部静态资产依赖。
3. 浏览器端轮询 `/api/status`。

`/api/status`：

1. 调用 `IHealthStatusQueryPort.ListAllAsync`。
2. 将 `HealthProbeOperationalSnapshot` 映射成既有 JSON（含 `severity`），外部字段不变。
3. 聚合 overall（按 severity 加权、诚实口径）：
   - 只统计 status 已知（ok/degraded/down）的 enabled target；status 为 `unknown`（例如未配置凭证的 canary）一律排除，永不把面板拖红。
   - 任一 `critical` target 为 `down` → overall `down`。
   - 否则任一 target 为 `degraded`，或任一非 critical target 为 `down` → overall `degraded`。
   - 否则存在已知 ok → `ok`；否则 `unknown`。
   - 口径目的：liveness 绿不能掩盖 critical 业务面 down；非 critical 失败降级但不黑屏。
4. 计算最近样本窗口的 availability。

`/api/status` 返回的是可覆盖、可丢失的当前运维快照，不承诺持久化或强一致。调用方应结合 `last_check_at`、`last_success_at` 与 `updated_at_utc` 判断新鲜度；不得把缺失历史解释为业务事实丢失。

## 9. 内置 Targets

当 `Aevatar:Status:Targets` 为空且 `UseBuiltInTargets=true` 时，`StatusDashboardManifest` 会生成 mainnet 默认 target 集。

默认类别（`category` 即展示分层）：

| Category | 含义 |
|---|---|
| `self` | 当前 Mainnet Host 的 liveness / readiness |
| `llm` | OpenAI 兼容 LLM ingress（`/v1/*`） |
| `studio` | Studio / App 对外匿名健康面（`/api/health`、`/api/app/context`） |
| `feature` | Aevatar 核心功能面或内部 readmodel freshness |
| `upstream` | NyxID 等上游依赖 |

每个 target 另有 `severity`（`critical` / `standard` / `canary`），用于 §8 的诚实 overall 聚合。`critical` 表示该面 down 即代表产品对用户不可用；`canary` 表示带凭证/付费的深度探针，失败只降级、未配置即 `unknown` 不参与聚合。

无凭证内置 probe（始终开启，全部断言真实成功状态）：

1. `self-liveness`（standard）/ `self-readiness`（critical）。
2. `studio-health`（`/api/health` → 200）、`app-context`（`/api/app/context` → 200）：匿名 200 即真实成功信号。
3. `aevatar-core-loop-tools`（critical），展示当前分支核心的 LLM-driven Aevatar invocation/tool-choice 链路是否在 host 组合层可用。
4. `channel-bot-runtime` readmodel freshness（standard）。
5. `audit-query-index` audit artifact query/index readiness（standard）。
6. NyxID `/health` 与 OIDC discovery 上游探测（standard）。

凭证门控的 LLM canary（仅当配置了 `Aevatar:Status:Probe:CanaryBearer` 时生成，`severity=canary`）：

1. `llm-catalog`：带 canary bearer 读 `GET /v1/models` → 200 + 合法列表 body，证明 LLM ingress 与 NyxID catalog 聚合端到端可用，无模型调用成本。
2. `llm-completion-canary`：带 canary bearer `POST /v1/chat/completions`（最便宜模型、`max_tokens≈8`、prompt `ping`）→ 200 + body 含 `choices`，端到端验证 LLM 真能出结果。默认 15 分钟一次；未配置凭证即不探测（不误报 down）。两者复用 `static_bearer` 认证模式，executor 在探测时从 `Aevatar:Status:Probe:CanaryBearer` 读取密钥。

凭证门控的编排 / observatory 探针（仅当配置了 `Aevatar:Status:Probe:ScopeId` 时生成，`scope_service_token` 认证、断言 200）：

1. `orchestration-scope-read`：带自签 scope service token 读 `GET /api/scopes/{ScopeId}/services` → 200，证明编排读路径与 scope 鉴权端到端可用。
2. `observatory-read`：带自签 scope service token 读 `GET /api/workflow/observatory/me` → 200，证明 observatory 读路径可用。

二者需 `Aevatar:Authentication:ScopeServiceTokens:Enabled=true` + 签名密钥才能真正出 token；未启用时降级为 `unknown`（`credential_unavailable`），不误报 down。不使用 draft-run 工作流 canary：draft-run 对内部工具错误也返回 200，是误导性的“200=ok”信号，违背 §9.1 同一原则。

`Aevatar:Status:Probe` 配置：`CanaryBearer`（真实 NyxID 凭证，空则禁用 LLM canary）、`CanaryModel`（默认 `deepseek/deepseek-v4-flash`）、`CanaryIntervalSeconds`（默认 900）、`CanaryMaxTokens`（默认 8）、`ScopeId`（探针 scope，空则禁用编排/observatory 探针）。

默认内置 target 不包含”期望返回 401”的 auth gate。401 只能证明匿名请求被拦截，不能证明业务链路可用；若探针目标是业务健康，必须显式配置带凭证的 target，并把 `ExpectedStatuses` 设为真实成功状态（通常是 `200`）。生产环境可以显式配置 `Targets` 覆盖内置集合，也可以保留内置集合并只通过配置项调整 base URL、token、timeout 与 interval。

### 9.1 退役探针

`chat-completion-api-singular-route` 已退役。它曾用于探测 `/v1/chat/completion` 单数误用路径是否返回 `404`，但 Mainnet Host 启用全局 auth fallback 后，未带 bearer 的未知 `/v1/*` 请求会先得到 `401`，这条探针只能反映认证中间件顺序，不能稳定证明 OpenAI 兼容入口是否正确。真实入口由 `chat-completions-api-auth-gate` 监控；单数路径不得注册由 Host route composition 测试保证。

默认 auth-gate 探针已退役：`responses-api-auth-gate`、`messages-api-auth-gate`、`chat-completions-api-auth-gate`、`models-api-auth-gate`、`voice-websocket-auth-gate`、`channel-registration-api-auth-gate`、`nyxid-llm-status`、`nyxid-llm-gateway-auth-gate`、`nyxid-channel-bots-auth-gate`、`nyxid-channel-relay-reply-auth-gate`。它们长期把 `http_401` 显示成 `ok`，语义上只是认证边界检查，不是健康检查。

旧的 `responses-forward-team-00` 到 `responses-forward-team-08` 分阶段探针已经退役。这组探针绑定 NyxID proxy、`/v1/responses`、chat-route、Studio Team、member binding 与 direct team invoke，长期依赖预置 token 和固定 team/member 事实，和当前“通过 Aevatar 核心功能由 LLM driven 使用”的方向不一致。

退役后：

1. 内置 manifest 不再生成这些 target。
2. `/api/status` 只返回当前 manifest 中仍声明的 target，旧 snapshot/readmodel 不再展示。
3. `HealthProbeStartupService` 会对这些旧 slug 下发 disabled descriptor，停止旧 actor 的后续 tick。
4. `HealthProbeStartupService` 也会查找并释放这些旧 slug 遗留的 health projection scope 与嵌套 projection-scope-status lease，不让历史 relay 继续放大写入。
5. 若需要临时诊断某条业务链路，应通过 `Aevatar:Status:Targets` 显式增加普通 `http_status` target，或使用业务侧 readmodel / tracing 工具定位，不把固定业务编排长期放进默认状态面板。

## 10. 扩展新探针

### 10.1 新增一个 HTTP target

只需要在 `Aevatar:Status:Targets` 中增加配置：

```json
{
  "Aevatar": {
    "Status": {
      "Targets": [
        {
          "Slug": "nyxid-oidc-discovery",
          "Name": "NyxID OIDC Discovery",
          "Category": "upstream",
          "Probe": "http_status",
          "IntervalSeconds": 60,
          "TimeoutMs": 5000,
          "Parameters": {
            "Url": "${configuration:Aevatar:NyxId:Authority}/.well-known/openid-configuration",
            "Method": "GET",
            "ExpectedStatuses": "200",
            "ExpectedBodyContains": "issuer"
          }
        }
      ]
    }
  }
}
```

### 10.2 新增一个 readmodel freshness source

当探针需要读取某个已物化 readmodel 时，实现并注册 `IReadmodelFreshnessSource`：

```csharp
internal sealed class MyReadmodelFreshnessSource : IReadmodelFreshnessSource
{
    public string Name => "my-readmodel";

    public async Task<ReadmodelFreshnessSnapshot> GetFreshnessAsync(CancellationToken ct)
    {
        var items = await _queryPort.QueryAsync(ct);
        return new ReadmodelFreshnessSnapshot(items.Count, LastUpdatedAt: null);
    }
}
```

注册时使用 `TryAddEnumerable`，再在 target 中使用：

```json
{
  "Slug": "my-readmodel",
  "Name": "My Readmodel",
  "Category": "feature",
  "Probe": "readmodel_freshness",
  "Parameters": {
    "Source": "my-readmodel",
    "MinCount": "1"
  }
}
```

### 10.3 新增一个 executor kind

只有当 HTTP 与 readmodel freshness 都无法表达需求时，才新增 `IHealthProbeExecutor` 实现。

要求：

1. `Kind` 必须稳定、短小、语义明确。
2. 输入来自 `HealthProbeTargetDescriptor.Parameters`。
3. 输出必须是 `HealthProbeOutcome`。
4. executor 不持有跨 target 事实状态。
5. executor 不直接写 actor state、operational snapshot 或其他 store。
6. executor 必须接受 cancellation token，并由 actor 外层 timeout 约束。
7. 注册使用 `TryAddEnumerable(ServiceDescriptor.Singleton<IHealthProbeExecutor, ...>())`。

## 11. 架构边界

必须保持：

1. `/status` 与 `/api/status` 不执行探针。
2. query path 不创建 actor、不激活 projection、不补跑 materialization。
3. target 配置的权威事实只由 `HealthProbeTargetGAgent` 持久化；采样是 actor-owned ephemeral runtime state。
4. executor 只是执行策略，不是事实源。
5. operational snapshot 只是可覆盖、可丢失的运维查询副本，不是 readmodel 或业务事实。
6. 配置占位符只在 executor 边界解析，避免把 secret 写进 actor state 或 snapshot。
7. 对外 JSON 不暴露请求头、token 或原始配置 secret。

允许：

1. startup service 根据 manifest 创建或配置 probe actor。
2. startup service 仅释放已存在的 legacy scopes/callbacks，不创建新的 health projection。
3. executor 调用外部 HTTP API 或读取已存在 query port。
4. host 模块按需注册新的 `IReadmodelFreshnessSource` 或 executor。
5. Mainnet Host 在 startup reconcile 独立 operational alias。

禁止：

1. 在 `/api/status` 中同步调用 LLM、HTTP upstream 或 actor tick。
2. 在 query 方法里 event replay、projection priming 或 readmodel rebuild。
3. 用进程内 `Dictionary<slug, outcome>` 作为生产 snapshot store 或事实源；显式 InMemory dev/test adapter 除外。
4. 让 executor 直接修改 actor state 或 operational snapshot。
5. 把 `/status` 当完整回归测试平台。
6. 在 sampling path 追加 domain event、projection watermark 或 durable callback state。

## 12. 验证

相关测试项目：

| 测试 | 覆盖 |
|---|---|
| `test/Aevatar.GAgents.StatusDashboard.Tests/StatusDashboardManifestTests.cs` | manifest 解析、内置 target、退役 target 屏蔽 |
| `test/Aevatar.GAgents.StatusDashboard.Tests/HealthProbeTargetGAgentTests.cs` | configure-only persistence、ephemeral tick/timeout、异常、历史裁剪、重启/停用 |
| `test/Aevatar.GAgents.StatusDashboard.Tests/HttpStatusProbeExecutorTests.cs` | HTTP executor status/body/header/timeout 行为 |
| `test/Aevatar.GAgents.StatusDashboard.Tests/ReadmodelFreshnessProbeExecutorTests.cs` | freshness executor 分类逻辑 |
| `test/Aevatar.Capabilities.Tests/ElasticsearchHealthProbeOperationalSnapshotStoreTests.cs` | exact-key overwrite、读取、404 与 startup alias reconcile |
| `test/Aevatar.Capabilities.Tests/MainnetStatusEndpointsTests.cs` | mainnet `/status` 与 `/api/status` endpoint |
| `test/Aevatar.Capabilities.Tests/MainnetHostCompositionTests.cs` | Mainnet host 注册与 `aevatar_core_loop` executor 可用性 |

常用验证命令：

```bash
dotnet test test/Aevatar.GAgents.StatusDashboard.Tests/Aevatar.GAgents.StatusDashboard.Tests.csproj --nologo
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --filter MainnetStatusEndpointsTests --nologo
```

若修改测试或新增测试，还必须执行：

```bash
bash tools/ci/test_stability_guards.sh
```
