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

## 1. 结论

当前项目真正的底层模型仍然是：

`scope -> service -> endpoint -> run`

但产品想表达的模型应该是：

`scope -> team -> team member -> implementation -> published service -> endpoint -> run`

如果继续让 `scope` 冒充 `team`，再让 `service` 冒充 `member`，用户会同时搞不清三件事：

1. 我现在在哪个 scope。
2. 我现在在哪个 team。
3. 我现在编辑的是哪个 team member。

这份 PRD 的目标，就是把这条主语链改正。

---

## 2. 核心定义

## 2.1 Scope

`Scope` 是用户或组织拥有的工作空间边界。

它回答的是：

1. 当前是谁的工作空间。
2. 当前可见哪些 team。
3. 当前的权限、凭证、目录和集成归属于谁。

本期约束：

1. 一个用户通常默认落在自己的 `scope`。
2. 用户也可以切换到某个组织 `scope`。
3. `scope` 不是 `team`，不能再继续把 `scopeId` 当 `teamId` 使用。

## 2.2 Team

`Team` 是 `scope` 下的一等协作单元。

它回答的是：

1. 这支团队叫什么。
2. 这支团队属于哪个 `scope`。
3. 这支团队有哪些成员。
4. 这支团队最近发生了什么。
5. 这支团队当前对外提供哪些能力。

最少字段：

1. `teamId`
2. `scopeId`
3. `displayName`
4. `description`
5. `status`
6. `createdAt`
7. `updatedAt`

## 2.3 Team Member

`Team Member` 是 `team` 下的一等对象。

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

## 2.4 Implementation

`Implementation` 不是成员本体，而是成员的实现方式。

当前只允许三种：

1. `workflow`
2. `script`
3. `gagent`

因此：

1. 用户先选中一个 member
2. 再决定这个 member 用哪种 implementation
3. 再进入对应的 Build surface

而不是：

1. 先切 `workflow / script / gagent` 模式
2. 再假装自己已经在编辑某个 member

## 2.5 Published Service

`Published Service` 是 member 的发布面，不是 member 本体。

它负责：

1. 对外暴露 endpoint
2. 承载 invoke contract
3. 承载 serving revision
4. 作为运行和治理的观测入口

一期约束建议：

1. 一个 member 对应一个 primary published service
2. 一个 published service 归属于一个 member

## 2.6 Runtime Actor / Run

`Actor` 和 `Run` 都是运行时对象，不是成员身份。

它们只回答：

1. 这次运行由谁执行
2. 这次运行发生了什么
3. 现在运行停在哪

不能回答：

1. 当前 scope 里有哪些 team
2. 某个 team 里有哪些 member
3. 某个 member 的产品身份是什么

因此：

1. `actorId` 不能做 `memberId`
2. `runId` 不能做成员上下文主键
3. Team Members 页不能靠 actor graph 临时拼 roster

---

## 3. 领域不变量

## 3.1 Scope 先于 Team，Team 先于 Member

正常用户链路必须是：

1. 进入某个 Scope
2. 在 Scope 下创建 Team
3. 在 Team 下新增 Member
4. 为 Member 选择实现方式
5. 为 Member 绑定发布契约
6. 调用并观察 Member

不是：

1. 直接新建 workflow 草稿
2. 再把它解释成某个 team
3. 再把某个 scope 默认等同于 team

## 3.2 Member 先于 Implementation

`workflow / script / gagent` 是 member 的实现方式，不是 member 类型目录。

因此：

1. 左侧 rail 应该列 Member
2. Build 区应该切 Implementation
3. 不能反过来

## 3.3 Workflow Role 不是 Team Member

`workflow role` 是 workflow 内部实现结构。

它可以表示：

1. role
2. agent node
3. execution responsibility

但它默认不等于 team member。

只有当用户显式创建一个 team member，并把它的 implementation 设为 workflow 时，这个 workflow 才是某个 member 的实现体。

因此：

1. Team Members 页不能把 workflow roles 直接当成员
2. Studio 左侧 rail 不能把 workflow 资产直接当成员

## 3.4 Service 是 Member 的发布面

Team / Studio 中关于调用、版本、治理、运行的所有信息，都应该围绕：

`selected scope -> selected team -> selected member -> published service -> revision -> run`

而不是直接回到：

`scope -> service catalog`

---

## 4. 当前项目哪里不对

## 4.1 当前项目把 Scope 和 Team 混成同一个对象

当前 `Teams Home / Team Detail / Studio` 大量把 `scopeId` 当成当前团队上下文。

这意味着当前产品语义变成了：

1. 切换 scope 就像切换 team
2. `/teams/{scopeId}` 看起来像 team detail
3. scope 下的多个 team 根本没有显式位置

这是顶层身份错误。

## 4.2 Studio 把 Build mode 当成 Member 切换

当前 Studio 里 `workflow / script / gagent` 是整个 Build surface 的模式切换。

这意味着当前产品语义变成了：

1. 我现在在 workflow studio
2. 我现在在 script studio
3. 我现在在 gagent studio

而不是：

1. 我现在在编辑成员 A
2. 成员 A 的实现方式是 workflow

这是主语错误。

## 4.3 Studio 左侧“Team members”其实是资产混排

当前左侧 rail 实际混入了：

1. 当前焦点
2. workflow 文件
3. script 资产
4. binding 焦点

这不是 member roster。

正确的 member roster 应该只展示稳定成员对象：

1. 成员名称
2. 实现方式
3. 发布状态
4. 最近运行状态

## 4.4 Team Detail 里的成员是推导出来的，不是声明出来的

当前 Team 页大量依赖：

1. workflow roles
2. actor groups
3. primary actor fallback

来“推断”团队成员。

这会产生两个问题：

1. 没运行时，成员列表不稳定
2. workflow 团队看起来有成员，script/gagent 团队却会显得结构很弱

Team 成员必须先声明，再观察，不应先观察，再猜成员。

## 4.5 Team 路由和 Studio 深链都还是 scope-first / service-first

当前 Team Detail 主路由本身就把 `scopeId` 放进 path，Studio 又大量围绕 `serviceId` 和 `scope binding` 运转。

这会导致：

1. 用户从 Scope 进 Team 时，没有真正的 `teamId`
2. 用户从 Team 进 Studio，落的是服务上下文，不是成员上下文
3. `Bind / Invoke / Observe` 的深链主键始终不是 member

正确做法应该是：

1. Team Detail 深链至少要带 `scopeId + teamId`
2. Team 内成员深链要带 `memberId`
3. Studio 深链也带 `scopeId + teamId + memberId`
4. `serviceId` 只作为 member 发布面的派生上下文存在

## 4.6 Create Team 入口仍然是“去 Studio 开草稿”

当前 `Create Team` 没有真正完成：

1. 在当前 scope 下创建 team
2. 添加初始 member

而是把用户直接送进 Studio 去做 draft。

这会让“创建团队”和“创建一个实现草稿”混成一件事。

正确入口应该是：

1. 先在当前 scope 下创建 team 壳
2. 进入团队后默认引导创建第一个成员
3. 再进入这个成员的 Build

---

## 5. 正确的信息架构

## 5.1 Teams Home

Teams Home 的主语是 “当前 scope 下的 teams”，不是 service。

卡片应该展示：

1. scope switcher
2. team name
3. member count
4. active members
5. health
6. recent activity
7. next action

## 5.2 Team Detail

Team Detail 的一等对象是：

1. Scope breadcrumb
2. Team summary
3. Members
4. Integrations
5. Runtime health
6. Recent runs

推荐 tab：

1. `Overview`
2. `Members`
3. `Bindings`
4. `Events`
5. `Topology`
6. `Assets`
7. `Advanced`

其中：

1. `Members` 要展示真实 member roster
2. `Bindings` 展示 member -> published service 的发布面
3. `Topology` 和 `Events` 是运行观察，不再冒充成员结构

## 5.3 Studio

Studio 的一等主语必须是：

`selected scope + selected team + selected member`

页面结构应变为：

1. 左侧：`Team members`
2. 顶部：`selected scope / team / member context`
3. 中间 stepper：`Build / Bind / Invoke / Observe`
4. 主区：围绕当前 member 的生命周期工作台

## 5.4 Build

Build 页回答：

`当前 member 用什么实现`

因此 Build 需要：

1. member header
2. implementation kind switch
3. implementation-specific editor

这里的 `workflow / script / gagent` 是当前 member 的 implementation tabs，不是整个 Studio 的全局对象模式。

## 5.5 Bind

Bind 页回答：

`当前 member 以什么 published contract 对外暴露`

它应该围绕：

1. member
2. published service
3. revision
4. endpoint
5. invoke URL

## 5.6 Invoke

Invoke 页回答：

`当前 member 的 published contract 被真实调用时发生了什么`

它应该直接继承当前 member 的绑定上下文，而不是让用户重新理解当前 service 是谁。

## 5.7 Observe

Observe 页回答：

`当前 member 的最近运行和内部执行路径是什么`

这里可以看 run、steps、human input、tool call、logs，但这些都是 member 的运行观察，不是 member 身份。

---

## 6. 目标数据模型

## 6.1 Team Summary

```text
TeamSummary
- scopeId
- teamId
- displayName
- description
- memberCount
- healthStatus
- createdAt
- updatedAt
```

## 6.2 Team Member Summary

```text
TeamMemberSummary
- memberId
- scopeId
- teamId
- displayName
- description
- implementationKind
- implementationStatus
- publishedServiceId
- publishedServiceStatus
- latestRunStatus
- latestRunAt
- createdAt
- updatedAt
```

## 6.3 Team Member Detail

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

## 6.4 Implementation Ref

```text
WorkflowImplementationRef
- workflowId
- revisionId
- definitionActorId

ScriptImplementationRef
- scriptId
- scriptRevision
- definitionActorId

GAgentImplementationRef
- actorTypeName
- revisionId?
```

## 6.5 Member -> Service 映射

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

关键约束：

1. `memberId` 是稳定主键
2. `serviceId` 是发布面主键
3. 二者必须显式映射，不能隐式等同

---

## 7. 路由与 URL 规则

## 7.1 Team Detail

当前：

`/teams/{scopeId}?serviceId=...`

目标：

`/teams/{teamId}?scopeId={scopeId}&memberId=...`

允许补充：

1. `tab`
2. `runId`
3. `workflowId`

但 Team 详情默认上下文必须先 resolve `scope + team`，然后再 resolve member，不再是 service。

## 7.2 Studio

Studio 深链必须至少支持：

1. `scopeId`
2. `teamId`
3. `memberId`
4. `step`
5. `implementationKind`
6. `focus`

推荐：

`/studio?scopeId={scopeId}&teamId={teamId}&memberId={memberId}&step=build`

`focus` 只能表达当前 member 实现内部的资产焦点，比如：

1. `workflow:{workflowId}`
2. `script:{scriptId}`

它不能代替 `memberId`。

---

## 8. API 方向

## 8.1 一期约束

一期不要求整仓库立刻把所有 team 能力都做成完整新后端。

但必须同时满足：

1. `scope` 和 `team` 语义分开
2. `team` 和 `member` 语义分开
3. 不能继续只靠 `service` 冒充 `member`

## 8.2 推荐接口

```text
GET    /api/scopes/{scopeId}/teams
POST   /api/scopes/{scopeId}/teams
GET    /api/scopes/{scopeId}/teams/{teamId}
PUT    /api/scopes/{scopeId}/teams/{teamId}

GET    /api/scopes/{scopeId}/teams/{teamId}/members
POST   /api/scopes/{scopeId}/teams/{teamId}/members
GET    /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}
PUT    /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}

PUT    /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/implementation/workflow
PUT    /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/implementation/script
PUT    /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/implementation/gagent

GET    /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/binding
POST   /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/binding/revisions/{revisionId}:activate

POST   /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/invoke/{endpointId}:stream
GET    /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/runs
GET    /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/runs/{runId}
```

这里的关键不是 endpoint 形状，而是语义：

1. 先 resolve scope
2. 再 resolve team
3. 再 resolve member
4. 再 resolve published service
5. 再调用或观察运行态

---

## 9. 迁移方案

## 9.1 Phase 1: 先补显式 Team / Member 模型

目标：

1. Scope 内可以拿到真实 team roster
2. Team 和 Studio 都能拿到真实 member roster
3. 现有 service 仍然可以继续用

动作：

1. 新增 `TeamSummary / TeamMemberSummary / TeamMemberDetail`
2. 新增 `teamId -> members[]` 与 `memberId -> publishedServiceId` 映射
3. Team Detail / Studio 都切到 `scopeId + teamId + memberId` 深链

## 9.2 Phase 2: Studio 改成真正的 member-first

动作：

1. 左侧 rail 改成真实 members
2. Build mode 改成 member implementation tabs
3. Bind / Invoke / Observe 都通过 selected scope + team + member 取上下文

## 9.3 Phase 3: Team Detail 去掉推导式成员

动作：

1. `Members` tab 改为显式成员清单
2. workflow roles 降级为 implementation detail
3. actor groups 降级为 runtime detail

## 9.4 Phase 4: Create Team 改成真正的 team-first

动作：

1. 先在当前 scope 下创建 team
2. 默认引导创建第一个 member
3. 创建完成后进入该 member 的 Build

---

## 10. 验收标准

## 10.1 用户语义

用户在任意页面都能清楚回答：

1. 我现在在哪个 scope
2. 我现在在哪个 team
3. 我现在看的是哪个 member
4. 这个 member 的实现方式是什么
5. 这个 member 的 published service 是什么

## 10.2 Studio

1. 左侧只列真实 members
2. 切 `workflow / script / gagent` 不会切掉 member 身份
3. `Bind / Invoke / Observe` 都保留 scope / team / member 主语

## 10.3 Team Detail

1. Team Members tab 不再靠 workflow roles 和 actor groups 拼 roster
2. Team Detail 深链以 `scopeId + teamId` 为主，并在 team 下选择 `memberId`
3. service 只作为 member 的发布面出现

## 10.4 Create Team

1. Create Team 至少能在当前 scope 下创建 team container
2. 后续流明确是“添加 member”
3. 不再把“进入 Studio 开一个草稿”等同于“已经创建团队”

---

## 11. 非目标

本期不做：

1. 一次性重写完整的 scope switcher 和权限体系
2. 一次性重写所有 `services` 页面
3. 支持一个 member 绑定多个 primary service
4. 把 workflow 内部 role 全部升级成 team member

---

## 12. 最终产品承诺

用户应该感觉到的是：

1. 我先进入某个 scope
2. 我在这个 scope 下创建很多 team
3. 我在某个 team 下创建很多 member
4. 每个 member 可以选择 workflow、script 或 gagent 作为实现
5. 每个 member 都有自己的 Build / Bind / Invoke / Observe 生命周期
6. service 只是这个 member 被外界调用时看到的发布面

而不是：

1. 我切了一个 scope
2. 我看到的却像是某个 team
3. 我又有一些 service
4. 我又有一些 workflow 文件
5. 我又有一些 actor
6. 系统试图告诉我这些东西合起来差不多就是 team / member

这两种心智差别很大。

当前项目的下一步，必须是把这个主语改正。
