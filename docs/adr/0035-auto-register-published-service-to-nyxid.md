---
title: "已发布 workflow 服务自动注册到 NyxID"
status: accepted
owner: eanzhao
---

# ADR-0035: 已发布 workflow 服务自动注册到 NyxID

> 跟踪 epic：[#2299](https://github.com/aevatarAI/aevatar/issues/2299) · 关联：ADR-0018（per-user binding 边界）、#375（线上零 secret material）、#1983（`ExternalExposure` typed 记录——本 ADR 将其从本地指针升级为真回执）

## Context

aevatar 没有任何代码会主动去 NyxID 注册自己：`ServiceDefinition.ExternalExposure.nyxid_slug` 只是一个**本地指针**（`ServiceCommandApplicationService.UpdateServiceExternalExposureAsync` 仅 provision 定义 actor 并 dispatch 本地 `ServiceExternalExposureUpdatedEvent`，**零对外调用**，#1983），它记的 slug 在 NyxID 是否存在、是否有效 aevatar 不校验，可能悬空。「发布即被 NyxID 发现」今天是一次**手工** `nyxid service add --custom` + 一份**手写** OpenAPI。这把「发布即被发现」卡成需要人介入的一跳，并留下三个缺口：protobuf↔OpenAPI 形状不匹配（G1）、注册桥缺失 / 本地悬空（G2）、`scope_id` 鉴权对不齐（G3）。

约束：**只能改 aevatar**（NyxID 只读其既有契约，不得新增 / 修改），且必须遵守主链路架构（actor 即业务实体、committed 事件驱动、读写分离、序列化 Protobuf、host 配置注入 FI-002）。

核心可行性事实（已对 `~/Code/aevatar` 代码核实）：

- **触发缝已存在**：`ICommittedStatePublicationHook` 在领域事件提交后、对外发布前被调用；`ScriptingServiceRevisionRepublishHook` 是「committed 事件 → 读 readmodel → 经命令端口派发」的活先例。注册无需新机制。
- **注册原语已存在**：`NyxIdApiClient.CreateServiceAsync` → `POST /api/v1/keys`（NyxID 连接层 / `nyxid service add --custom`），并有 `GetServiceAsync` / `DeleteServiceAsync` → `/api/v1/keys/{id}`；目前仅经 `NyxIdServicesTool` 作 LLM 工具暴露，可被注册 port 复用。
- **鉴权可 aevatar-only 闭环**：NyxID 代理对 `auth_method=bearer` 把存储凭证 **verbatim** 注入下游。于是 aevatar 可以**自签**一把带 `scope_id` claim 的 JWT 存进 NyxID，代理注入回来即自满足 `AevatarScopeAccessGuard`——NyxID 不必拥有 `scope_id` 概念。
- **身份边界已被路由限定**：NyxID 连接层注册拒绝 service-account 与 delegated token → 注册只能用 scope owner 的人类 token。

## Decision

在已发布服务的生命周期里引入一条 **committed-事件驱动、actor 拥有的对账式自动注册**，把 `ExternalExposure` 从本地指针升级为**真注册回执**：

1. **触发**：`ServiceDeploymentActivatedEvent` 提交后，`ServiceExposureReconcileHook`（实现 `ICommittedStatePublicationHook`）按 opt-in 门控算出 `desired_spec_hash`，向 `ServiceDefinitionGAgent` 派发 `ReconcileExternalExposureCommand`；停用事件派 `RetireExternalExposureCommand`。
2. **状态机**：`ServiceDefinitionGAgent` 内 `Pending → Registering → Registered/Failed`，经新增的 `INyxIdServiceRegistrationPort` 调 NyxID **既有** 连接层 API（复用 `NyxIdApiClient` 的 `/api/v1/keys` create/get/delete，补一个 `PUT /api/v1/keys/{id}` 做漂移就地更新）；成功事件携带 NyxID **返回的** canonical slug + id 写回回执。
3. **G1 OpenAPI 自产**：新增匿名只读端点，把 `ServiceDefinitionSpec` 投影成 OpenAPI 3.1（带 `x-aevatar-tool`），作为 `openapi_spec_url` 传给 NyxID（复用既有 protobuf→JSON-schema 转换，不新写 schema 代码）。
4. **G3 scope 凭证**：存进 NyxID 的 `credential` 是 aevatar **自签**的 scope-JWT（带 `scope_id` claim），注册体置 `forward_access_token=false`；invoke 端点接受 NyxID 与 aevatar-self **双 issuer**。
5. **身份**：注册 / 轮转用 scope owner 的人类 NyxID token（瞬时、不入 grain state）；存储凭证是自签 scope-JWT，两套凭证两条生命周期。

无 opt-in 的服务行为字节级不变（纯 opt-in 扩展）。注册是**解耦的后续动作**：即使注册失败，服务本身仍可调（只是未上架）。

### 注册层选择（待 Phase 1 评审定稿）

NyxID 有两层下游模型：**连接层 `POST /api/v1/keys`**（自定义服务，自助，现有客户端已支持）与**目录层 `POST /api/v1/services`**（平台 `DownstreamService` 模板，human-only）。aevatar workflow 服务不在 NyxID 目录里，**本 ADR 默认走连接层 `/api/v1/keys`**（与 `nyxid service add --custom` 一致、复用现有 `CreateServiceAsync`）。早期方案稿曾写 `/services`；两者 ownership / visibility 语义不同，最终层选择在 Phase 1 设计评审定稿。G3 凭证注入机制与层选择无关。

## Locked Rules

1. **单一拥有者**：`ServiceDefinitionGAgent` 是回执唯一权威；不引入注册协调 actor。
2. **committed 触发，非请求线程**：注册只由 committed 激活 / 停用事件经写侧钩子驱动。
3. **slug 只来自 NyxID 返回**：`nyxid_slug` 仅由 `ServiceRegistrationSucceededEvent` 写入；aevatar 绝不预占或猜测 ⇒ 悬空指针不可能。
4. **事件化退避**：重试 / 超时经自消息进 inbox；回调只发信号，无 `Task.Run` 改状态、无 lock。
5. **显式对账**：重试命令带 `expected_attempt + desired_spec_hash`，与 `State` 不符即拒。
6. **幂等对账，不重复建**：`desired_spec_hash == registered_spec_hash` ⇒ no-op；漂移 ⇒ `PUT` 就地更新；半注册 409 ⇒ `GET` 解析既有 id。
7. **凭证不入持久态**：owner 人类 token 仅瞬时（AsyncLocal）；proto / grain state 不落任何 secret；签名密钥仅以 host 配置 / KMS 引用存在。
8. **读侧诚实**：注册 `status` + 失败详情只经 projection readmodel 暴露、携权威源版本；不同步查 actor、不 query-time replay。
9. **不静默吞失败**：永久失败落 `FAILED + last_error` 且 readmodel 可见；不 log-and-drop。
10. **重试耗尽语义**：host 只注入 `max_attempts / base_delay / max_delay` 事实；`ServiceDefinitionGAgent` 自己判定耗尽，落 `FAILED + last_error=retry_exhausted:* + attempt=max + next_attempt_at=null`，并停止 durable self retry。后续显式 reconcile 可从 attempt 1 重新开始。
11. **bind opt-in 语义**：bind request 的 typed `ExposureDesired` 是 tri-state intent： omitted/null 保持当前 intent，`true` 写 canonical opt-in intent，并通过与 activation committed hook 共用的 external exposure intent service 统一计算 OpenAPI URL / spec hash / credential 后派发 reconcile command；显式 `false` 复用 retire command，由 `ServiceDefinitionGAgent` 提交关闭外部暴露的 actor-owned receipt。
12. **读侧版本语义**：`externalExposure.sourceStateVersion` 必须来自 service catalog current-state readmodel 根 `StateVersion`，不得使用投影本地计数或 query-time replay。

## Required Contract（`ExternalExposure` 演进，additive、wire-safe）

```proto
enum ServiceRegistrationStatus {
  SERVICE_REGISTRATION_STATUS_UNSPECIFIED = 0;
  SERVICE_REGISTRATION_STATUS_PENDING     = 1;
  SERVICE_REGISTRATION_STATUS_REGISTERING = 2;
  SERVICE_REGISTRATION_STATUS_REGISTERED  = 3;
  SERVICE_REGISTRATION_STATUS_FAILED      = 4;
  SERVICE_REGISTRATION_STATUS_RETIRED     = 5;
}

message ExternalExposure {
  string nyxid_slug = 1;                        // 字段 1/2 保留，wire-safe
  google.protobuf.Timestamp registered_at = 2;
  ServiceRegistrationStatus status = 3;
  string nyxid_service_id = 4;                  // update/delete 的 key
  string desired_spec_hash = 5;                 // 想注册的（漂移探测）
  string registered_spec_hash = 6;             // 已注册的（== desired ⇒ no-op）
  string last_error = 7;                         // 脱敏，无 secret
  int32  attempt = 8;
  google.protobuf.Timestamp next_attempt_at = 9;
  string credential_kid = 10;                    // 存进 NyxID 的 scope-JWT 的 kid（轮转）
  bool   exposure_desired = 11;                  // 每服务 opt-in 结果
}
```

新增命令：`ReconcileExternalExposureCommand` / `RetireExternalExposureCommand` / `RunRegistrationAttemptCommand`（自消息）/ `RegistrationRetryDueCommand`（退避后自消息）。
新增 committed 事件：`ServiceRegistration{Requested,AttemptStarted,Succeeded,Failed,Retired}Event`。

## Consequences

- 「发布即被发现」成为现实且**自动**：无手工 `nyxid service add`、无手写 OpenAPI；`external-exposure` 从悬空指针变成可验证回执。
- 信任边界正确：调用方只持 NyxID 凭证；aevatar 自签 scope-JWT 把「谁有权调这个 scope」收敛回 aevatar 自己的门。
- 新增成本诚实暴露：双 issuer / JWKS 是一个真子项目（Phase 2）；OpenAPI URL 须公网可达；per-user 身份穿透仍不可 aevatar-only 解（见 Non-Goals）。
- 无 opt-in 的已发布服务零影响。

## Cutover Order

分阶段交付，每步由 build + 定向测试 + 对应 `tools/ci/*guard*.sh` 门禁，详见 epic [#2299](https://github.com/aevatarAI/aevatar/issues/2299)：

1. 接受本 ADR（proposed → accepted）。
2. **Phase 0 契约先行**：`ExternalExposure` 升级 + 新命令 / 事件 + readmodel（纯结构）；build + proto 重生 + reducer/replay 测试。
3. **Phase 1 自动发现**：OpenAPI 匿名端点 + `ServiceExposureReconcileHook` + `INyxIdServiceRegistrationPort` / 适配器 + `NyxIdApiClient` 的 `/keys` PUT；用 owner token 跑通上架 / 发现 / 回执 / 下架。
4. **Phase 2 凭证闭环**：新认证项目（scope-JWT 铸币 + JWKS + 双 issuer + `credential_kid` 轮转）。
5. **Phase 3 硬化 + 文档**：退避耗尽、`status` 可观测、opt-in 接进 bind 面；canon 更新回执模型；给相关 canon 加 supersede 导读。当前 accepted 实现见 [external-exposure-receipt.md](../canon/external-exposure-receipt.md)。

## Non-Goals

- 改 NyxID（任何形态）。
- **per-user 身份穿透**：代理调用在 scope 权威下跑，不是原始用户 NyxID subject；按人归属需 NyxID delegation token，**出界**，与 ADR-0018 边界一致。
- 无人值守注册 / 轮转（NyxID 连接层注册拒 service-account + delegated token，须 owner 人类 token）。
- exactly-once：注册是 at-least-once + 幂等对账。

## Outcome

接受并实现后，一个 Studio 用户发布 workflow 即在 NyxID 自动出现、带可拉取的 OpenAPI、`ExternalExposure` 是 NyxID 回执而非悬空指针，且（Phase 2 后）经 NyxID 代理的调用自满足 `AevatarScopeAccessGuard`——在 aevatar-only 边界内补齐手工那一跳，并把无法 aevatar-only 解的 per-user 穿透诚实留给 ADR-0018 一族。
