---
title: "Studio 信息架构（Member-first Workbench）"
status: draft
owner: tbd
last_updated: 2026-04-22
references:
  - "./2026-04-22-team-member-first-prd.md"
  - "./2026-04-20-studio-member-workbench-prd.md"
---

# Studio 信息架构（Member-first Workbench）

## 0. 文档目的

本文是 `Studio` 的页面结构展开稿。

它只回答三件事：

1. `Studio` 到底在编辑什么
2. 页面、区域、导航、深链如何表达 `scope / team / member`
3. team-level invoke 与 member-level workbench 如何共存

---

## 1. 核心对象模型

## 1.1 Scope

`Scope` 是外层工作空间上下文。

在 UI 里负责表达：

1. 当前归属谁
2. 当前从哪个 scope 进入
3. 返回 Teams Home 时落在哪个 scope

## 1.2 Team

`Team` 是 Studio 的本地上下文，不是 Studio 的直接编辑对象。

在 UI 里负责表达：

1. 当前协作边界
2. 当前 members 的集合
3. 返回 Team Detail 的上级导航

## 1.3 Team Entry

`Team Entry` 是 team-level invoke 的前门。

在 Studio 里不作为主工作区对象，但需要被看见：

1. 当前 member 是否是 team entry target
2. 是否可以从这里快速回到 Team Entry 设置
3. 是否可以直接 `Invoke Team`

## 1.4 Member

`Member` 是 Studio 的一等主对象。

Studio 内所有主流程都围绕当前选中的 member 展开：

1. Build 这个 member
2. Bind 这个 member
3. Invoke 这个 member
4. Observe 这个 member

## 1.5 Implementation

`Implementation` 是 member 的实现方式，不是顶级页面。

实现方式有三种：

1. `Workflow`
2. `Script`
3. `GAgent`

它们只应该出现在 `Build` 阶段的 mode switch 中。

## 1.6 Binding

`Binding` 是 member 对外暴露与接入运行面的配置层。

在 UI 里负责表达：

1. invoke URL
2. revision
3. auth / env / streaming
4. current contract

## 1.7 Run

`Run` 是 member 被调用后的运行事实。

在 UI 里负责表达：

1. AGUI event stream
2. current execution state
3. human-in-the-loop
4. recent runs
5. run compare

---

## 2. 页面层级

推荐信息层级如下：

1. `Teams`
2. `Team Detail`
3. `Studio`
4. `Platform deep links`

对应语义：

1. `Teams`
   看当前 scope 下有哪些 team
2. `Team Detail`
   看这个 team 的整体状态、team entry、members、拓扑、事件流
3. `Studio`
   选一个 member 继续做 build / bind / invoke / observe
4. `Platform`
   深挖 service / governance / deployment / runtime

关键规则：

1. Team-level invoke 属于 `Teams / Team Detail`
2. Member-level lifecycle 属于 `Studio`

---

## 3. Studio 的页面骨架

Studio 页面推荐使用四段式结构：

1. Top Context Bar
2. Member Rail
3. Main Workbench
4. Secondary Rail

## 3.1 Top Context Bar

顶部条始终显示：

1. 当前 Scope
2. 当前 Team
3. 当前 Member
4. 当前 Step
5. 当前 revision / binding / health / last run

右侧动作建议：

1. `Save`
2. `Invoke Member`
3. `Invoke Team`
4. `Open Team Entry Settings`
5. `Back to Team`

其中：

1. `Invoke Team` 是 team-level 动作
2. `Invoke Member` 是当前 member 动作
3. 当前 member 若是 `team entry target`，需要明显 badge

## 3.2 Member Rail

左侧 rail 的唯一职责是管理 member 上下文。

它不是资源库，不是 workflow 列表，也不是 service 列表。

每个列表项展示：

1. Member display name
2. Implementation kind
3. Binding status
4. Health dot
5. Revision
6. Last run
7. Entry target badge

Rail 顶部功能：

1. Search
2. Filter
3. New Member

Rail 底部可展示：

1. Scope badge
2. Team badge
3. Observation provenance

## 3.3 Main Workbench

主工作区永远围绕当前选中的 member。

其顶部使用 stepper：

1. `Build`
2. `Bind`
3. `Invoke`
4. `Observe`

stepper 的意义不是页面分类，而是 `member lifecycle stage`。

## 3.4 Secondary Rail

右侧辅助区域按当前 step 变化。

推荐职责：

1. Build
   `Dry-run / Preview / Inspector`
2. Bind
   `Smoke-test / current contract / team entry status`
3. Invoke
   `Request history / run summary / raw payload`
4. Observe
   `Run detail / governance snapshot / trust rail`

---

## 4. 四个主阶段

## 4.1 Build

页面目标：

编辑当前 member 的实现。

页面结构：

1. 顶部：Build mode switch
2. 中心：当前实现方式的主编辑器
3. 右侧：dry-run / preview / inspector
4. 底部：save / continue to bind

Build 顶部必须先问：

1. 这个 member 用哪种实现方式？

可选项：

1. `Workflow`
2. `Script`
3. `GAgent`

## 4.2 Bind

页面目标：

把当前 member 变成可直接调用、可投放、可治理的接口。

页面结构：

1. Member invoke URL
2. Binding parameters
3. cURL / Fetch / SDK
4. Existing bindings
5. Revisions
6. 右侧 smoke-test

额外状态：

1. 当前 member 是否是 `team entry target`
2. 若是，显示 team invoke 的次级说明或 deep link

## 4.3 Invoke

页面目标：

立即调当前 member，并看到运行中反馈。

页面结构：

1. Playground
2. AGUI panel
3. History / run summary

必须包含：

1. request editor
2. send button
3. request history
4. streaming chunks
5. AGUI timeline / tabs / trace / bubbles / raw
6. human approval / input response

## 4.4 Observe

页面目标：

看当前 member 某次 run 的可追踪事实。

页面结构：

1. run compare
2. human escalation playback
3. governance snapshot
4. health & trust rail
5. honest availability states

Observe 默认只讲：

1. 当前 member
2. 当前 selected run
3. 当前 member 的 bindings / revisions / trust

不是 team-wide 运营总览。

---

## 5. 导航规则

Studio 内只需要两层导航：

1. 左侧 `member selection`
2. 顶部 `lifecycle stepper`

不应继续保留：

1. `Workflows`
2. `Scripts`
3. `Roles`
4. `Connectors`
5. `Executions`
6. `Settings`

作为同层主导航。

资产型内容应改成次级入口：

1. Roles
   Build inspector / modal / drawer
2. Connectors
   Build / Bind 的 context picker
3. Settings
   全局设置入口

---

## 6. 深链与路由语义

Studio 路由最低应稳定表达：

1. `teamId`
2. `memberId`
3. `step`
4. 可选 `buildMode`

推荐：

`/studio?teamId={teamId}&memberId={memberId}&step=build`

兼容期可以映射但不应继续作为产品主语：

1. `scopeId`
2. `workflowId`
3. `scriptId`
4. `tab`
5. `executionId`

推荐的内部状态解析顺序：

1. 先解析 Scope 上下文
2. 再解析 selected team
3. 再解析 selected member
4. 再解析当前 lifecycle step
5. 最后解析 Build mode / selected run

---

## 7. 页面标题与文案规范

推荐标题结构：

1. `Scope`
2. `Team`
3. `Member`
4. `Current Step`

例如：

1. `runtime-ops / Support Ops / Support Triage Router / Build`
2. `runtime-ops / Support Ops / Risk Review / Bind`

主语规则：

1. 先说当前 member
2. 再说当前阶段
3. 最后再说实现细节

避免：

1. 直接以 workflowId 为标题
2. 直接以 script draft 为标题
3. 用 `Studio` 作为页面主标题

---

## 8. 一句话准则

> Scope 提供工作空间，Team 提供协作边界与 team invoke，Team Entry 负责入口解析，Member 提供 Studio 主语，Build/Bind/Invoke/Observe 提供流程，Workflow/Script/GAgent 提供实现。
