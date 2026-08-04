---
title: NyxID Chat Agent Profile Binding
status: canonical
owner: Aevatar AI
---

# NyxID Chat Agent Profile Binding

NyxID Chat 是第一阶段唯一正式消费 Agent Profile 的 runtime。管理面的 Definition 由 `AgentProfileGAgent` 持有，新 Conversation 只从 Actor-backed protected execution read model 解析一次 published snapshot，并把 immutable clone 固化到 Conversation Actor state。

## 身份边界

- `scopeId` 是用户 Profile owner；system 是独立 typed platform owner。
- `profileId` 是 Profile 的 opaque identity；`profileSlug` 只用于 owner namespace 内的人类引用。
- `actorId` 是 Conversation 地址，不能从 profileId/slug 推导，也不承载 Profile 事实。
- NyxID `userId` 只用于 system Admin 授权与审计，不得与 scopeId 比较、互换或由字符串规则推导。

显式 create reference 只有 `ownerKind = caller | system` 与 `profileSlug`。请求不能提交 profileId、其他用户 scope/user ID、inline Profile、文件路径或通用 object。Host/Application 在授权后把 human reference 一次解析为 typed owner + opaque profileId；后续 execution resolution 以 profileId 为 load-bearing identity。

## 创建解析

解析顺序固定为显式 reference、scope default、system default rollout、genuinely unprofiled。每一级都先从 Catalog read model 得到 typed binding target，再从 Protected execution read model 读取 snapshot，并核对：

- target owner/profileId 与 execution document identity 完全一致；
- published revision 与 snapshot digest 与 binding 证据一致；
- outer published digest 与 inner runtime snapshot digest 均有效；
- agent kind 为 `nyxid.chat`，route tool set 是 Host 静态允许项；
- system binding 的 enabled/cohort admission 对当前新 actorId 生效。

明确 binding 不可用时必须在创建 Actor 之前 fail closed，返回 typed unavailable/integrity failure；不得继续尝试下一层。只有没有任何显式或默认 binding 时，create command 才不携带 Profile snapshot。

Create resolver 不访问 Ornn、文件、event store 或进程内 Profile registry，也不在 query 方法里 prime projection/activate Actor。Chat route 的明确拒绝仍是 403；Profile unavailable/integrity failure 使用稳定的 503-class typed error；成功仍是 honest `202 Accepted`，不暗示 Conversation state 已提交或已投影。

## Conversation 固化

`RoleGAgentState.agent_profile` 是存量 Conversation 的唯一 Profile 事实源：

- 未绑定且 create command 带有效 snapshot：先提交 `AgentProfileBoundEvent`；
- 已绑定且 deterministic bytes 相同：幂等继续；
- 已绑定后收到不同、缺失或 digest 无效的 snapshot：拒绝，不改写既有 binding；
- genuinely unprofiled create：保持 `agent_profile` 缺失。

Profile 重新发布只影响之后创建的 Conversation。旧实例不 hot-upgrade、不 lazy rebind、不 replay/backfill；Host/Actor 重启也不会重新解析其 Profile。

## Turn Authority

Turn-local materialization 继续使用已固化 snapshot 与既有 `AgentProfileTurnCatalogMaterializer`。`RoleGAgentState.agent_profile_turn_authority` 是当前 turn authority 的唯一 durable fact，`session_id + attempt` 是 reconciliation fence。Exact skill body、prompt layer、tool object、token、credential、header 和 adapter/runtime instance 不进入 actor state/read model。

Runtime 先对 route-owned tools、registered tools、typed visibility、Profile maximum/recovery/task policy 与 caller authorization 取交集，再执行 bounded routing/classification。任何 profile、exact fetch、identity、collision、capability 或 integrity 失败都只能继续缩权；交集为空即 restricted-empty，不能退回 unrestricted。

Request-local `AgentProfileTurnCatalog` 不是 DI service、cache 或进程级上下文。非 Profile consumer 必须显式传 `null`；当前只有 genuinely unprofiled NyxID Chat 允许这个值表达未绑定。
