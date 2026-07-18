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
