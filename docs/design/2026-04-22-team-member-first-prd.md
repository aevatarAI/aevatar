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

`scope -> team -> team member -> implementation -> published service -> endpoint -> run`

这里有三条必须写死的规则：

1. `scope` 不是 `team`
2. `team` 不是 `service`
3. 只有 `team member` 能被 invoke

`team` 最多只维护一份 `router` 配置：

1. 它只表达默认路由应该落到哪个 member
2. 它属于 team 自己的配置，不是新的成员类型
3. 被指向的仍然只是一个普通 member

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
5. 这支团队的默认路由指向谁

关键约束：

1. `teamId` 全局唯一
2. `team` 可以先存在，再逐步补成员
3. `team` 自己不是 invoke target

## 2.3 Team Router

`Team Router` 是 team 自己维护的默认路由配置。

它的语义是：

1. 当用户从 team 视角进入成员链路时，系统默认先落到哪个 member
2. 这通常用于 intake / route / dispatch / hand-off 这类入口能力
3. 它不创造新的 team invoke 语义

关键原则：

1. `routeTargetMemberId` 只是 `team -> member` 的映射
2. `router` 是 team 的配置，不是 `member` 的新类型
3. 如果产品提供 team 级快捷动作，它也只能先解析路由配置，再跳到某个 member 页面

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
4. 只有 `member` 才是 invoke target

## 2.5 Implementation

`Implementation` 不是成员本体，而是成员的实现方式。

当前只允许三种：

1. `workflow`
2. `script`
3. `gagent`

## 2.6 Published Service

`Published Service` 是 member 的发布面，不是 team 或 member 本体。

关键原则：

1. `service` 只表达调用契约
2. `serviceId` 不能再冒充 `teamId` 或 `memberId`
3. team 若配置了默认路由，最终仍然是路由到某个 member 的 published service

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
5. 为 member 绑定发布契约
6. 调用并观察 member

## 3.2 只有 Member 能被 Invoke

允许：

1. `Invoke Member`

不把下面两件事当成新的真实语义：

1. `Team` 作为独立 invoke target
2. `Team Published Service`

如果产品需要从 Team Detail 直接进入默认成员调用页，只允许这样做：

1. 从 team 详情出发
2. resolve `routeTargetMemberId`
3. 跳到该 member 的 invoke surface
4. 发起的仍然是 `member invoke`

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

`serviceId` 只能表达 member 的 published contract，不能表达：

1. team 身份
2. member 身份

---

## 4. 关键用户链路

## 4.1 从 Team 视角进入调用

用户动作：

1. 进入 Team Detail
2. 点击 `Open Default Routed Member` 或同义导航动作

系统语义：

1. resolve `teamId`
2. 读取 `routeTargetMemberId`
3. 跳到该 member 的 invoke surface
4. 发起的仍然是 member invoke

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

1. team 有上下文
2. team 可维护一份 router 配置
3. 但 Studio 主链路始终只围绕 member

---

## 5. 当前项目哪里不对

## 5.1 把 Scope 和 Team 混成同一个对象

当前 `Teams Home / Team Detail / Studio` 大量把 `scopeId` 当当前团队上下文。

## 5.2 把 Team 和 Team Router 混成同一个对象

当前很多页面会把：

1. team
2. 默认入口
3. 默认 service

说成同一件事。

这会让用户误以为：

1. team 自己可以直接被 invoke
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

---

## 6. 正确的信息架构

## 6.1 Teams Home

Teams Home 的主语是 “当前 scope 下的 teams”。

卡片应展示：

1. team name
2. member count
3. routing status
4. current route target
5. health
6. recent activity
7. next action

## 6.2 Team Detail

Team Detail 的一等对象是：

1. Scope breadcrumb
2. Team summary
3. Team router
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

1. `Overview` 展示 routing 状态和跳转动作
2. `Members` 展示真实 member roster
3. `Bindings` 讲 member publish，不再发明 team publish

## 6.3 Studio

Studio 的一等主语必须是：

`selected team + selected member`

页面结构应为：

1. 左侧：`Team members`
2. 顶部：`selected scope / team / member context`
3. 中间 stepper：`Build / Bind / Invoke / Observe`
4. 主区：围绕当前 member 的生命周期工作台

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
- routeTargetMemberId?
- routeTargetMemberDisplayName?
- routingStatus
- healthStatus
- createdAt
- updatedAt
```

## 7.2 Team Router

```text
TeamRouterConfig
- teamId
- routeTargetMemberId?
- routeTargetMemberDisplayName?
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
- isRouteTarget
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

---

## 8. 路由与 URL 规则

## 8.1 Team Detail

`teamId` 全局唯一，因此 Team Detail 应直接使用：

`/teams/{teamId}?memberId=...`

## 8.2 Studio

Studio 深链必须至少支持：

1. `teamId`
2. `memberId`
3. `step`
4. `implementationKind`
5. `focus`

推荐：

`/studio?teamId={teamId}&memberId={memberId}&step=build`

`scopeId` 可选，只用于上下文恢复。

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
PUT /api/teams/{teamId}/router
GET /api/teams/{teamId}/members
POST /api/teams/{teamId}/members
```

## 9.3 Member 接口按 Team 组织

```text
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

1. team list 按 scope
2. team detail 按全局唯一 `teamId`
3. 只有 member invoke 是真实 invoke

---

## 10. 迁移方案

## 10.1 Phase 1: 先补 Team / Router / Member 模型

目标：

1. Scope 内可以拿到真实 team roster
2. Team Detail 能拿到默认路由配置
3. Team 和 Studio 都能拿到真实 member roster

动作：

1. 新增 `TeamSummary / TeamRouterConfig / TeamMemberSummary / TeamMemberDetail`
2. 新增 `teamId -> routeTargetMemberId` 与 `teamId -> members[]` 映射
3. Team Detail / Studio 都切到 `teamId + memberId` 深链

## 10.2 Phase 2: Team Detail 先支持 Routing Overview

动作：

1. Team header 展示 routing 状态
2. Team Overview 提供 `Open Default Routed Member`
3. Bindings tab 只讲 team router mapping 与 member publish 的边界

## 10.3 Phase 3: Studio 改成真正的 member-first

动作：

1. 左侧 rail 改成真实 members
2. Build mode 改成 member implementation tabs
3. Bind / Invoke / Observe 都通过 `team + member` 取上下文

## 10.4 Phase 4: Create Team 改成真正的 team-first

动作：

1. 先在当前 scope 下创建 team
2. 默认引导创建第一个 member
3. 默认允许把该 member 设为默认路由目标
4. 创建完成后进入该 member 的 Build

---

## 11. 验收标准

用户在任意页面都能清楚回答：

1. 我现在在哪个 scope
2. 我现在在哪个 team
3. 这个 team 的默认路由指向谁
4. 我现在操作的是哪个 member
5. 这个 member 的实现方式是什么
6. 这个 member 的 published service 是什么

同时：

1. Teams Home 不再把 team 和 service 混成一个对象
2. Team Detail 不再伪装 team 自己能直接被 invoke
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
3. 每个 team 下有很多 member
4. team 可以维护一份 router 配置，把默认路由指向某个 member
5. 我真正调用的永远是 member
6. Studio 永远是在编辑某个 member，而不是在编辑一个抽象 service

这才是正确的主语链。
