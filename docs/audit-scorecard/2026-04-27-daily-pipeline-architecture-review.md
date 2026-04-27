---
title: /daily Pipeline Architecture Review (aevatar-side)
status: active
owner: eanzhao
---

# `/daily` 架构 review — aevatar 侧

> 配套文档：[docs/canon/daily-command-pipeline.md](../canon/daily-command-pipeline.md)（端到端流程与测试参考）。
>
> 范围限定：**只评审 aevatar 侧可独立完成的改动**。NyxID 侧的契约（callback fire-and-forget、API key 无幂等、proxy 错误透传）按"现状不可改"处理，由 aevatar 侧自己消化。
>
> 本文不重复已存在的 bug issue，那些另行跟踪：#398 / #423 / #436 / #437 / #439 / #440。本文聚焦**结构性优化**，并为不在现有 issue 覆盖范围内的改动提交独立 issue。

---

## 总评

`/daily` 把"用户订阅每天的 GitHub 报告"这件本来很有边界的事，拆成了 aevatar 内部**十几个没有归属的资源**和一条**事实源不清晰**的事件链。它能跑，是因为 happy path 上每一步都同步紧密协作；只要一个边界出问题（NyxID 调用慢、投影晚到、Lark 不可达），就立刻产生孤儿资源 / 状态分裂 / 假成功——这正是上面五条 bug issue 的共性根因。

下面四类问题都按 CLAUDE.md 原则展开。每条标注：现存 issue 链接 / 新提 issue 链接 / 修复成本 / 收益。

---

## A. 与 NyxID 边界的消化

aevatar 不能改 NyxID 契约，但可以让自己对这些契约的脆弱性更鲁棒。

### A1. NyxID API key 没有 agent 概念，且创建非幂等——aevatar 用资源 actor 自己消化

**现状**
- NyxID 的 `proxy` / `rate-limit` / `approval` 全建在 API key 维度，没有"调度型 agent"。aevatar 为了拿到 per-agent 隔离，每个 agent 都创建独立 key
- NyxID `POST /api/v1/api-keys` 不幂等，同 label 调两次产生两把 key
- aevatar 在中间层（`AgentBuilderTool`）直接调 NyxID HTTP 创建 key，靠 preflight 失败时的 `BestEffortRevokeApiKeyAsync`（[AgentBuilderTool.cs:260](../../agents/Aevatar.GAgents.ChannelRuntime/AgentBuilderTool.cs)）兜底避免孤儿
- key 同时被 mirror 到多处：`SkillRunnerOutboundConfig.NyxApiKey`（actor state）+ `UserAgentCatalogEntry.nyx_api_key`（well-known 注册表 actor state）+ 单独的 `UserAgentCatalogNyxCredentialDocument.nyx_api_key`（运行时出站读取的凭据 readmodel）。公开的 `UserAgentCatalogDocument` 已 `reserved "nyx_api_key"` 不再暴露明文，但凭据 readmodel 与 actor state 仍是明文复制。

**违反原则**
> CLAUDE.md "默认路径须定义资源语义：任何「缺失即创建」策略须同时定义归属、复用规则和清理责任"

**修复方向（aevatar 侧）**
引入 `AgentExecutionCredentialGAgent`（id = agentId）：
- 持有 key 明文（actor state）；agentId 是天然的幂等键，actor 重入时检查自己 state 而不是 NyxID
- 创建 key 是状态转换 `CredentialIssuedEvent`；撤销是 `CredentialRevokedEvent`
- preflight 在 actor 内执行，结果是事件而不是补丁式的 best-effort
- 出站要 key 时按需问 actor，不在 readmodel 内 mirror 明文

副收益：
- 砍掉 `BestEffortRevokeApiKeyAsync` 整套补丁逻辑
- 砍掉 `UserAgentCatalogNyxCredentialDocument.nyx_api_key` 与 `UserAgentCatalogEntry.nyx_api_key`，缩小 LLM-adjacent secret 的泄露面（公开 catalog 已用 `reserved` 把字段腾出，只剩凭据 readmodel + actor state 两处需要清理）
- 与 NyxID 的"key 创建非幂等"解耦——aevatar 这边永远只新建一次

> 跟踪：[#445 refactor(daily-credential): introduce AgentExecutionCredentialGAgent](https://github.com/aevatarAI/aevatar/issues/445)

---

### A2. NyxID → aevatar callback 是 fire-and-forget——aevatar webhook handler 必须 accept-fast

**现状**
- `NyxIdChatEndpoints.Relay.cs:28` `HandleRelayWebhookAsync` 在同步路径里做：parse → JWT 验签 → scope resolve → activity normalize → 投递 ConversationGAgent inbox
- 任何一步抛异常或返非 2xx，NyxID 那边只更新 `channel_messages.callback_status='failed'`，**消息永久丢失**
- issue #398 是直接症状

**违反原则**
> CLAUDE.md "事实源唯一" / "committed event 必须可观察"——当前 inbound message 在 aevatar 侧的"持久化"是延迟到 ConversationGAgent inbox 才发生的，handler 中段失败 = 丢失

**修复方向（aevatar 侧）**
两段 webhook：
1. **accept 段**：从请求 body 拿到字节流后立即写入持久化 inbox（一个 `RelayInboundInboxGAgent` 或直接 append-only 存储），返 202
2. **process 段**：异步 worker 从 inbox 消费，做鉴权 / scope resolve / normalize / 派发；任何失败留在 inbox，可重放 / 死信

收益：
- aevatar 侧的代码失败不再会让 NyxID 那边的消息丢
- 鉴权失败的 payload 仍留有审计痕迹（当前是直接 401 抛掉）
- 对未来"NyxID 加 outbox 重试"也是兼容的——aevatar 重复 accept 是幂等的（`message_id` 已经在 payload 里）

不解决的：NyxID 完全没投到 aevatar 的场景（issue #398 hypothesis 1/2/3）——那是 NyxID/Lark/网络的事，aevatar 无法弥补。

> 跟踪：[#449 harden(webhook): nyxid-relay handler accept-fast + persisted inbox](https://github.com/aevatarAI/aevatar/issues/449)

---

### A3. `nyxid_proxy` 工具响应分类——issue #439 的 aevatar 侧解法

**现状**
- NyxID proxy 上游 4xx/5xx 直接透传 status + body
- aevatar `src/Aevatar.AI.ToolProviders.NyxId/Tools/NyxIdProxyTool.cs:111-120` 直接把 NyxID 返回字符串透给 LLM
- LLM 拿到 `{"error":true,"status":401}` 没法和"GitHub 真返了空数组"区分，套 prompt "无活动就坦白说"模板，输出空报告

**违反原则**
> CLAUDE.md "外部协议必须 JSON 时，仅在 Host/Adapter 边界做协议转换；进入应用/领域/运行时层后恢复为 Protobuf"——上游 raw JSON 不应一路透到 LLM

**修复方向（aevatar 侧）**
`nyxid_proxy` 中间件按 HTTP status / body shape 分类：
```csharp
sealed class ToolResult {
    Ok(JsonElement data, int status),
    Empty(int status),
    Error(string kind, int status, string detail, string? requestId)
}
```
LLM 拿到的不是 raw blob 而是判定后类型；prompt 改写为按 `tool_status` 分支（`Error` 分支必须列出失败 endpoint，不允许走"无活动"模板）。

> 已在 issue [#439](https://github.com/aevatarAI/aevatar/issues/439) 的 acceptance criteria 内，本文不重复提 issue

---

## B. aevatar 内部架构重构

### B1. `UserAgentCatalogGAgent` 戴了两顶帽子，issue #440 是结构性副作用

**现状**
- `agents/Aevatar.GAgents.ChannelRuntime/UserAgentCatalogGAgent.cs:91-114`，well-known 单 actor，同时承担：
  - **集合成员管理者**（哪些 agent 存在）—— 长期事实拥有者，OK
  - **逐条执行状态汇总者**（每个 SkillRunner 每次执行都发 `UserAgentCatalogExecutionUpdateCommand` 来更新自己内部 entry）—— **不该是它的职责**
- early-return guard：
  ```csharp
  if (State.Entries.All(x => !string.Equals(x.AgentId, command.AgentId, StringComparison.Ordinal))) {
      Logger.LogWarning("...missing entry...");
      return;
  }
  ```
- issue #440 "首次执行不刷新 LastRun/NextRun"：init 端 upsert 还没落 state，trigger 端 ExecutionUpdate 已到，被 guard 静默丢

**违反原则**
> CLAUDE.md "单线程 actor 不做热点共享服务" / "聚合必须 actor 化" / "Actor 即业务实体"——catalog actor 不该当所有 agent 执行更新的漏斗。

**修复方向**
- catalog actor 只处理 `UpsertCommand` / `TombstoneCommand`，**移除** `ExecutionUpdateCommand` 整条路径
- `SkillRunnerGAgent` 只 commit 自己的 `SkillRunnerExecutionCompletedEvent` / `Failed`
- `UserAgentCatalogProjector` 同时订阅两路事件（catalog upsert/tombstone + SkillRunner 执行），按 `agent_id` 主键覆盖写到 `UserAgentCatalogDocument`
- guard 自然消失（projector 是覆盖语义，事件晚到不影响最终态）
- issue #440 race 自动消失，**砍掉一条 actor 间命令耦合**（同时解决下面 B6）

issue #440 的"Suggested fix direction"列了 coalesce / defer / watermark 三种修补方案，但**结构性的**修复是上面这条——把 catalog 还原为纯集合管理。修补方案能让现状跑通，但耦合还在；下一次类似的命令竞争还会出新 bug。

> 跟踪：[#444 refactor(daily-catalog): catalog as pure set-membership; projector consumes SkillRunner committed events directly](https://github.com/aevatarAI/aevatar/issues/444)

---

### B2. `UserConfigGAgent` 用 bot scope，丢了 Lark 用户身份——issue #436/#437 根因

**现状**
- `ChannelInboundEvent.sender_id`（per-Lark-user）已在 proto 里，但从 `ChannelConversationTurnRunner.cs:787` 进 `AgentToolRequestContext` 后没传下去
- `AgentBuilderTool.cs:186-187` 用 `scope_id`（per-bot）当 user-config 的键
- 一个 bot 下所有 Lark 用户共享一把 user-config，last writer wins

**违反原则**
> CLAUDE.md "事实源唯一" / "Actor 即业务实体"——每个 Lark 用户应该是 first-class 业务实体，有自己的 actor

**修复方向**
issue [#436](https://github.com/aevatarAI/aevatar/issues/436) 给了两条修复路径（composite scope vs. 引入 channel-user-binding actor）。**架构上正解是后者**：`LarkUserGAgent`（id = `lark:{registrationScopeId}:{senderId}`）承载未来的 GitHub 偏好、跨 agent ACL、跨 agent 查询入口。

Composite scope 是补丁，未来只会越长越多 key 拼接。本文**支持** issue #436 里"Channel-user-scoped actor"路径。

> 已在 issue [#436](https://github.com/aevatarAI/aevatar/issues/436) / [#437](https://github.com/aevatarAI/aevatar/issues/437) 内，本文不重复提 issue（仅明确表态）

---

### B3. `AgentBuilderTool.CreateDailyReportAgentAsync` 是 god 函数，违反"命令骨架内聚"

**现状**

`AgentBuilderTool.cs:178-340` 这 162 行做了：
1. 解析 scope / username（应用层）
2. 调 NyxID `/users/me`（外部 HTTP）
3. 调 NyxID `/api-keys`（外部 HTTP，副作用：建资源）
4. 调 NyxID `/proxy/.../user` preflight（外部 HTTP，副作用：可能撤销）
5. 创建/取 actor（运行时副作用）
6. 主动 prime projection scope（应用层管投影）
7. dispatch `InitializeSkillRunnerCommand`
8. dispatch `TriggerSkillRunnerExecutionCommand`
9. **同步轮询投影 20 次**（query-time 探测，[AgentBuilderTool.cs:310-317](../../agents/Aevatar.GAgents.ChannelRuntime/AgentBuilderTool.cs)）
10. 写偏好（actor 命令）
11. 拼返回 JSON

一个人干了 application + integration + runtime + projection 四层的事，刚好踩遍 CLAUDE.md "中间层"反模式：编排状态、副作用混合、ACK 不诚实（同步窗口里既要决定"建好了"又要决定"看到投影了"）、query-time priming。

**违反原则**
> CLAUDE.md "命令骨架内聚: Normalize → Resolve Target → Build Context → Build Envelope → Dispatch → Receipt → Observe" + "ACK 诚实" + "禁止 query-time replay/priming"

**修复方向**
- tool 只产出 `CreateDailyReportSubscriptionCommand` 一个 envelope 并 dispatch
- 第 2-4 步的 NyxID 调用下沉到 `AgentExecutionCredentialGAgent`（A1 的资源 actor）saga 内
- 第 7-8 步的 dispatch 由订阅 actor 自己 self-continuation 完成
- 第 6 步的 prime / 第 9 步的轮询全部砍掉——committed 即可观察，readmodel 由 projector 异步追上
- tool 同步只承诺 "accepted + commandId"——符合 "ACK 诚实"
- 用户在 Lark 看到的"agent registered + first run"由 actor committed event 通过 outbound 通道反向推送

> 跟踪：[#446 refactor(daily-builder): decompose AgentBuilderTool into command + saga; remove projection polling](https://github.com/aevatarAI/aevatar/issues/446)

---

### B4. `SkillRunnerGAgent` 名字描述技术角色，且把"订阅"和"运行"揉在一个 actor

**现状**
- `SkillRunnerGAgent` 是技术角色名（"运行 skill 的东西"）
- 它用 `template_name` 字段决定自己是 daily_report 还是 social_media——多态 actor，业务语义全在 `skill_content` 这段冻结的 prompt 字符串里
- `State` 揉了两类东西：
  - **订阅事实**（cron、target、GitHub binding、skill_content）—— 长期
  - **执行历史**（last_run_at, last_output, error_count, retry_attempt）—— 自然 session-scoped
- `ScheduleRetryAsync` 和 `ScheduleNextRunAsync` 共用 `ChannelScheduleRunner` + retry lease 机制——**互相可能抢**

**违反原则**
> CLAUDE.md "Actor 以业务命名... 禁止 WriteActor、ReadModelActor、StoreActor 等技术功能命名"
>
> CLAUDE.md "默认短生命周期: 一次执行/会话/编排即完成的能力，建模为 run/session/task-scoped actor; 长期 actor 限定事实拥有者"
>
> CLAUDE.md "Actor 即业务实体: 一个 actor = 一个业务实体（数据与方法同住）"

**修复方向**
- `DailyReportSubscriptionGAgent`（长期，订阅事实拥有者）+ `DailyReportRunGAgent`（session-scoped，每次执行一个）
- 重试逻辑完全塞进 run actor（一个 run 失败 = run actor 进入 retry-scheduled 状态），订阅 actor 不知道"retry"是什么
- 查询订阅历史 = 查 run readmodel
- run actor 拆出来后，issue #439 的"假成功"改 run 状态机就够了，不再污染订阅事实
- social_media 同模式拆出 `SocialMediaPostSubscriptionGAgent` + `SocialMediaPostRunGAgent`——`skill_runner` 这个泛化抽象消失

> 跟踪：[#447 refactor(daily-actor): split SkillRunnerGAgent into Subscription + Run actors](https://github.com/aevatarAI/aevatar/issues/447)

---

### B5. `lark_receive_id*` 在创建时被冻结，迟绑定的机会被提前用掉

**现状**
- `AgentBuilderTool.cs:274` `ResolveDeliveryTarget(conversationId, agentId)`（实现在 [AgentBuilderTool.cs:1881](../../agents/Aevatar.GAgents.ChannelRuntime/AgentBuilderTool.cs)）在创建时把 `chat_id` / `union_id` 一对全算好存入 `OutboundConfig`
- Cross-app、chat 改名、bot 被踢、用户离开群——任何一种都让 receive_id 永久失效
- 用户唯一可行的恢复手段是 `/delete-agent` 重建（已在文档里提示，但这是把架构问题外包给用户）

**违反原则**
> CLAUDE.md "本地可用不等于分布式正确: 依赖本地 runtime 偶然细节才成立的实现视为未完成设计"——这里"本地偶然"是"创建时的 chat 拓扑就是未来执行时的拓扑"

**修复方向**
- `OutboundConfig` 只存逻辑引用：`(platform, conversation_canonical_key, owner_lark_user_id)`
- 出站时由 `ChannelDeliveryResolver`（按 platform 一对一的 adapter）查当前 receive_id，late-binding
- 把 binding 失效作为可观察故障暴露给用户（"机器人不在该聊天，请 /rebind 选择其他聊天"），而不是变成永远失败的 agent
- 与 B2 的 `LarkUserGAgent` 协作：用户身份是 first-class 后，"用户在哪个聊天能收到"自然挂在 LarkUser 下

> 跟踪：[#448 refactor(daily-outbound): late-bind lark_receive_id at send time](https://github.com/aevatarAI/aevatar/issues/448)

---

### B6. `SkillRunnerGAgent` 主动调对方 actor 的命令——actor 间编排耦合

**现状**
`SkillRunnerGAgent.HandleTriggerAsync` 里：
```csharp
await UpdateRegistryExecutionAsync(StatusRunning, ...);  // 发 ExecutionUpdateCommand 到 catalog
```
actor 主动告诉别的 actor "你该这么改自己的状态"。SkillRunner 不该知道 catalog 的命令 schema，更不该知道"每次执行后要更新 catalog"。

**违反原则**
> CLAUDE.md "Actor 即业务实体... 数据与方法同住"

**修复方向**
和 B1 一起做——projector 同时订阅两路事件，actor 之间不再相互发命令。本节不另开 issue，跟随 B1 的 issue。

---

### B7. `WaitForCreatedAgentAsync` 是 query-time priming 的软版本

**现状**
`AgentBuilderTool.cs:310-317` 在请求路径里轮询 `IUserAgentCatalogQueryPort.GetStateVersionAsync` 最多 20 次。

**违反原则**
> CLAUDE.md "禁止 query-time replay/priming: ApplicationService 不得在 query 方法内同步补投影"

技术上这没读 event store、没补跑投影，所以擦边过；但"在请求路径里同步等投影"这个方向就是反的。和 B3 一起改：dispatch 后立刻返 "accepted + commandId"，客户端按 commandId 异步查 readmodel。本节不另开 issue，跟随 B3 的 issue。

---

## C. 跨层 / 设计完备性

### C1. proxy API key 是 LLM-adjacent secret，泄露面比看起来大

**现状**

key 当前的物理位置：
- `SkillRunnerOutboundConfig.NyxApiKey`（actor state，protobuf 持久化）
- `UserAgentCatalogEntry.nyx_api_key`（well-known 注册表 actor state）
- `UserAgentCatalogNyxCredentialDocument.nyx_api_key`（运行时出站读取的凭据 readmodel；与公开 catalog 分离）
- 出站 HTTP header
- 工具 middleware 上下文

历史上公开的 `UserAgentCatalogDocument` 也曾持有过 `nyx_api_key`，已通过 proto `reserved "nyx_api_key"` 拆出独立凭据 readmodel。当前剩余泄露面主要是凭据 readmodel + 两处 actor state 的明文复制。

**修复方向**
随 A1（`AgentExecutionCredentialGAgent`）一起：key 只在 credential actor 内持有；凭据 readmodel 和 catalog entry 都改存 `api_key_id` 引用，不存明文；出站 sender 按需问 credential actor。

本节合并到 A1 issue 内，不另开。

---

### C2. 失败通知通道 = 主投递通道（同一条 proxy）

`SkillRunnerGAgent.TrySendFailureAsync` 走 `s/api-lark-bot` proxy，主投递也走它。如果主投递失败原因是这条通道有问题，失败通知也到不了用户——故障可观测性归零。

已在 issue [#423 §C](https://github.com/aevatarAI/aevatar/issues/423) 内，本文不重复提 issue。

---

### C3. Daily prompt 是代码里硬编码的字符串模板

**现状**
- [`AgentBuilderTemplates.cs:48-64`](../../agents/Aevatar.GAgents.ChannelRuntime/AgentBuilderTemplates.cs) 是个 StringBuilder
- 改一行 prompt → 重新部署 → 已存在的 agent 仍用 frozen 在自己 state 里的旧 prompt
- 没有"模板版本"概念

**违反原则**
> CLAUDE.md "渐进演进: 开发期可用本地/内存实现，但生产语义必须能无缝迁移到分布式与持久化"——prompt 是事实但被当成代码常量

**修复方向**
`DailyReportTemplateCatalog`（actor 或 readmodel 文档），prompt 是有版本号的资源，agent 引用 `template_id + template_version`。issue #423 想做"更丰富的内容"时，在不破坏老 agent 的前提下出新版自然就有了。

> 跟踪：[#450 refactor(daily-prompt): versioned daily report prompt templates](https://github.com/aevatarAI/aevatar/issues/450)

---

## D. 优先级矩阵

按"修复成本 / 影响面 / 是否解决多个已知 bug"排序：

| # | 改动 | 解决/关联 | 成本 | 收益 | 跟踪 |
|---|------|----------|------|------|------|
| 1 | catalog 改纯 set-membership + projector 直接消费 SkillRunner committed event（B1 + B6） | #440 + 砍 actor 间命令耦合 | 中 | 大 | [#444](https://github.com/aevatarAI/aevatar/issues/444) |
| 2 | `LarkUserGAgent`（per-platform-user 实体）+ 偏好挂它（B2） | #436 / #437 | 中 | 大 + 长期复利 | [#436](https://github.com/aevatarAI/aevatar/issues/436) / [#437](https://github.com/aevatarAI/aevatar/issues/437) |
| 3 | `nyxid_proxy` 工具响应分类（A3） | #439 | 小 | 大 | [#439](https://github.com/aevatarAI/aevatar/issues/439) |
| 4 | `AgentExecutionCredentialGAgent`（A1 + C1） | 砍 best-effort revoke + 缩小 key 泄露面 + 与 NyxID 幂等问题解耦 | 中 | 中 | [#445](https://github.com/aevatarAI/aevatar/issues/445) |
| 5 | webhook accept-fast + persisted inbox（A2） | aevatar 侧 #398 一类 | 中 | 中 | [#449](https://github.com/aevatarAI/aevatar/issues/449) |
| 6 | 拆 `DailyReportSubscription` + `DailyReportRun`（B4） | retry-定时混跑 / 历史可查 / 命名 | 大 | 中 + 长期复利 | [#447](https://github.com/aevatarAI/aevatar/issues/447) |
| 7 | `lark_receive_id` 改运行时迟绑定（B5） | cross-app & chat 改名 | 中 | 中 | [#448](https://github.com/aevatarAI/aevatar/issues/448) |
| 8 | `AgentBuilderTool` 解构成 command + saga（B3 + B7） | "ACK 诚实" + 砍 query-time priming | 大 | 中 | [#446](https://github.com/aevatarAI/aevatar/issues/446) |
| 9 | prompt 模板版本化（C3） | #423 实施前置 | 小 | 中 | [#450](https://github.com/aevatarAI/aevatar/issues/450) |
| 10 | failure-notification 通道与主通道解耦（C2） | 故障可观测性 | 小 | 小但运维体感大 | [#423 §C](https://github.com/aevatarAI/aevatar/issues/423) |

**最高 ROI：#1 和 #4**。两件都是结构性修复（不是补丁），各解决一个已被 QA 报出来的 bug 或安全顾虑，且修复方式都是"砍命令 / 砍重复 truth"——**净删代码**。

---

## E. 新 issue 总览

本次评审新提的 architectural 重构 issue（其他 bug-level 修复已存在 issue，详见 D 表"跟踪"列）：

| Issue | 标题 | 章节 |
|-------|------|------|
| [#444](https://github.com/aevatarAI/aevatar/issues/444) | refactor(daily-catalog): make UserAgentCatalogGAgent pure set-membership; projector consumes SkillRunner committed events directly | B1 + B6 |
| [#445](https://github.com/aevatarAI/aevatar/issues/445) | refactor(daily-credential): introduce AgentExecutionCredentialGAgent — proxy API key as actor-owned resource | A1 + C1 |
| [#446](https://github.com/aevatarAI/aevatar/issues/446) | refactor(daily-builder): decompose AgentBuilderTool god function into command + saga; remove query-time projection polling | B3 + B7 |
| [#447](https://github.com/aevatarAI/aevatar/issues/447) | refactor(daily-actor): split SkillRunnerGAgent into DailyReportSubscriptionGAgent + DailyReportRunGAgent | B4 |
| [#448](https://github.com/aevatarAI/aevatar/issues/448) | refactor(daily-outbound): late-bind lark_receive_id at send time instead of freezing at agent creation | B5 |
| [#449](https://github.com/aevatarAI/aevatar/issues/449) | harden(webhook): nyxid-relay handler must accept-fast and persist to inbox before processing | A2 |
| [#450](https://github.com/aevatarAI/aevatar/issues/450) | refactor(daily-prompt): versioned daily report prompt templates (prerequisite for #423 enrichment) | C3 |

依赖关系建议先后：#445（credential actor）+ #444（catalog 重构）可并行 → #447（actor 拆分）依赖 #445 + #450 → #446（builder 解构）依赖 #447 → #448（迟绑定）可在 #447 后并行 → #449（webhook 加固）独立可任意时机进行。
