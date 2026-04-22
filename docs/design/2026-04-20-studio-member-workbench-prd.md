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
2. `Team / Team Entry / Member / Implementation / Binding` 的层级如何分工
3. `Studio` 与 `Teams / Team Detail` 的边界在哪里

本文遵循 [2026-04-22-team-member-first-prd.md](./2026-04-22-team-member-first-prd.md) 的 canonical 模型。

---

## 2. 核心结论

`Studio` 不应该被定义成 “Team Builder”。

`Studio` 的正确主语应该是：

`当前 Team 中某一个 Member 的 Build / Bind / Invoke / Observe 工作台`

这意味着：

1. `Scope` 是外层工作空间上下文
2. `Team` 是协作边界与团队级调用对象
3. `Team Entry` 是 team-level invoke 的前门
4. `Member` 是 Studio 的直接编辑对象
5. `Workflow / Script / GAgent` 是 Member 的实现方式

一句话：

`Team can be invoked, Studio edits members.`

---

## 3. 基于原型的关键判断

从 `aevatar-console` 原型里能读出的真实心智不是：

1. 我在编辑一个抽象的 team
2. 我在浏览一堆 workflow/service 资产

而是：

1. 我在某个 team 里选中了一个 member
2. 我要继续把这个 member 的 build / bind / invoke / observe 跑通

因此原型真正成立的结构应解释为：

1. 顶部先给出 `scope / team / current member` 上下文
2. 左侧是 `team members`
3. 中间是当前 member 的生命周期工作台
4. team-level invoke 通过 `Team Entry` 存在，但不改变 Studio 的 member-first 本质

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

## 4.3 把 Team、Team Entry、Member、Service 混成一层

当前很多页面状态同时携带：

1. `scopeId`
2. `teamId`
3. `memberId`
4. `workflowId`
5. `scriptId`
6. `serviceId`

但没有写清它们的职责。

正确拆分应该是：

1. `Team`
   负责归属和团队级调用
2. `Team Entry`
   负责 team invoke 的入口解析
3. `Member`
   负责能力本体和运行责任
4. `Implementation`
   负责具体实现形态
5. `Published Service`
   负责对外暴露

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
2. Team entry status
3. Members roster
4. Team topology
5. Team event stream
6. Team governance / integrations / assets
7. `Invoke Team`

`Studio` 负责 member-first 视角：

1. 当前选中 member 的实现编辑
2. 当前 member 的 bind 配置
3. 当前 member 的调用与调试
4. 当前 member 的运行观察

## 5.3 Team Entry 与 Studio 的关系

`Team` 可被 invoke，但不意味着 `Studio` 要退回到 team-first。

正确规则是：

1. `Invoke Team`
   放在 Team Detail 或 Studio 顶部上下文的次级动作里
2. `Build / Bind / Invoke / Observe`
   仍然围绕当前 member
3. 若当前 member 是 `team entry target`
   Studio 可展示 badge、状态和 deep link
4. Team Entry 的设置面
   默认放在 Team Detail / Team Bindings，而不是 member Bind 的主线

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

我要在团队上下文里管理多个成员，并在需要时：

1. 直接 `Invoke Team`
2. 或深入某个 member 继续编辑

---

## 7. 信息架构

## 7.1 全局路径

推荐主路径：

1. `Teams`
2. 进入某个 `Team Detail`
3. 选择 `Invoke Team` 或选中某个 member
4. 打开 `Studio`
5. 在 Studio 内继续围绕该 member 工作

## 7.2 Studio 标准布局

Studio 应采用三段式结构：

1. 顶部 Context Bar
2. 左侧 Member Rail
3. 中间 Member Workbench

右侧可选 Secondary Rail：

1. Inspector
2. Dry-run
3. Binding detail
4. Run detail

## 7.3 顶部 Context Bar

必须稳定显示：

1. 当前 Scope
2. 当前 Team 名称
3. 当前 Member 名称
4. 当前 Member 类型
5. 当前 revision / binding / health 摘要
6. 返回 Team 的入口

可选显示：

1. 当前 member 是否是 `team entry target`
2. `Invoke Team` 次级动作

## 7.4 左侧 Member Rail

左侧列表是一组 Team Members，不是资产分类导航。

每个列表项至少展示：

1. Member Name
2. Implementation Kind
3. Binding Status
4. Health
5. Last Run
6. Revision

支持：

1. Search / Filter
2. New Member
3. 切换当前工作台主体

## 7.5 中间主工作区

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

1. 顶部先选择当前 member 的实现方式：
   `Workflow / Script / GAgent`
2. 右侧保留 `preview / dry-run` 区域
3. 保存后可直接进入 Bind

## 8.2 Bind

Bind 页负责当前 member 的直接调用契约。

必须包括：

1. 当前 member 的 `Invoke URL`
2. Copy
3. Auth / token 说明
4. `cURL / Fetch / SDK` 示例
5. Binding 参数表单
6. 已有 binding / revisions

补充规则：

1. 这页讲的是 `member bind`
2. 不是 team 总治理页
3. 如果当前 member 是 team entry target，可显示 badge 和“Open Team Entry Settings”

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

## 8.5 Shared Requirements

所有步骤必须共享：

1. 当前 team
2. 当前 member
3. 当前 revision
4. 当前 binding
5. 当前 selected run

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

## 10. 当前能力的重组建议

当前前端能力可大致重组为：

1. `pages/studio/index.tsx`
   主要复用为 `Build`
2. `modules/studio/scripts/ScriptsWorkbenchPage.tsx`
   下沉为 `Build -> Script`
3. `pages/scopes/invoke.tsx`
   主要复用为 `Invoke`
4. `ScopeServiceRuntimeWorkbench`
   主要复用为 `Bind` 与 `Observe` 的 runtime 数据面

要降级的一级导航：

1. `Workflows`
2. `Scripts`
3. `Executions`
4. `Roles`
5. `Connectors`
6. `Settings`

它们应改成：

1. Member rail + Build mode
2. Inspector / drawer / modal
3. Team Detail 的辅助入口

---

## 11. 一句话准则

> Scope 提供外层工作空间，Team 提供协作边界与团队入口，Member 提供 Studio 主语，Build/Bind/Invoke/Observe 提供流程，Workflow/Script/GAgent 提供实现。
