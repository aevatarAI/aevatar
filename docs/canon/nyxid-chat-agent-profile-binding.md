---
title: NyxID Chat Agent Profile Binding
status: canonical
owner: Aevatar AI
---

# NyxID Chat Agent Profile Binding

NyxID Chat 的 agent profile 是 conversation actor 的创建输入。Mainnet Host 只在进程启动时读取、校验并 seal 一份部署快照；create resolver 对每次新建会话读取一次本地 clone，不在请求路径访问 Ornn、NyxID 或其他网络服务。

## 权威边界

`NyxIdChatAgentProfileOptions` 只承载部署开关、稳定外部引用 `nyxid-chat` 和 typed profile payload。恢复工具必选名单与旧工具拒绝名单属于 Host 安全政策，由独立的 immutable `NyxIdChatAgentProfileValidationBaseline` 提供，不从 profile 配置绑定，也不进入 snapshot、digest、command、event 或 actor state。

Host 启动校验固定覆盖三个 schema root：

1. `AgentProfileSnapshot.Descriptor` 可达的仓库自有 Protobuf graph。
2. `NyxIdChatAgentProfileOptions` 的仓库自有 public property graph。
3. `NyxIdChatAgentProfileValidationBaseline` 的仓库自有 public property graph。

扫描只检查 schema 名称，不读取配置值，不遍历 BCL 实现或全仓 assembly。启用 profile 或提供 profile payload 时，两组 baseline 都必须非空，且 profile 必须通过完整 identity、bounds、policy subset、known tool-set 和 baseline 校验。默认的 disabled、无 payload、empty baseline 配置可以启动。

## 创建与绑定

create resolver 继续以 `CHAT_SOURCE_KIND_DIRECT` 的 chat-route decision 为权威。启用 profile 时，route 的 `ForwardToModel.ToolSetRef.Name` 必须与 snapshot 的 `route_tool_set_ref` 按 `Ordinal` 完全一致；不一致在 actor 创建前沿用现有 503 admission-unavailable 语义。route policy 的明确拒绝仍返回 403，成功仍返回原有 202 accepted receipt、`Location` 和 `statusUrl`。

`NyxIdChatGAgent` 在 creation-started event 和 registry I/O 前验证 snapshot digest并执行一次性绑定：

- 未绑定且 command 带有效 snapshot：先提交 `AgentProfileBoundEvent`。
- 已绑定且完整 deterministic bytes 相同：幂等继续，不追加 binding event。
- 已绑定后收到不同 snapshot、缺失 snapshot 或无效 digest：拒绝并停止注册。
- 未绑定且 command 不带 snapshot：保持 legacy 行为。

`RoleGAgentState.agent_profile` 是会话绑定的唯一权威当前态。完整 sealed snapshot 随 committed event 进入既有 observation 主链；Host options 不是存量会话的事实源。

## Identity 与演进

`ExactRemoteSkillRef` 和 `ExactRemoteSkillsetRef` 只接受非零、lowercase canonical `D` GUID，以及 Ornn `<major>.<minor>` literal version。validator 不 Trim、不转小写、不做读取时 normalization，也不附加 UUID version/variant 限制。

协议采用 additive tags：`RoleGAgentState.agent_profile = 12`，`NyxIdChatConversationCreateCommand.agent_profile = 3`。旧 bytes 的字段缺失值为空，因此旧会话保持未绑定；系统不 replay、backfill、lazy bind 或 hot-upgrade。配置和 validation baseline 变更都需要重新部署并重启，只影响之后创建的会话。

本边界不增加公开 profile diagnostics、readmodel/query/API、create-time exact fetch、release verification或第二条 chat pipeline。具体 reviewed GUID、publisher、member、recovery/deny 名单和 enablement 属于发布配置，不属于本协议。

## Turn-local materialization 与执行约束

绑定 profile 的真实请求在 completed replay 判定之后只物化一次 immutable `AgentProfileTurnCatalog`。该值以无默认值的显式参数进入 main chat、step executor 和 prompt 组合；它不是 DI service、actor state、readmodel、cache 或进程级上下文。未绑定会话以及 Workflow、Studio、relay、Household、Scheduled 和 AgentRun 等非 profile consumer 必须显式传 `null`，只有这个值表示 unprofiled。

NyxID materializer 先从 route-owned tool set、当前已注册工具、既有 typed visibility、maximum policy 和 recovery policy 取交集，再执行 alias 或 bounded streaming classifier。`ENFORCED` 只有在 exact GUID、literal version、expected name、reviewed publisher、hash evidence、唯一 `SKILL.md` 和 UTF-8 正文上限全部通过后，才加入 selected-skill prompt layer，并把 member task policy 与 recovery policy 的并集继续限制在上述交集内。Ornn adapter 只读取两个 version-pinned GUID endpoint，不按 name、latest 或 search 回退。

非空 catalog 即使没有任何工具，也表示 restricted-empty。materializer 不只保存 `FinalAllowedToolNames`，还把 route-owned exact `IAgentTool` 对象冻结到 `RouteOwnedTools`。route tool set 与 actor registered tools 的同名项只有在引用相同时才能合并；同名不同引用视为 collision，整名删除并降权，不能靠名称选择其中一个实例。

`ChatRuntimeRequestBuilder` 把这些 exact 对象并入最终 `LLMRequest.Tools`，再与 `FinalAllowedToolNames` 和既有 `AgentToolVisibilityScope` 取交集。每次 LLM call 在 middleware 前捕获 schema object + visibility authorization fence，middleware 只能继续缩权；返回同名但引用不同的工具时，fence 整名拒绝。模型 schema 和执行能力因此由同一组 object capability 决定，而不是由名称清单与 actor-level `ToolManager` 分别决定。

main、step、structured tool call、text fallback、final fallback、skill recovery、tool outcome lookup 与 direct `ToolCallLoop` 都只从当前 final request 的 `Tools` 构造 request-local `ToolManager`。`Tools = null` 表示本次请求没有工具能力，不得回查 actor-level manager；模型伪造、middleware 替换或后续 fallback 恢复出的非 exact tool call 都在执行前拒绝。

`SHADOW` 只保留当前请求的 candidate identity 与 bounded diagnostic，权限和 prompt body 固定为 recovery，不读取、解析或注入 candidate skill body，也不解析 candidate task tool set。profile digest、classifier、registry、tool discovery、collision、capability、exact fetch、identity、integrity或正文校验任一失败，都只能降为继续取交集后的 recovery；若交集为空则保持 restricted-empty，不能退回 unrestricted。
