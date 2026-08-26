---
title: "Agent Turn Tool Catalog"
status: active
owner: architecture
last_updated: 2026-08-24
---

# Agent Turn Tool Catalog

本文定义所有用户面 LLM generation 的工具目录不变量。系统注册目录和 route ceiling 可以很大；一次模型 turn 最终看到的 Aevatar-owned 工具必须是 request-local、强类型、小规模、可解释的 exact catalog，并与执行准入使用同一份 proof。

```text
intent_scope = intent_candidates UNION anticipated_continuations

effective_tools =
    route_ceiling
  INTERSECT profile_or_definition_ceiling
  INTERSECT intent_scope
  INTERSECT caller_authority
  INTERSECT runtime_availability
```

求交集之后不存在无条件 union。0-tool 是有效的 restricted-empty catalog；`null` 不得表示 unrestricted。一次 `ChatStreamAsync(maxRounds)` 只物化一次 final catalog，后续 tool round 不改变 schema、exact object fence 或 digest。

## 权威主干

`ToolSetRegistry` 只拥有静态 include topology。同一个 tool set 经多条 include 路径可达时只 materialize 一次；重复 materialize 会给 discovery 两个产出相同工具名的 source 实例，直接触发 name collision fail closed。`AgentToolDiscoveryService` 是 resolve 后唯一的 request-scoped 动态发现主干：它在 typed `AgentToolExecutionContext` 下发现 exact objects，不缓存 caller、connection、session 或 authority 事实，并对大小写不敏感的同名不同 object fail closed。

Tool source 每次 discovery 都新建 tool object，因此"同名"跨 discovery pass 不等于"同一个对象"。已经进入 turn catalog 的名字保留 catalog 自己的 exact object——它是在本轮真实 execution context 下发现、且 proof 覆盖的那个；runtime 侧同名对象按重复丢弃，不当作冲突。只有 catalog 尚未绑定 exact object 的 allowed name 才由 runtime 补绑。

所有入口按同一顺序处理：

1. 解析 route ceiling 和 immutable profile/definition snapshot。
2. request-local 发现 exact tools；duplicate、invalid schema 或 source failure 返回 typed failure。
3. 与 intent、caller authority、runtime availability 求交集。
4. 以工具数量优化目标、schema 大小和 connected-operation 风险约束构造 `AgentTurnToolCatalog` 和 `AgentTurnToolCatalogProof`。
5. 把同一 proof 同时用于 model declarations、`AgentToolVisibilityScope`、exact object fence、持久化和 telemetry。
6. 跨 actor/off-grain 边界只携带 typed proof；执行端重新发现 exact objects 后逐项验证。任何 digest/schema/origin/selector mismatch 都在模型或副作用前拒绝，不能回退 DI 全量工具。

Proof 的 catalog digest 覆盖 lowercase canonical name、exact description、递归 canonical JSON schema、origin 和 exact operation selector digest。JSON object property 使用 Ordinal 排序，array 顺序保留；secret、arguments、token、caller identity、时间戳和随机数不进入 digest。

## Tool-set topology

| Tool set | 语义 | 默认面 |
|---|---|---|
| `chat.core` | `ask_user` | public text |
| `web.runtime` | `web_search`、`web_fetch` | public text |
| `skill.runtime` | `ornn_search_skills`、`use_skill` | public text |
| `skill.authoring` | Ornn publish/update/validation | opt-in authoring |
| `aevatar.invoke` | typed service/GAgent/team/member/workflow invocation | public text ceiling |
| `aevatar.observe` | run observe 与 artifact read | public text ceiling |
| `responses.state` | Responses-owned state，例如 `TodoWrite` | Responses opt-in |
| `nyxid.assistant.admission` | 窄 readiness/admission | NyxID Assistant intent |
| `nyxid.connected_services` | request-local exact operations | typed selector only |
| `nyxid.privileged` | proxy/admin/key/approval/node | privileged opt-in |
| `nyxid.execution` | SSH/code/Codex execution | execution opt-in |
| `storage.read` / `storage.write` | storage read 与 mutation | coding/sandbox opt-in |
| `channel.core` | 与渠道无关的 reply、registration、delivery-target | 由每个 channel set include |
| `channel.lark` / `channel.telegram` | channel-specific actions | channel route only |
| `studio.local` | Studio local provisioning | Studio workflow only |

`workspace.default` 只组合 `chat.core + web.runtime + skill.runtime + aevatar.invoke + aevatar.observe`。它不包含 skill authoring、NyxID privileged/execution、storage write、完整 channel、Studio、Responses state 或 connected-service 全集。`lark.self_notify` 显式组合自己的 channel set；Voice 不继承整个 workspace ceiling。

Responses ingress 在边界兼容客户端的 `WebFetch` / `WebSearch` aliases，但内部 route 只保留 `web_fetch` / `web_search` canonical schema。Caller-declared forwarded tools 不冒充 Aevatar-owned tools，Aevatar 不执行它们；owned 与 forwarded count/bytes 分别记录。

## Final-catalog optimization targets and safety budgets

| Turn class | Aevatar-owned count optimization target | Canonical schema hard limit | 额外约束 |
|---|---:|---:|---|
| ordinary text / channel / `nyxid.chat` | 8 | 48 KiB | 无 |
| connected exact operations | 计入 ordinary 8 目标 | 计入 48 KiB | read ≤ 3，write ≤ 1 |
| voice realtime | 6 | 32 KiB | persisted snapshot/proof 必须同预算 |
| workflow LLM | 16 | 128 KiB | definition scope 必须显式 |
| admin / skill authoring | 16 | 128 KiB | opt-in profile |
| coding / sandbox | 6 | 64 KiB | opt-in profile |

工具数量只用于路由/选择优化与 telemetry：完成 typed authority 求交后的 exact catalog 即使超过数量目标也必须完整进入模型，既不能报错，也不能静默截断。`MaximumToolCount` / `max_owned_tool_count` 保留在 typed proof/profile 中表示 reviewed optimization target，不是执行正确性或授权边界。Canonical schema 大小与 connected read/write 数量仍是独立的硬安全约束，超限必须 typed fail closed。

## Boundary-specific rules

### Responses, Messages, Chat Completions

三入口在 ownership classification 之前共用 `ResponsesOwnedToolCatalogPlanner`，固定 profile snapshot、exact catalog 和 proof。Caller-forwarded declarations 单独分类、单独计量，不能进入 Aevatar executor。Off-grain `LlmRunCore` 重新物化后必须验证 persisted proof；显式 profile/tool-set 解析失败不能降级为 always-on providers。persisted proof 自带 budget，因此执行端从 sealed profile 重新推导权威 budget 并比对，不采信 payload 对自身的声明。

### Role、Channel 与 NyxID Assistant

Direct Role、Lark/Telegram relay 和 NyxID Chat 都必须生成 typed proof。Profiled NyxID Chat 轮次的普通 member no-match、classifier 失败，以及 selected member 的 task/recovery policy 解析后无任何可用工具（`SelectedPolicyEmpty` diagnostic）时，不再失败关闭为零工具：降级 ceiling = recovery policy ∪（reviewed ordinary baseline ∩ profile 可用面（route ∩ visibility ∩ maximum policy）），sealed ceiling 不会被扩宽；非 chat 面（generic `PrepareAsync`）保持 restricted empty；需要澄清时仍只暴露 `ask_user`。无 Agent Profile 的普通 NyxID Chat 轮次（intent Unspecified）物化 reviewed unprofiled baseline：从专用 `nyxid.chat.baseline`（仅含提供 pinned 审阅名单的轻依赖 source）选取 Class-R 管理读、`nyxid_require_service` readiness gate、`ask_user` 与显式 skill discovery/loading；capability 不合格是该 caller 的可用性交集，单个工具带 diagnostic 降级，不判整次 discovery 失败；request-local connected operations 不进入该基线，仍由 readiness gate 的 verified authorization continuation 承载。基线物化失败或工具集不可用时按 restricted empty fail closed，并附 typed diagnostics。Connected-service selector 只能从 caller-authorized typed presentation index 选择 exact endpoint：read 最多 3 个、write 最多 1 个；missing、ambiguous、timeout 或多 write 只会缩权或要求澄清。

Channel 的首个 profiled AgentRun 在模型调用前将 sealed profile snapshot、turn authority、catalog proof 和 policy version 持久化到 run actor。Run 的每个后续 LLM/tool/approval continuation 都从这些 typed facts 精确重物化并验 proof，不能重新读取当前 binding。终态 ready 或 CardKit completion 将 profile snapshot 回传给 Conversation actor；Conversation 以 `ConversationAgentProfilePinnedEvent` 固化首个快照，后续 run 从 actor state 注入同一快照。不同快照只能产生 `agent_profile_pin_mismatch` typed failure，不能热替换，也不能由进程内 conversation registry 兜底。

NyxID 继续拥有 caller/service/credential/resource authorization。缺连接时复用 typed readiness/connection card，不输出 CLI fallback。普通 route 不暴露 raw proxy、admin/key/node、SSH/Codex execution。Unattended workflow/channel/schedule 保留 typed Agent Key；短时 delegation token 只做请求准入。

Connected-service task policy 优先声明 exact `catalog_service_slug + endpoint_id + risk`；连接唯一时 exact selector 直接进入本轮 authority，不经过模型选择。只声明 `catalog_service_slug + risk` 且候选超过预算时，bounded operation selector 只能读取已经通过 route ceiling、Profile maximum、caller visibility 与 runtime availability 求交集后的 typed presentation index。该索引最多 64 项，只包含临时候选号、展示名、连接标签、HTTP method/path 和 typed risk；不包含 opaque tool name、endpoint identity、参数 schema、token 或 caller identity。模型必须从临时候选号中返回“最多 3 个 read”或“恰好 1 个 write”，不得混合；服务端随后映射回 exact published endpoint，并重新验证展示契约、连接身份、risk 和预算。

选择结果以 exact opaque tool names 写入 actor-owned turn authority，最终 proof 同时固定 operation selector digest；后续 materialization、tool round、approval/resume 只按该 authority 重物化，不重新运行 selector。混合风险目录中存在多个 write 候选时，selector 仍可为明确的只读请求选择 read；任何 write 结果在原始候选中存在多个 write 时仍按歧义拒绝。多个连接实例、超过 64 个候选、no-match、timeout、provider/tool-call 输出、未知或重复候选号、混合 risk、超量结果全部 fail closed。若 reviewed maximum 明确允许 `ask_user`，这些情况只暴露 `ask_user`；否则产生 restricted-empty，不能绕过 Profile ceiling 临时 union 澄清工具。

缺连接完成授权后，continuation 必须先用回调中已验证的 typed `userServiceId + serviceSlug` 收窄当前 route/caller/runtime 候选，再在原 turn 固定的 Profile maximum 与 task policy 内选择 exact operation；禁止先对整个 connector maximum 应用 operation budget，再事后按 UserService 过滤。已存在但被 maximum、caller visibility 或 runtime authority 排除的 operation 不能伪装成“缺连接”并再次触发 readiness。需要再次澄清时仍只允许 reviewed maximum 中的 `ask_user`。

### Workflow

新建、重新发布或重新绑定的 workflow 使用 `workflow-agent-turn-tool-catalog/v1`。每个 direct 或 parameterized `llm_call` 必须在 step/role 显式声明 `allowed_tools`；空数组表示静态工具维度 restricted empty，缺字段或 duplicate 在发布前拒绝。`tool_sets` 是 definition scope 中独立、显式的 request-time dynamic source 维度，只能物化被引用 set 的 exact tools，且不能扩大 caller authority；静态工具与动态工具合并后的 16-tool 值只作为优化目标，最终 exact catalog 超过该目标仍完整执行，128 KiB schema 上限继续硬约束。

历史已绑定 run 的 unversioned definition 继续按 legacy v0 replay；不得从 v0 definition 新建 run。重新发布/绑定后进入 v1，run state、model-start artifact、projection 都持久化 policy version、tool descriptors、schema bytes 和 catalog digest。

### Voice

Voice allowlist 只通过共享 discovery 和 `VoiceAgentTurnToolCatalogMaterializer` 物化。空 allowlist 是 restricted empty，不是 unrestricted。Session readiness、endpoint、module 和 invoker 传递同一 persisted snapshot/proof；6 tools 是延迟/上下文优化目标，不是请求门禁，32 KiB canonical schema 仍是硬上限。schema mismatch 或 proof drift 在执行前拒绝。

## Measured baseline

机器可读基线位于 [`tools/ci/agent_turn_tool_catalog_baseline.json`](../../tools/ci/agent_turn_tool_catalog_baseline.json)，由 Mainnet composition test 和 architecture guard 固定。

| `workspace.default` | Source families | Raw tools | Unique tools | Canonical schema bytes | Digest |
|---|---:|---:|---:|---:|---|
| commit `5af59719f` | 18 | 70 | 68 | 48,328 | legacy manifest，无统一 digest |
| issue #3512 | 5 named sets | 13 | 13 | 11,524 | `sha256:46788e82f006792a4c606a8784c036a465bd53bba143439bf7eb7e625d3a9932` |

Unique tool count 下降 80.9%，canonical schema bytes 下降 76.2%，超过 60% 验收线。这个 13-tool 数字是 route ceiling snapshot，不是每轮模型注入目标；ordinary final catalog 仍受 intent/profile 求交集并以 8 tools 为优化目标，普通回答可以是 0-tool，但合法 exact catalog 超过 8 时不得因此失败。

## Shadow、rollout 与 rollback

Shadow Profile 必须完整计算候选 authority、exact descriptors、schema bytes 和 candidate digest，并记录 `shadow` telemetry；它不得把候选 schema、prompt layer 或 exact objects交给模型/执行器。执行继续使用该入口原本的 typed baseline catalog。

System default binding 持久化两个强类型 target：candidate `target` 与 `previous_reviewed_target`。Rollout 只影响创建后的新 conversation/session/run，已有实例继续使用创建时 pin 的 immutable snapshot。合法阶段固定为：

```text
reviewed baseline at 100%
  -> new candidate at 5%
  -> same candidate at 25%
  -> same candidate at 100%
```

Partial rollout 没有 `previous_reviewed_target` 时以 `ROLLOUT_BASELINE_REQUIRED` 拒绝；跳阶段以 `ROLLOUT_STAGE_INVALID` 拒绝。Cohort 未命中的新实例解析 previous reviewed snapshot，不是 unprofiled。Rollback 只能把 target 切回 previous reviewed target 并设为 100%；原 candidate 成为新的 previous target，因此回滚也不进入 unrestricted。

每个阶段必须用真实新会话验证 typed tool call 与 terminal receipt，再推进下一阶段。旧会话只能证明旧 snapshot，没有 rollout 验收价值。Rollback 后同样创建新会话确认 previous reviewed digest 已 pin。

## Telemetry 与告警

Meter/Activity source 为 `Aevatar.GenAI`。低基数 metrics 固定记录：

- `registered`、`discovered`、`authority`、`final`、`forwarded`、`filtered`、`rejected`、`restricted_empty`；
- `schema_bytes`、`degradation`、`tool_round`、`outcome`、`time_to_first_output`。

一次 materialize 的 turn catalog 只计一次。exact-object 绑定与 narrowing 产生的派生 catalog 重新表达同一轮，不重复计数；否则每个 catalog 指标都会被派生步数放大，且放大倍数随代码路径变化，rollout 前后不可比。

`turn_class` 由本轮**实际注入**的 connected operation 决定，不由 budget 形状判定：每个 sealed profile budget 都带 connected read/write 上限作为纵深防御，按形状判定会把普通 profiled 聊天报成 `connected`，正好抹掉 rollout 对比最需要的那个维度。

Digest、profile identity 和 intent identity 只写 trace attributes，不作为 metric tags。Deny reason 使用 typed enum；不得记录 token、arguments、connection identity 或完整 tool payload。Rollout 需比较 invalid tool-call rate、task success、authorization correctness 和 TTFT；任何授权错误或明显回归都停止推进并 rollback。

## Production verification matrix

每次上线至少用本地已登录的 NyxID CLI 经 `nyxid proxy request aevatar ...` 创建新会话验证：

| Case | 必须观察的证据 |
|---|---|
| ordinary answer | 0-tool typed proof + terminal completion |
| clarification | exact `ask_user` typed call |
| web | `web_search` / `web_fetch` call + result + terminal receipt |
| Ornn | `ornn_search_skills` / `use_skill` exact call + terminal receipt |
| Aevatar | start/invoke 后 observe/artifact receipt |
| readiness | typed NyxID connection card，无 CLI fallback |
| connected service | exact read；write approval；multi-connection ambiguity 只出现 `ask_user` |
| channels | Lark/Telegram relay 的 proof 与 terminal delivery receipt |
| direct ingress | Responses/Messages/Chat Completions 行为一致 |
| unattended | typed Agent Key workflow receipt |
| voice | 6-tool target / ≤32 KiB persisted restricted proof；合法 count overflow 继续执行 |
| negative | forged hidden tool、schema mismatch、schema/connected-operation safety budget overflow 均在副作用前拒绝 |

Assistant prose、连接状态或“代码已部署”都不是成功证据。生产只做只读日志/资源观测；不得直接变更、exec、重启或 port-forward production pod。需要生产状态变更时必须写明“待运维执行后确认”。

## Governance

`tools/ci/agent_turn_tool_catalog_guard.sh` 接入 repository architecture guards，固定 forbidden default membership、duplicate handling、工具数量优化目标、schema/connected-operation 安全预算、digest/baseline、workflow v1、Voice proof、shadow observation 和 5%→25%→100% rollout。任何 snapshot 变化都必须同时更新实现、测试、manifest 与本文，并在 PR 说明原因和新的实测数据。
