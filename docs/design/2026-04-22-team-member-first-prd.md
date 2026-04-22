---
title: "Team Member First PRD"
status: draft
owner: codex
last_updated: 2026-04-22
references:
  - "./2026-04-20-studio-member-workbench-prd.md"
  - "./2026-04-21-studio-workflow-member-lifecycle-prd.md"
  - "./2026-04-21-studio-workflow-bind-information-architecture.md"
---

# Team Member First PRD

## 0. 文档优先级

本文是当前 `scope / team / member / invoke` 语义的唯一基线。

后续所有 `Teams / Team Detail / Studio / Create Team / Bind / Invoke` 相关文档，都必须遵循这里的对象定义与路由规则。

---

## 1. 结论

当前项目真实跑出来的底层模型仍然更像：

`scope -> service -> endpoint -> run`

但产品想表达的模型必须收敛成：

`scope -> team -> team entry -> team member -> implementation -> published service -> endpoint -> run`

这里有两个关键判断：

1. `scope` 不是 `team`
2. `team` 可以被 invoke，但必须先通过显式的 `team entry` 落到某个具体 `member`

如果这两层不写死，用户会一直搞不清：

1. 我现在在哪个 scope
2. 我现在在哪个 team
3. 我现在操作的是哪个 member
4. “Invoke team” 最终到底调的是谁

---

## 2. 核心定义

## 2.1 Scope

`Scope` 是用户或组织拥有的工作空间边界。

它回答的是：

1. 当前是谁的工作空间
2. 当前可见哪些 team
3. 当前的权限、凭证、目录和集成归属于谁

本期约束：

1. 一个用户通常默认落在自己的 `scope`
2. 用户也可以切换到某个组织 `scope`
3. `scope` 不是 `team`

## 2.2 Team

`Team` 是 `scope` 下的一等业务对象。

它回答的是：

1. 这支团队叫什么
2. 这支团队属于哪个 `scope`
3. 这支团队有哪些成员
4. 这支团队最近发生了什么
5. 这支团队是否具备可调用入口

关键约束：

1. `teamId` 全局唯一
2. `team` 可以先存在，再逐步补成员和入口
3. `team` 不是某个 `service` 的别名

## 2.3 Team Entry

`Team Entry` 是 `team` 的团队级调用前门。

它回答的是：

1. 这个 `team` 被 invoke 时默认落到哪个 `member`
2. 这个 `team` 当前是否具备可调用入口
3. 当前团队入口暴露的是哪个契约、哪个 revision、哪个 endpoint

它至少包含：

1. `entryTargetMemberId`
2. `publishedServiceId`
3. `defaultEndpointId`
4. `activeRevisionId`
5. `status`

关键原则：

1. `team` 支持被 invoke，不代表系统会“调用一个抽象 team”
2. 所有 `Invoke Team` 都必须先 resolve `Team Entry`
3. `Team Entry` 指向具体 `member`

## 2.4 Team Member

`Team Member` 是 `team` 下的一等能力对象。

它必须具备稳定身份，而不是运行时推导结果。

最少字段：

1. `memberId`
2. `scopeId`
3. `teamId`
4. `displayName`
5. `description`
6. `implementationKind`
7. `implementationRef`
8. `publishedServiceId`
9. `status`
10. `createdAt`
11. `updatedAt`

关键原则：

1. `memberId` 不是 `serviceId`
2. `memberId` 不是 `actorId`
3. `memberId` 不是 `workflowId`
4. `memberId` 是“这个 team 里的这个成员是谁”的稳定身份

## 2.5 Implementation

`Implementation` 不是成员本体，而是成员的实现方式。

当前只允许三种：

1. `workflow`
2. `script`
3. `gagent`

因此：

1. 用户先选中一个 member
2. 再决定这个 member 用哪种 implementation
3. 再进入对应的 Build surface

## 2.6 Published Service

`Published Service` 是 team 或 member 的发布面，不是业务对象本体。

在本期范围内，需要区分两种发布面：

1. `Team Entry Published Service`
   团队级调用前门
2. `Member Published Service`
   成员级直接调用面

关键原则：

1. `service` 只是发布面，不是 `team` 或 `member` 本体
2. `Team Entry Published Service` 必须通过 `Team Entry` 绑定到某个 member
3. `Member Published Service` 只表达某个 member 的直接调用面

## 2.7 Runtime Actor / Run

`Actor` 和 `Run` 都是运行时对象，不是团队或成员身份。

它们只回答：

1. 这次运行由谁执行
2. 这次运行发生了什么
3. 现在运行停在哪

不能回答：

1. 当前 scope 里有哪些 team
2. 某个 team 里有哪些 member
3. 某个 member 的产品身份是什么

---

## 3. 领域不变量

## 3.1 Scope 先于 Team，Team 先于 Member

正常链路必须是：

1. 进入某个 scope
2. 在 scope 下创建 team
3. 在 team 下新增 member
4. 为 member 选择实现方式
5. 配置 team entry 或 member publish
6. 调用并观察 team 或 member

## 3.2 Team 支持 invoke，但必须经过 Team Entry

允许：

1. `Invoke Team`
2. `Invoke Member`

但二者语义不同：

1. `Invoke Team`
   先 resolve `teamId -> Team Entry -> entryTargetMemberId`
2. `Invoke Member`
   直接对某个 member 的 published contract 发起调用

## 3.3 Member 先于 Implementation

`workflow / script / gagent` 是 member 的实现方式，不是 member 类型目录。

因此：

1. 左侧 rail 应该列 Member
2. Build 区应该切 Implementation
3. 不能反过来

## 3.4 Workflow Role 不是 Team Member

`workflow role` 是 workflow 内部实现结构，不天然等于 team member。

只有当用户显式创建某个 `member`，并把它的 implementation 设为 workflow 时，这个 workflow 才是某个 member 的实现体。

## 3.5 Service 是发布面，不是身份

`serviceId` 只能表达：

1. team entry 的发布面
2. member 的发布面

不能表达：

1. team 身份
2. member 身份

---

## 4. 关键用户链路

## 4.1 Invoke Team

用户动作：

1. 进入 Team Detail
2. 点击 `Invoke Team`

系统语义：

1. resolve `teamId`
2. 读取 `Team Entry`
3. resolve `entryTargetMemberId`
4. 通过 `Team Entry Published Service` 发起调用
5. 返回 run、events、summary

## 4.2 Invoke Member

用户动作：

1. 进入某个 team
2. 选择某个 member
3. 点击 `Invoke`

系统语义：

1. resolve `memberId`
2. 读取该 member 的 published service
3. 发起调用
4. 返回 run、events、summary

## 4.3 Build / Bind / Observe Member

Studio 的直接对象仍然是 `member`。

也就是说：

1. team 可以被 invoke
2. 但 Studio 仍然是 `member-first`
3. team-level invoke 配置属于 Team Detail / Team Entry 设置，不是把 Studio 重新变成 team builder

---

## 5. 当前项目哪里不对

## 5.1 把 Scope 和 Team 混成同一个对象

当前 `Teams Home / Team Detail / Studio` 大量把 `scopeId` 当当前团队上下文。

结果是：

1. 切换 scope 就像切换 team
2. `/teams/{scopeId}` 看起来像 team detail
3. scope 下多个 team 没有显式位置

## 5.2 把 Team 和 Team Entry 混成一个对象

当前很多文档和页面会把：

1. team
2. 团队入口
3. 默认 service

说成同一件事。

这会让用户误以为：

1. team 只有在发布了入口以后才存在
2. team 本体就是某个 service

## 5.3 把 Service 冒充 Member

当前很多路径仍然围绕 `serviceId`、`scope binding`、`published services` 运转。

这会导致：

1. 用户从 Team 进 Studio，落的是服务上下文，不是成员上下文
2. `Bind / Invoke / Observe` 的深链主键不是 member

## 5.4 Team Detail 里的成员是推导出来的，不是声明出来的

当前 Team 页大量依赖：

1. workflow roles
2. actor groups
3. primary actor fallback

来“推断”成员。

这会让 roster 在没有运行信号时不稳定。

---

## 6. 正确的信息架构

## 6.1 Teams Home

Teams Home 的主语是 “当前 scope 下的 teams”。

卡片应展示：

1. team name
2. member count
3. team entry status
4. current entry target
5. health
6. recent activity
7. next action

团队卡可选主动作：

1. `查看团队`
2. `Invoke Team`
   仅当 team entry ready 时可用

## 6.2 Team Detail

Team Detail 的一等对象是：

1. Scope breadcrumb
2. Team summary
3. Team Entry
4. Members
5. Integrations
6. Runtime health
7. Recent runs

推荐 tab：

1. `Overview`
2. `Members`
3. `Bindings`
4. `Events`
5. `Topology`
6. `Assets`
7. `Advanced`

其中：

1. `Overview` 需要有 `Invoke Team`
2. `Members` 展示真实 member roster
3. `Bindings` 既能看 team entry，也能看 member publish

## 6.3 Studio

Studio 的一等主语必须是：

`selected team + selected member`

页面结构应为：

1. 左侧：`Team members`
2. 顶部：`selected scope / team / member context`
3. 中间 stepper：`Build / Bind / Invoke / Observe`
4. 主区：围绕当前 member 的生命周期工作台

补充原则：

1. Team-level invoke 存在
2. 但 Studio 主链路仍然只围绕 member

---

## 7. 目标数据模型

## 7.1 Team Summary

```text
TeamSummary
- teamId
- scopeId
- displayName
- description
- memberCount
- entryTargetMemberId?
- entryTargetDisplayName?
- entryStatus
- healthStatus
- createdAt
- updatedAt
```

## 7.2 Team Entry

```text
TeamEntryConfig
- teamId
- entryTargetMemberId
- publishedServiceId
- defaultEndpointId
- activeRevisionId
- status
- updatedAt
```

## 7.3 Team Member Summary

```text
TeamMemberSummary
- memberId
- teamId
- scopeId
- displayName
- description
- implementationKind
- implementationStatus
- publishedServiceId
- publishedServiceStatus
- isEntryTarget
- latestRunStatus
- latestRunAt
- createdAt
- updatedAt
```

## 7.4 Team Member Detail

```text
TeamMemberDetail
- summary
- workflowRef?
- scriptRef?
- gagentRef?
- publishedService
- bindingContract
- latestRuns[]
```

## 7.5 Published Service 映射

```text
PublishedMemberService
- memberId
- serviceId
- serviceKey
- primaryActorId
- endpointIds[]
- activeServingRevisionId
- deploymentStatus
```

```text
PublishedTeamEntryService
- teamId
- entryTargetMemberId
- serviceId
- defaultEndpointId
- activeServingRevisionId
- deploymentStatus
```

---

## 8. 路由与 URL 规则

## 8.1 Team Detail

`teamId` 全局唯一，因此 Team Detail 应直接使用：

`/teams/{teamId}?memberId=...`

允许补充：

1. `tab`
2. `runId`
3. `workflowId`

`scopeId` 可以作为上下文恢复信息存在，但不是 team 身份主键。

## 8.2 Studio

Studio 深链必须至少支持：

1. `teamId`
2. `memberId`
3. `step`
4. `implementationKind`
5. `focus`

推荐：

`/studio?teamId={teamId}&memberId={memberId}&step=build`

`scopeId` 可选，用于返回路径或 scope 恢复，但不作为 team 身份必需字段。

---

## 9. API 方向

## 9.1 列表接口仍然按 Scope 组织

```text
GET  /api/scopes/{scopeId}/teams
POST /api/scopes/{scopeId}/teams
```

## 9.2 Detail 接口按 Team 组织

```text
GET /api/teams/{teamId}
PUT /api/teams/{teamId}
PUT /api/teams/{teamId}/entry
POST /api/teams/{teamId}/invoke/{endpointId}:stream
GET /api/teams/{teamId}/runs
GET /api/teams/{teamId}/runs/{runId}
```

## 9.3 Member 接口按 Team 组织

```text
GET  /api/teams/{teamId}/members
POST /api/teams/{teamId}/members
GET  /api/teams/{teamId}/members/{memberId}
PUT  /api/teams/{teamId}/members/{memberId}

PUT  /api/teams/{teamId}/members/{memberId}/implementation/workflow
PUT  /api/teams/{teamId}/members/{memberId}/implementation/script
PUT  /api/teams/{teamId}/members/{memberId}/implementation/gagent

GET  /api/teams/{teamId}/members/{memberId}/binding
POST /api/teams/{teamId}/members/{memberId}/binding/revisions/{revisionId}:activate
POST /api/teams/{teamId}/members/{memberId}/invoke/{endpointId}:stream
GET  /api/teams/{teamId}/members/{memberId}/runs
GET  /api/teams/{teamId}/members/{memberId}/runs/{runId}
```

关键不是 URL 形状，而是语义：

1. Team list 按 scope
2. Team detail 按全局唯一 `teamId`
3. Team invoke 先 resolve `Team Entry`
4. Member invoke 直接 resolve member published service

---

## 10. 迁移方案

## 10.1 Phase 1: 先补 Team / Team Entry / Member 模型

目标：

1. Scope 内可以拿到真实 team roster
2. Team Detail 能拿到真实 team entry
3. Team 和 Studio 都能拿到真实 member roster

动作：

1. 新增 `TeamSummary / TeamEntryConfig / TeamMemberSummary / TeamMemberDetail`
2. 新增 `teamId -> team entry` 与 `teamId -> members[]` 映射
3. Team Detail / Studio 都切到 `teamId + memberId` 深链

## 10.2 Phase 2: Team Detail 先支持 Invoke Team

动作：

1. Team header 增加 `Invoke Team`
2. Team Overview 展示 team entry status
3. Bindings tab 区分 `Team Entry` 与 `Member Publish`

## 10.3 Phase 3: Studio 改成真正的 member-first

动作：

1. 左侧 rail 改成真实 members
2. Build mode 改成 member implementation tabs
3. Bind / Invoke / Observe 都通过 `team + member` 取上下文

## 10.4 Phase 4: Create Team 改成真正的 team-first

动作：

1. 先在当前 scope 下创建 team
2. 默认引导创建第一个 member
3. 默认把这个 member 设为初始 team entry target
4. 创建完成后进入该 member 的 Build

---

## 11. 验收标准

用户在任意页面都能清楚回答：

1. 我现在在哪个 scope
2. 我现在在哪个 team
3. 这个 team 能不能被 invoke
4. 如果能，默认会落到哪个 member
5. 我现在操作的是哪个 member
6. 这个 member 的实现方式是什么
7. 这个 member 的 published service 是什么

同时：

1. Teams Home 不再把 team 和 service 混成一个对象
2. Team Detail 支持 `Invoke Team`
3. Studio 保持 member-first，不再退回到 service-first
4. `serviceId` 不再冒充 `teamId` 或 `memberId`

---

## 12. 非目标

本期不做：

1. 一次性重写完整的 scope switcher 和权限体系
2. 一次性重写所有 `services` 页面
3. 支持一个 member 绑定多个 primary service
4. 把 workflow 内部 role 全部升级成 team member

---

## 13. 最终产品承诺

用户应该感觉到的是：

1. 我先进入某个 scope
2. 我在这个 scope 下管理很多 team
3. 每个 team 都可以有自己的团队入口
4. 每个 team 下有很多 member
5. 我可以 invoke 整个 team，也可以直接 invoke 某个 member
6. Studio 永远是在编辑某个 member，而不是在编辑一个抽象 service

这才是正确的主语链。
