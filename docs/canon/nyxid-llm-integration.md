---
title: "NyxID LLM Provider 集成指南"
status: active
owner: eanzhao
---

# NyxID LLM Provider 集成指南

Aevatar 的 Agent 可以通过 NyxID LLM Gateway 使用用户在 NyxID 上配置的 LLM API Key（OpenAI、Anthropic、DeepSeek 等），无需在 Aevatar 端存储任何密钥。

## Durable authorization catalog lifecycle authority

NyxID catalog snapshots are owned by one catalog actor per authenticated `authority + owner_kind + owner_subject`. This identity is independent of Aevatar `scopeId`; adapters must not derive one from the other. Host and Identity adapters may use a transient bearer to read the external catalog, but dispatch only secret-free typed activation, observation, refresh-failure, invalidation, or cleanup commands. The actor commits the corresponding domain event and the unified projection pipeline materializes its actor-scoped current-state replica.

Activation is committed before an external refresh begins. Every refresh reads the actor-issued `lifecycle_fence` from the current-state replica and includes it as `expected_lifecycle_fence` on the typed begin command. A stale epoch commits a correlated `Superseded` outcome. While a refresh is active in the same epoch, contenders are ordered by `(started_at, refresh_id)`; after an observed, failed, invalidated, or cleaned terminal transition, the actor advances the fence and clears active ownership. New terminal events carry exactly the next epoch; replay applies at least `current fence + 1` for every terminal and activation never reduces an already migrated fence, so historical events that predate the fence fields still establish distinct epochs. State and lifecycle events carry a typed lifecycle-fence semantics version. After replay or snapshot restore, a persisted legacy state commits one actor-owned migration event before serving commands, advances the fence once, clears and supersedes any restored active refresh, and publishes the migrated state root through the normal projection pipeline; fresh empty actors skip this migration. The actor retains no wall-clock watermark across terminal epochs, so delayed prior-epoch begins remain fenced while a legitimate later refresh can start after clock rollback. A successful observation activates or refreshes the snapshot; a `401/403` response or explicit binding revocation invalidates it immediately; transient provider failures record a failure without extending `fresh_until`.

Cleanup is stronger than invalidation: it clears services, observation freshness, revision, and content digest while retaining owner identity and a terminal reason. Invalidation and cleanup both produce projected tombstones, including when the actor has never published a successful observation. Consumers can therefore distinguish `missing` from an actor-owned `invalidated` or `cleaned` state through the projected `state_version`, `lifecycle_fence`, and lifecycle fields. Scheduling reads this replica only and never fetches, refreshes, replays, or primes NyxID inside the query call stack.

## Atomic selection and durable model evidence

`LLMSelection` is the atomic UserConfig route/model fact. It contains the route kind and canonical route identity together with either `ProviderDefault` or one explicit model. `UserConfigGAgent` is its only authoritative owner: settings, channel commands, and preset flows submit a complete selection, the actor commits it, and the unified projection pipeline publishes the actor-scoped current-state replica. Compatibility `default_model` and `preferred_llm_route` strings are derived reads only; no normal write can update either string independently.

Reset commits a complete `Unspecified` selection and the product displays it as System default, not Gateway. Gateway is a separate typed selection with the canonical `/api/v1/llm/gateway/v1` route. A saved route that becomes unavailable remains visible with typed Retry, Choose replacement, or Reselect remediation; runtime and UI must not silently fall back to Gateway or another provider.

An accepted UserConfig receipt means accepted-for-dispatch only. The UI shows Update submitted with the command ID until the current-state projection observes the exact submitted `LLMSelection`; only that equality makes the selection Active.

Durable execution requires `ExplicitModel` plus exact `Enumerated` evidence from the committed authorization catalog for the same route. In other words, only enumerated committed catalog evidence can authorize a durable route/model pair. `NotVerifiable`, `Unavailable`, a missing snapshot, an empty model list, a model outside the ordinal exact list, or Gateway without its own committed evidence all fail closed. An empty list is never an open identifier set.

The evidence path has one owner and one projection path:

```text
configured NyxID authority + verified canonical owner
  -> bounded catalog refresh
  -> authorization catalog actor commit
  -> unified current-state projection
  -> planner exact route/model match and permission digest
  -> persisted authorization fact and identical runtime payload
  -> runtime exact match before actor inbox
```

Refresh destinations come only from configured NyxID authority and verified canonical identity; untrusted caller route strings, URLs, labels, slugs, and model prefixes cannot invent a network destination. Query and planner paths do not refresh catalogs, replay events, prime projections, or perform external I/O. The legacy bare-model fallback described for external Responses clients is not authority for UserConfig, scheduling, or workflow execution.

## Scope model catalog discovery policy

`LLMModelCatalogPolicyGAgent` is the sole authority for the model discovery policy exposed by
`GET /v1/models`. There is one platform owner and one owner for each `scopeId`. A replace command
uses `expectedStateVersion` for optimistic concurrency and `mutationId` for idempotency; its
`202 Accepted` receipt means accepted for dispatch only. The actor commits the policy, publishes
the committed state through the unified Projection Pipeline, and
`LLMModelCatalogPolicyCurrentStateDocument` becomes the actor-scoped current-state query replica.
Readers never replay events, refresh NyxID, or prime that projection in the query call stack. An
Admin client reports the change as active only after a later GET observes both a higher
`stateVersion` and `lastMutationId` equal to its submitted `mutationId`. A higher version carrying a
different mutation means another update won; the client must load that latest policy and ask the
user to retry instead of reporting a false success.

The two policy owners have deliberately different portable identities:

- The platform policy is always `custom_replace`. Each source stores an exact NyxID
  `catalogServiceId`, so an administrator can publish a default source list for all scopes without
  storing one user's credential binding.
- A scope policy is either `inherit_platform` or `custom_replace`. A custom source stores the exact
  caller-owned `userServiceId`. `custom_replace` is a complete replacement: an explicitly empty
  source list is valid, remains empty, and never falls back to the platform policy.
- Each source always carries an exact, non-empty `explicit_models` list; there is no wildcard or
  request-time inventory expansion mode. `serviceSlugSnapshot` must be a canonical NyxID slug and
  must be unique within the policy. It is the stable public model namespace, not an authoritative
  service identity or an access grant.

`custom_replace` is also the user override boundary. It replaces the platform default as a whole;
there is no source-by-source merge. This includes an explicitly empty override. To return a scope
to the administrator-managed default, the client sends
`DELETE /api/scopes/{scopeId}/llm-model-catalog` with the current `expectedStateVersion` and a
`mutationId`. The committed scope mode then becomes `inherit_platform`; reset does not copy the
current platform sources into scope-owned state.

This policy controls discovery, not permission. Saving a source does not create a NyxID service,
bind credentials, make an organization credential usable, or grant proxy access. Runtime model
listing and qualified invocation resolve from the persisted policy and never call the human-only
`GET /api/v1/keys` or `GET /api/v1/services` endpoints. NyxID remains the credential and permission
authority when the resolved target is invoked at the proxy boundary. The Admin candidate APIs are
the only consumers of those human inventory endpoints: scope candidates come from `/api/v1/keys`,
platform candidates from `/api/v1/services`, and they are configuration aids rather than runtime
facts.

Model inventory can be fetched on demand while editing a source. These configuration-only APIs are:

- `GET /api/scopes/{scopeId}/llm-model-catalog/candidates/{userServiceId}/models`;
- `GET /api/admin/llm-model-catalog/candidates/{catalogServiceId}/models`.

The scope endpoint re-reads the caller's authoritative NyxID inventory, requires an ordinal exact
`userServiceId` match that is currently callable, derives the canonical slug from that match, and
then requests the upstream service's `/models` through
`/api/v1/proxy/s/{serviceSlug}/models?_nyxid_via={userServiceId}`. The platform endpoint re-reads
the authoritative catalog inventory, requires an ordinal exact `catalogServiceId` match that is
currently selectable, and requests `/api/v1/proxy/{catalogServiceId}/models`. A caller cannot
supply a trusted slug, URL, or proxy destination to either endpoint.

A successful discovery response contains `sourceIdentity`, `serviceSlug`, sorted unique
`modelIds`, and an optional `defaultModelId`. It is an editing suggestion, not a policy mutation or
a runtime fact. The operator selects models and persists them as `explicit_models` through the
corresponding policy `PUT`; an upstream discovery failure fails the request, while a valid empty
upstream list remains an empty suggestion. Runtime reads do not repeat this fetch.

Aevatar must never classify an LLM source or repair a missing identity from URL text, display name,
service name, or the presence of `llm` in any of those strings. A canonical slug is accepted only as
an explicit policy field tied to an exact service identity. This is why services such as
`chrono-llm` and `chrono-llm-public` are selectable through explicit Admin configuration instead of
a naming convention.

`GET /v1/models` requires a bearer at the Host boundary only so the caller-scope resolver can
determine the caller's `scopeId`. The discovery application service receives only that `scopeId`;
it receives no bearer credential and performs no HTTP calls. It reads only the effective
`LLMModelCatalogPolicyCurrentStateDocument`:

- A scope `custom_replace`, including one with an explicitly empty source list, is authoritative and
  never falls back.
- An absent scope policy or `inherit_platform` selects the platform projection. If that required
  projection is missing or unavailable, the catalog is unavailable.

Each configured upstream model ID produces one deterministic OpenAI-compatible entry. Entries are
sorted by `id` using ordinal comparison, and every entry has:

- `id = <serviceSlugSnapshot>/<upstreamModelId>`;
- `object = model` and `created = 0`;
- `owned_by` and `group` equal to `serviceSlugSnapshot`;
- no optional rich metadata (`context_length`, `max_output_tokens`, `display_name`, or
  `description`).

Model listing never calls an upstream `/models` endpoint, invents request-time timestamps, classifies
upstream authentication or availability failures, or returns a partial per-source result.

`GET /v1/models` distinguishes an empty catalog from an unavailable catalog:

- Missing or invalid caller authentication returns `401` (`authentication_required` or
  `authentication_failed`) before discovery reads the policy.
- An explicitly empty effective policy returns `200 OK` with `data: []`.
- A missing or unavailable required effective-policy projection returns
  `503 model_catalog_unavailable`.

Qualified invocation uses the same effective policy as listing and requires an ordinal exact match
for both `serviceSlugSnapshot` and `upstreamModelId`. A match produces a typed target: a platform
source supplies its exact `catalogServiceId`, while a scope source supplies its exact
`userServiceId` together with the canonical slug snapshot. The slug remains a public namespace and
never becomes authoritative identity. Every qualified model ID returned by `/v1/models` is resolved
through this same policy path by both `POST /v1/chat/completions` and `POST /v1/responses`; subject
to NyxID proxy authorization and upstream availability, the same ID is therefore invocable through
either API. An unknown qualified slug or model returns
`404 model_not_found`; a routing-projection failure returns `503 model_route_unavailable`. Bare
model routing remains a legacy compatibility path. Runtime routing does not read external inventory
or keep an in-process per-bearer catalog/slug cache.

Qualified routes use NyxID's REST proxy plane, not the legacy LLM gateway authorization path. The
bearer used for a qualified invocation must therefore carry `proxy` or `proxy:*`; `llm:proxy` alone
is insufficient. A bearer limited to `llm:proxy` may continue to call the legacy gateway with a
bare model, but it cannot invoke a configured qualified catalog or UserService target.

## Published topology contract boundary

The external source of truth is a published NyxID contract, not the shape or iteration order of a runtime JSON response. The read-only audit target for this integration is `/Users/chronoai/Code/NyxID`; Aevatar work must not patch that repository as part of Milestone 33.

At NyxID revision `c885cbfa`, `GET /api/v1/user-services` and its response schemas are included in the published OpenAPI document. Runtime handlers and prose exist for `GET /api/v1/nodes` and `GET /api/v1/nodes/{node_id}/bindings`, but those routes and their response schemas are not included in that published OpenAPI document. Route existence is not a contract locator.

Before node-backed catalog evidence can be authoritative, a published locator must guarantee all of the following:

- the exact owner of every user service, node, and binding, including personal versus organization ownership;
- caller access and visibility semantics, including inherited and cross-owner resources;
- the exact service-to-primary-node and service-to-binding topology;
- route order and tie behavior, including whether priority alone is total ordering;
- edge multiplicity, including whether repeated bindings are distinct authorization facts;
- a revision or watermark that proves the service, node, and binding reads belong to one coherent source snapshot.

Until that locator exists, any plan that depends on those unproven node/topology fields is blocked and must fail closed. Aevatar must not manufacture authority by sorting node or binding identifiers, selecting a minimum priority, collapsing repeated edges, or treating two equal local reads as a published ordering or snapshot guarantee. Organization ownership and cross-owner topology remain unsupported for the same reason. The adapter's double-read/content-digest check can detect local instability, but it cannot replace the missing NyxID contract.

## 原理

```
用户通过 NyxID 登录 Aevatar（Bearer Token）
        |
Agent 需要调用 LLM
        |
Aevatar 用用户的 Bearer Token 直接请求 NyxID LLM Gateway
        |
NyxID 验证 Token → 查找该用户的 API Key → 注入 → 转发给上游 Provider
        |
返回结果
```

用户的 API Key 始终加密存储在 NyxID 中，Aevatar 不接触明文密钥。旧调用方仍可让 Gateway 按裸 model 名做兼容路由（例如 `gpt-4o` → OpenAI，`claude-sonnet-4-5-20250929` → Anthropic），但新的 Responses 直连接入应优先使用 `/v1/models` 返回的 `<service-slug>/<model>`。这里的 slug 只是客户端可读的模型命名空间，不能代替 NyxID 的精确 service identity。

---

## Responses 直连路由

外部客户端通过 NyxID proxy 调 Aevatar `/v1/responses`、`/v1/messages` 或 `/v1/chat/completions` 时，调用者身份与下游委托凭据分开处理。`X-NyxID-Identity-Token` 若存在，Host 先用 NyxID JWKS 校验该 RS256 assertion；校验通过后用 `sub` 作为 caller scope，不再调用 `/me`。该 header 存在但校验失败时 fail closed。只有缺失 identity assertion 时，才使用 inbound bearer 调 NyxID `/me` fallback。`X-NyxID-Delegation-Token` 只作为 downstream delegated credential，永远不作为 caller identity 输入。

模型路由采用 OpenRouter 风格：

```text
<service-slug>/<model>
```

例如：

```text
chrono-llm/gpt-5.5
llm-anthropic/claude-haiku-4-5
```

`GET /v1/models` 要求 bearer，但 bearer 只交给 caller-scope resolver 解析 `scopeId`；model discovery 只接收 `scopeId`，不接收 bearer，也不执行 HTTP 请求。它只读取当前 scope 的有效 model catalog policy current-state projection：scope `custom_replace`（包括显式空来源）始终优先且不回退；scope 缺失或 `inherit_platform` 时读取平台 projection，所需的平台 projection 缺失或不可用则返回 `503 model_catalog_unavailable`。显式空有效策略返回 `200 OK` 与 `data: []`，缺失或非法 caller authentication 返回 `401`。

每个 `explicit_models` 中的 model ID 都确定性映射为一条 `<serviceSlugSnapshot>/<upstreamModelId>`，结果按完整 `id` 做 ordinal 排序；`created` 固定为 `0`，`owned_by` 与 `group` 固定为 slug，rich metadata 字段保持 null 并从 JSON 省略。该端点不调用上游 `/models`，不生成请求时刻时间戳，也不存在按来源失败分类、部分成功或成功并集语义。human-only 的 `/api/v1/keys`、`/api/v1/services` 与 `/api/v1/user-services` 不参与运行时 discovery；Admin candidate inventory 也不是运行时事实。

创建请求时，Aevatar 会把 `<service-slug>/<model>` 拆成展示命名空间与裸模型名，但不会把 slug 当作最终路由身份：

1. `service-slug` 与裸 `model` 必须同时 ordinal exact match 同一份有效 policy；只命中 slug 但 model 不在该来源的 `explicit_models` 中也视为未知模型。
2. 命中平台来源时生成携带 exact `catalogServiceId` 的 typed target；命中 scope 来源时生成携带 exact `userServiceId` 与 canonical slug snapshot 的 typed target。slug 只用于公开命名空间，绝不是 authoritative identity。
3. 裸 `model` 与 typed target 一起传给下游 LLM provider。

未知的 qualified slug/model 返回 `404 model_not_found`；routing projection 不可用返回 `503 model_route_unavailable`。

如果客户端传裸 model 名，Aevatar 仍会走默认 gateway fallback。这只是兼容路径，不是新文档推荐路径。

完整外部接入说明见 `docs/canon/nyxid-responses-direct.md`；终端配置步骤见 `docs/operations/2026-05-13-aevatar-responses-via-nyxid-setup.md`。

---

## Workflow caller credential 边界

Workflow 层只承载 provider-neutral 调用者凭据与路由偏好。`WorkflowCallerCredential.BearerToken` 保存的是已经规范化的 raw bearer token，不包含 HTTP `Authorization` scheme；`WorkflowLlmControl.RoutePreference` / workflow proto `route_preference` 表达的是 workflow 自身的路由偏好，不使用 NyxID 专有字段名。

当前 internal P0 的 Host/Infrastructure HTTP 边界在 `Authorization: Bearer` 与 NyxID proxy 注入的 `X-NyxID-Delegation-Token` 同时存在时，优先选择合法的 forwarded Authorization bearer；只有 Authorization 缺失时才回退 delegation token。原因是透明 managed Codex readiness 需要用当前用户 bearer 调用 NyxID `/users/me` 与 API-key 管理接口，而 `proxy:*` delegation token 只负责下游 proxy 能力。被选择的凭据会交给 workflow-owned `WorkflowCallerCredentialTokens.ParseOptional` 做一次规范化与 fail-closed 校验；已出现但格式非法的 Authorization 不得回退 delegation。`X-NyxID-Identity-Token` 只用于 Host 认证和从 `sub` 派生 caller scope，绝不能作为 workflow caller credential 下传。

进入 Workflow Application/Core 后，调用者凭据继续作为 typed workflow credential 在 command、actor state 与 LLM execution intent 中传递；不得在 workflow 中间层通过 headers、metadata 或 provider-specific 字段回填身份语义。internal P0 中代理 Aevatar 的 NyxID UserService 必须暂时保持 `forward_access_token=true`，可以同时保持 `inject_delegation_token=true`；这项较弱边界不能外推为 public rollout 合同。关闭 access-token forwarding 前，必须先引入按用途分离的 typed 双凭据合同，或由 NyxID 提供可完成 self-service readiness 的窄 delegation capability。

定时 workflow 调度不把 fire-time 换出的短期 NyxID bearer 写入 `connector_http_authorization`、`llm_control` 或 run 级 runtime secret。Scheduled Dispatch 在可信 fire 链路中把短期 token 存入 durable vault，向 `ChatRequestEvent.caller_durable_credential` 只传 typed `DurableCallerCredentialRef`；NyxID source 的原始 subject + capability scope 作为独立 typed caller authority 随 handle 传入，禁止从 token、vault `subject_id` 或 Aevatar `scopeId` 解析。`WorkflowRunGAgent` 把 handle 与 authority 保存到 `WorkflowCallerCredentialState`，但 committed projection 会移除二者。LLM、tool 与 connector 外呼继续走统一 `TryGetCallerCredentialAsync` 漏斗，每次外呼前用 handle 现场解析 raw bearer。只有不属于 `scheduled_invocation_agent_key` 完整性契约的历史 workflow run 才可按其原版本留在旧 runtime-secret 路径；新建、reauthorize 或再次 fire 的 Agent Key automation 不允许 missing handle、missing binding 或 legacy bearer fallback。

外部 API 不接受 `caller_durable_credential`；该字段只能由 Scheduled Dispatch 内部生成。Projection、readmodel、日志与诊断只允许展示 caller credential 的 source kind，不回显 durable ref、vault ref、fingerprint 或 raw bearer。

NyxID 专有映射只发生在 `Workflow.Integration.AI` 边界：workflow raw token 分别映射到 LLM provider auth 与 tool execution credentials，workflow `RoutePreference` 在这里映射为 provider-specific `NyxIdRoutePreference`。NyxID provider 本身继续读取 typed provider auth，不从 tool context 或 workflow headers 兜底推断身份。

### Scheduled Agent Key LLM 完整性链

依赖 owner LLM 的 Team member automation 只有一条权威链路：

```text
committed typed UserConfig selection
  -> digest-covered ScheduledInvocationOwnerLLMSelection
  -> constrained NyxID Agent Key + Vault reference
  -> actor-owned authorization fact + persisted ChatRequestEvent.LlmControl
  -> runtime caller/payload/fact cross-check
  -> workflow inbox
```

UserConfig read model 中的 typed selection 是 planning-time authority。`NyxIdUserService` 必须同时包含 canonical route、精确 `UserService.id`、service slug snapshot 与 model；显式 Gateway selection 也必须是 typed `Gateway`，不能由空字段或 Host default 推断。缺失或 malformed selection 保持 `Unspecified`，对 owner-LLM-dependent schedule 直接 fail closed。

计划把 selection 写入 Protobuf permission digest，并复制到 schedule actor 的 authorization fact。Studio adapter 只能从已验证的 plan/fact 生成持久化 `ChatRequestEvent.LlmControl`；schedule fire 时禁止再次查询 UserConfig、从 Host default 补 route/model、从 slug 或 model prefix 反推 service identity，或把 v1 digest 当作 v2 compatibility 输入。运行时必须在 workflow inbox 之前校验 verified caller binding、fact selection、payload route/model 与 exact service grant 全部一致；任何缺失或漂移都进入 typed `needs_authorization` 失败路径。

Caller authority 与 `VerifiedBindingId` 是 write/runtime-side authority，不属于 projection 或 public API。成功 create 仅通过 Host category `Aevatar.Studio.MemberAutomation` 的 Information event `6201/StudioMemberAutomationCreateAccepted` 提供非投影 operational correlation；其 structured state 除日志框架的 `{OriginalFormat}` 外只有 `ScopeId`、`TeamId`、`MemberId`、`ScheduleId`、`OperationId` 与 `BindingId`。accepted committed revocation outcome 在两个 pending flag 都为 false 后，通过同一 category 的 `6202/StudioMemberAutomationRevocationCompleted` 记录精确的 `ScopeId`、`TeamId`、`MemberId`、`ScheduleId`、`OperationId`、两个值为 `Completed` 的 revocation status、`StateVersion` 与 `ObservedAtUtc`。仓库工具 `tools/schedules/query_member_automation_audit.sh` 是这两个事件的 canonical allowlisted query。两个事件都不得包含 permission digest、bearer、Agent Key/API-key identifier、Vault reference/ciphertext 或 refresh token。

---

## Channel Route 选择

Lark bot 等 channel surface 通过 `/model`、`/models`、`/llm`、`/route` 暴露同一组 LLM route 命令：

- `/route`：列出当前 NyxID 绑定用户可作为 LLM provider 的 ready service
- `/route use <编号|service-name> [model-name]`：保存 service route，可同时指定 model
- `/model use <model-name>`：只覆盖当前 route 下的 model
- `/model preset <preset-id>`：按 NyxID 返回的 setup preset 使用或创建 service
- `/model reset`：清空用户偏好，回退到 bot 默认配置

这些命令不读取 Aevatar 内部密钥，也不使用独立的 `llm:status` scope。Aevatar 通过 per-user NyxID binding 做 broker token-exchange，请求 `proxy` scope 的短期 token，然后调用 NyxID LLM service catalog / route API。`Aevatar:BackendConsole:OidcClientId` 配置的 OAuth client 与 `/oauth/authorize` 必须使用同一 canonical interactive authorization scope：

```text
openid profile email offline_access urn:nyxid:scope:broker_binding proxy
```

`llm:proxy` 是通常的短期 LLM capability token-exchange scope，不是 interactive OAuth scope；DCR、Console login 与 channel `/init` 都不得把它发送到 `/oauth/authorize`。managed Codex 的内部 canary 是明确例外：它调用 NyxID REST proxy 的固定 `chrono-llm-public` 路由，而 NyxID 当前没有 service-scoped delegation，因此暂时要求五分钟 `proxy:*` token。`proxy:*` 同样不得进入 interactive OAuth consent，且在 NyxID 提供窄 capability 前不得用于全用户 rollout。如果旧 binding 缺少 canonical authorization scope，用户可重新发送 `/init` 或重新完成 Studio 登录 consent 来刷新 binding；Aevatar 不会降级到 bot-owner credential、复用入站 bearer 或缓存 token。

---

## Console Settings 契约

Console 的 Settings、Chat composer、Studio workflow dry-run 必须共用后端 LLM Settings 视图，不再各自读取 NyxID catalog 后在前端推断 route、provider label 或 fallback。

Canonical endpoints：

- `GET /api/user-config/llm`：返回当前用户的 LLM settings view。
- `PUT /api/user-config/llm`：投递保存 `routeValue` 与 `model` 的命令，返回 `202 Accepted` receipt（`accepted`、`commandId`、`ackStage = "accepted"`、`actorId`、`correlationId`、`ackedAtUtc`）；该响应只承诺命令已进入 dispatch/inbox 边界，不承诺 actor 已 handled、event 已 committed 或 read model 已 observed。前端保存成功后必须重新 `GET /api/user-config/llm` 读取 canonical settings view。
- `GET /api/user-config/runtime`：返回 runtime mode、active runtime URL、local/remote URL 以及后端默认值。
- `GET /api/auth/me`：返回最小 typed `profile` / `session`，不得回显 access token、refresh token、raw JWT 或 raw claims。

`GET /api/user-config/llm` 是 Settings 闭环的唯一 route truth。响应必须至少表达：

- `savedRoute` / `savedRouteLabel`：用户保存的 route 及展示名。
- `savedRouteKind`：`unspecified`、`gateway` 或 `nyx_id_user_service` 的 typed selection kind。
- `savedUserServiceId` / `savedServiceSlug`：精确 UserService identity 与 slug snapshot；仅 `nyx_id_user_service` 有值。
- `effectiveRoute` / `effectiveRouteLabel`：本次实际可用的 route 及展示名；当 saved route 不可用时由后端选择 fallback。
- `routeFallbackActive` / `fallbackReason`：诚实暴露 saved route 与 effective route 是否分离。
- `routeOptions`：可选 route 列表，包含 `routeValue`、`label`、`source`、`status`、`allowed`、`ready`、`serviceId`、`serviceSlug`。
- `modelGroupsByRoute`：按 route 分组的模型集合；前端不得用 provider slug 或 model 前缀重新拼装。
- `catalogStatus` 与 `capabilities`：用于驱动禁用态、保存态与 retry 行为。
- `defaultModel`：当前保存的默认模型。

NyxID catalog 不可用时，后端返回 degraded view，而不是空列表：保留 `savedRoute`、`effectiveRoute`、`defaultModel`，设置 `catalogStatus = "unavailable"`，并通过 `capabilities` 禁止编辑和保存、允许 retry。前端只展示这个 degraded view，不做 query-time fallback 或本地补跑 catalog。

`effectiveRoute` 只描述交互式 Settings/运行时可用性，不会升级成 scheduled authorization authority。Scheduled planner 只读取 committed `savedRouteKind` 对应的 typed selection；`savedRouteKind == "unspecified"` 时，即使 GET 同时展示一个 Host-derived `effectiveRoute`，也不得据此授权或填充定时调用。

Gateway 的强类型 canonical route 是 `/api/v1/llm/gateway/v1`。Console Settings 中的空字符串 `""` 只是在保存与选择 surface 上表示显式 Gateway option 的稳定 selector；后端在形成 scheduled authorization selection 时必须把 typed `Gateway` 映射到 canonical route，不能把空 selector 带入 permission digest 或 runtime payload。Gateway 的展示名由后端 settings view 返回；前端只消费 `savedRouteLabel`、`effectiveRouteLabel`、`routeOptions[].label`，不得把 `NyxID Gateway` 当作 route display source 硬编码。

旧 Console surface 不再保留兼容入口：`/api/user-config/models`、`/api/user-config/llm/options`、`/api/user-config/llm/preference` 已被 canonical `/api/user-config/llm` 取代。

---

## NyxID 端配置（管理员）

### 1. 创建 LLM Provider

在 NyxID 管理后台 **Providers** > **Manage** 页面创建 Provider（以 OpenAI 为例）：

| 字段 | 值 |
|------|-----|
| Name | OpenAI |
| Slug | openai |
| Provider Type | api_key |
| API Key Instructions | 前往 https://platform.openai.com/api-keys 创建 |
| Is Active | true |

同理创建 Anthropic、DeepSeek 等。

---

## Aevatar 端配置（管理员）

在 `appsettings.json` 中分别配置 NyxID 的公开 OIDC、公开 API 和可选的集群内 REST 地址。系统会自动注册 NyxID LLM Provider：

```json
{
  "Aevatar": {
    "NyxId": {
      "Authority": "https://your-nyxid-domain",
      "ApiBaseUrl": "https://your-nyxid-domain",
      "EnableInternalApiTransport": true,
      "InternalApiBaseUrl": "http://nyxid-api.namespace.svc.cluster.local:3001",
      "InternalApiFallbackTimeoutSeconds": 5
    }
  }
}
```

`Authority` 只承担公开 OIDC issuer、discovery 和 JWKS；`ApiBaseUrl` 承担控制面 REST、LLM gateway、浏览器地址、webhook/resource URI 以及 Assistant action registry；`InternalApiBaseUrl` 只承担 `/api/v1/proxy/s/*` 与 `/api/v1/ssh/*` 的集群内执行传输。Mainnet 默认忽略历史配置中的 internal URL；只有 `EnableInternalApiTransport=true` 且 internal URL 是不含 userinfo、query 或 fragment 的绝对 HTTP(S) 地址时才启用，否则继续使用公网，显式启用但 URL 无效则启动失败。Gateway Endpoint 从 `ApiBaseUrl` 推导为 `{ApiBaseUrl}/api/v1/llm/gateway/v1`，不使用集群内地址。Chat 在签发用户能力时读取 `/api/v1/user-services`；该 catalog 以及 `/keys`、`/api-keys/scope-plan` 等控制面请求始终使用 `ApiBaseUrl`，不会先探测内网 transport。

当同时配置 `InternalApiBaseUrl` 与 `ApiBaseUrl` 时，只有 proxy/SSH 执行请求首选内网地址。DNS、拒绝连接或 host/network unreachable 明确表明尚未连接到内网目标时，客户端使用相同 method、path/query、authorization、headers 和 body 向公开 API 重试一次。此外，仅安全的 `GET/HEAD/OPTIONS` 在 `InternalApiFallbackTimeoutSeconds` 响应头预算内没有收到内网响应头时向公开 API 重试一次；默认预算为 5 秒，非正值恢复默认值，超大值限制为 300 秒，并与公网重试共享原 HttpClient 的 330 秒总超时预算。Mutation 超时不会重放；TLS、连接重置、Host/调用方取消、重定向、任意 HTTP 响应，以及已收到响应头后的 body 读取失败也不会重放，multipart 请求不参与降级。Assistant action registry 若在启动期因网络、HTTP、读取、JSON 或契约校验失败而不可用，只禁用本进程的 Assistant browser actions；Host 与普通 chat 仍继续启动。Host 自身的取消信号不会被该降级吞掉。

---

## 用户使用流程

1. **在 NyxID 上连接 Provider** — 登录 NyxID → Providers 页面 → 点击 Connect → 输入 API Key
2. **在 Aevatar 上使用** — 通过 NyxID 登录后，Agent 调用 LLM 时自动使用该用户的 API Key，无需额外操作

---

## 常见问题

**Q: 用户没配 API Key 会怎样？**
LLM 调用失败，NyxID 返回错误提示用户需要先连接 Provider。

**Q: 支持多个 Provider 吗？**
支持。NyxID catalog 提供多个可用 route，Aevatar 后端把它们物化成 `routeOptions` 与 `modelGroupsByRoute`，Settings 中选择的 route 和 model 决定后续调用。

**Q: 本地开发能直接用 API Key 吗？**
可以。在 CLI Settings > LLM 页面配置 OpenAI/DeepSeek 等 Provider 并填入 API Key，与 NyxID Gateway 共存。
