---
title: "Studio 重构 PRD（Member-first Workbench）"
status: draft
owner: tbd
last_updated: 2026-04-22
references:
  - "./2026-04-22-team-member-first-prd.md"
---

# Studio 重构 PRD（Member-first Workbench）

## 1. 文档定位

本文是 `Studio` 的产品边界稿。

它只回答三件事：

1. `Studio` 到底编辑什么
2. `Team / Team Router / Member / Implementation / Binding` 如何分工
3. `Studio` 与 `Teams / Team Detail` 的边界在哪里

本文遵循 [2026-04-22-team-member-first-prd.md](./2026-04-22-team-member-first-prd.md) 的 canonical 模型。

---

## 2. 核心结论

`Studio` 不应该被定义成 “Team Builder”。

`Studio` 的正确主语应该是：

`当前 Team 中某一个 Member 的 Build / Bind / Invoke / Observe 工作台`

这意味着：

1. `Scope` 是外层工作空间上下文
2. `Team` 是协作边界
3. `Team Router` 是 team 自己的默认路由配置
4. `Member` 是 Studio 的直接编辑对象
5. `Workflow / Script / GAgent` 是 Member 的实现方式

一句话：

`Team owns context, team router defines default routing, Studio edits members.`

---

## 3. 基于原型的关键判断

原型真正成立的心智不是：

1. 我在编辑一个抽象的 team
2. 我在浏览一堆 workflow/service 资产

而是：

1. 我在某个 team 里选中了一个 member
2. 我要继续把这个 member 的 build / bind / invoke / observe 跑通

补充规则：

1. team 可以维护默认路由配置
2. 被路由到的仍然只是普通 member
3. Studio 不应该因此退回到 team-first

---

## 4. 当前仓库的主要偏差

## 4.1 把 Studio 当成 Team Builder

这会导致：

1. team 级对象和 member 级对象混在同一页
2. 用户不知道自己当前在改 `team`、`member`、`workflow draft` 还是 `script draft`
3. `保存 / 发布 / 绑定 / 调用 / 测试` 全都失去明确主语

## 4.2 把实现方式当成产品一级导航

当前很多设计会自然滑向：

1. Workflows
2. Scripts
3. Executions
4. Connectors
5. Settings

这是一种“工具中心”导航，不是“成员工作台”导航。

## 4.3 把 Team、Router、Member、Service 混成一层

当前很多页面状态同时携带：

1. `scopeId`
2. `teamId`
3. `memberId`
4. `workflowId`
5. `scriptId`
6. `serviceId`

但没有写清它们的职责。

---

## 5. 产品定义

## 5.1 新定义

`Studio = Team Member Workbench`

Studio 负责一个 member 的完整工作闭环：

1. `Build`
2. `Bind`
3. `Invoke`
4. `Observe`

## 5.2 Team 与 Studio 的边界

`Team Detail` 负责 team-first 视角：

1. Team overview
2. Team router
3. Members roster
4. Team topology
5. Team event stream
6. Team governance / integrations / assets

`Studio` 负责 member-first 视角：

1. 当前选中 member 的实现编辑
2. 当前 member 的 binding 配置
3. 当前 member 的调用与调试
4. 当前 member 的运行观察

## 5.3 Team Router 与 Studio 的关系

`Team Router` 的存在不会改变 Studio 的主语。

正确规则是：

1. Team router 只是 team 的一份默认路由配置
2. 被它指向的仍然是普通 member
3. Studio 里若当前 member 是默认路由目标，可展示 badge 和 deep link
4. team router 的设置面默认属于 Team Detail，不属于 Studio 主线

## 5.4 Member 的定义

对前端产品而言，一个 `Team Member` 是：

1. Team 内一个可命名、可选择、可绑定、可调用、可观察的能力单元
2. 它有且仅有一个当前主实现方式：
   `workflow / script / gagent`
3. 它可以有多个 revision、多个 binding、多个 run

---

## 6. 目标用户与核心任务

## 6.1 Builder

我要修改某个 member 的实现，然后马上验证它是不是能跑。

## 6.2 Operator

我要看到某个 member 当前绑定了什么、最近怎么运行、是否需要人工介入。

## 6.3 Team Owner

我要在团队上下文里管理多个成员，并快速找到默认路由目标或某个具体 member。

---

## 7. 信息架构

推荐主路径：

1. `Teams`
2. 进入某个 `Team Detail`
3. 选择某个 member
4. 打开 `Studio`
5. 在 Studio 内继续围绕该 member 工作

Studio 标准布局应为：

1. 顶部 Context Bar
2. 左侧 Member Rail
3. 中间 Member Workbench
4. 可选右侧 Secondary Rail

## 7.1 顶部 Context Bar

必须稳定显示：

1. 当前 Scope
2. 当前 Team
3. 当前 Member
4. 当前 Member 类型
5. 当前 revision / binding / health 摘要
6. 返回 Team 的入口

可选显示：

1. 当前 member 是否是默认路由目标
2. `Open Team Routing` / `Back to Team`

## 7.2 左侧 Member Rail

左侧列表是一组 Team Members，不是资产分类导航。

每个列表项至少展示：

1. Member Name
2. Implementation Kind
3. Binding Status
4. Health
5. Last Run
6. Revision
7. Routed badge

## 7.3 中间主工作区

主工作区统一采用四步式 stepper：

1. `Build`
2. `Bind`
3. `Invoke`
4. `Observe`

这四步不是四个孤立页面，而是同一个 member 的四个连续阶段。

---

## 8. 功能需求

## 8.1 Build

Build 页负责编辑当前 member 的实现。

共性要求：

1. 顶部选择当前 member 的实现方式：
   `Workflow / Script / GAgent`
2. 右侧保留 `preview / dry-run`
3. 保存后可直接进入 Bind

## 8.2 Bind

Bind 页负责当前 member 的直接调用契约。

必须包括：

1. 当前 member 的 `Invoke URL`
2. Auth / token 说明
3. `cURL / Fetch / SDK`
4. Binding 参数
5. Existing bindings / revisions

补充规则：

1. 这页讲的是 `member bind`
2. 不是 team 总治理页
3. 若当前 member 是默认路由目标，可显示 routed badge 和“Open Team Routing”

## 8.3 Invoke

Invoke 页负责直接调当前 member。

必须包括：

1. Playground
2. Request editor
3. Request history
4. Streaming response
5. AGUI timeline / trace / bubbles / raw
6. Human-in-the-loop 交互入口

## 8.4 Observe

Observe 页负责运行后观察当前 member。

必须包括：

1. AGUI timeline
2. Step / Tool / Thinking / Message 分类视图
3. Run summary
4. Run compare
5. Human escalation playback
6. Governance snapshot

---

## 9. 深链与路由规则

Studio deep link 必须至少能表达：

1. `teamId`
2. `memberId`
3. 可选 `step`
4. 可选 `focus`

推荐：

`/studio?teamId={teamId}&memberId={memberId}&step=build`

兼容期可以映射但不再作为主语的参数：

1. `workflowId`
2. `scriptId`
3. `tab`
4. `executionId`
5. `scopeId`

其中：

1. `teamId` 是全局唯一 team 主键
2. `scopeId` 只做上下文恢复，不再冒充 team 身份

---

## 10. 一句话准则

> Scope 提供工作空间，Team 提供协作边界，Team Router 提供默认路由配置，Member 提供 Studio 主语，Build/Bind/Invoke/Observe 提供流程，Workflow/Script/GAgent 提供实现。
