---
title: "Studio 信息架构（Member-first Workbench）"
status: draft
owner: tbd
last_updated: 2026-04-20
---

# Studio 信息架构（Member-first Workbench）

## 0. 文档目的

本文是 [2026-04-20-studio-member-workbench-prd.md](./2026-04-20-studio-member-workbench-prd.md) 的信息架构与页面结构展开稿。

目标不是再解释一次 PRD，而是把下面三件事定死：

1. `Studio` 到底在编辑什么。
2. `Team / Member / Implementation / Binding / Run` 的层级关系是什么。
3. 页面、区域、导航、深链应该如何表达这套关系。

---

## 1. 核心对象模型

### 1.1 Team

`Team` 是 Studio 的上下文，不是 Studio 的直接编辑对象。

Team 在 UI 里负责表达：

1. 当前归属 scope
2. 当前协作边界
3. 当前 members 的集合
4. 返回 Team Detail 的上级导航

### 1.2 Member

`Member` 是 Studio 的一等主对象。

Studio 内所有主流程都必须围绕当前选中的 member 展开：

1. Build 这个 member
2. Bind 这个 member
3. Invoke 这个 member
4. Observe 这个 member

### 1.3 Implementation

`Implementation` 是 member 的实现方式，不是顶级页面。

实现方式有三种：

1. `Workflow`
2. `Script`
3. `GAgent`

它们只应该出现在 `Build` 阶段的 mode switch 中。

### 1.4 Binding

`Binding` 是 member 对外暴露与接入运行面的配置层。

在 UI 里负责表达：

1. 当前 revision 是否已经对外 serving
2. invoke URL
3. env / scope / rate / auth / streaming
4. 已有 binding 列表

### 1.5 Run

`Run` 是 member 被调用后的运行事实。

在 UI 里负责表达：

1. AGUI 事件流
2. 当前执行状态
3. human-in-the-loop
4. run compare
5. recent runs

---

## 2. 页面层级

### 2.1 全局层级

推荐的信息层级如下：

1. `Teams`
2. `Team Detail`
3. `Studio`
4. `Platform deep links`

对应语义：

1. `Teams`
   看当前有哪些 team，或者当前 team 的总入口。
2. `Team Detail`
   看这个 team 的整体状态、成员、拓扑、事件流。
3. `Studio`
   选一个 member 继续做 build/bind/invoke/observe。
4. `Platform`
   深挖 service / governance / deployment / runtime。

### 2.2 Studio 不是 Team Detail 的替代品

Studio 与 Team Detail 必须是并列但分责明确的两层：

1. `Team Detail`
   team-first
2. `Studio`
   member-first

Studio 不应该承接：

1. team overview
2. team topology 总览
3. team governance 总览
4. team-wide runtime dashboard

---

## 3. Studio 的页面骨架

Studio 页面推荐使用四段式结构：

1. Top Context Bar
2. Member Rail
3. Main Workbench
4. Secondary Rail

### 3.1 Top Context Bar

顶部条始终显示“我在哪个 team、正操作哪个 member”。

必须包含：

1. 返回 Team Detail
2. Team 名称
3. Member 名称
4. Member 类型
5. 当前 step
6. 关键状态摘要：
   `revision / binding / health / last run`
7. 右侧主动作：
   `save / test / share / open platform`

推荐文案结构：

1. 第一行：
   `Team / Member`
2. 第二行：
   `build mode / revision / binding / runtime health`

### 3.2 Member Rail

左侧 rail 的唯一职责是管理 member 上下文。

它不是资源库，不是导航中心，也不是 workflow 列表。

每个列表项展示：

1. Member display name
2. Implementation kind
3. Binding status
4. Health dot
5. Revision
6. Last run

Rail 顶部功能：

1. Search
2. Filter
3. New Member

Rail 底部可展示：

1. 当前 team scope
2. observation provenance

### 3.3 Main Workbench

主工作区永远围绕当前选中的 member。

其顶部使用 stepper：

1. `Build`
2. `Bind`
3. `Invoke`
4. `Observe`

stepper 的意义不是“页面分类”，而是“member 生命周期阶段”。

### 3.4 Secondary Rail

右侧辅助区域按当前 step 变化。

推荐职责：

1. Build
   `Dry-run / Preview / Inspector`
2. Bind
   `Smoke-test / current contract / auth helper`
3. Invoke
   `Request history / run summary / raw payload`
4. Observe
   `Run detail / governance snapshot / trust rail`

---

## 4. 四个主阶段的页面结构

## 4.1 Build

### 页面目标

编辑当前 member 的实现。

### 页面结构

1. 顶部：
   Build mode switch
2. 中心：
   当前 mode 的主编辑器
3. 右侧：
   dry-run / preview / inspector
4. 底部：
   save / continue to bind

### Build 下的 mode switch

Build 顶部必须先问：

1. 这个 member 用哪种实现方式？

可选项：

1. `Workflow`
2. `Script`
3. `GAgent`

### Workflow mode

页面结构：

1. Canvas
2. Node inspector
3. Dry-run panel

节点语义：

1. 当前 member 内部步骤
2. 该 member 编排的下游 member
3. 外部系统
4. human 节点

### Script mode

页面结构：

1. Code editor
2. Diagnostics
3. Package / file tree
4. Dry-run output

### GAgent mode

页面结构：

1. Type selector
2. Prompt / tools / persistence form
3. Preview

## 4.2 Bind

### 页面目标

把当前 member 变成可调用、可投放、可治理的接口。

### 页面结构

1. 当前 Invoke URL 卡片
2. Binding 参数表单
3. cURL / Fetch / SDK 示例
4. Existing bindings 列表
5. 右侧 smoke-test

### 必须强调的事实

1. 这是“当前 member 的 binding”。
2. 不是 team 总治理页。
3. 不是 service catalog 总览。

## 4.3 Invoke

### 页面目标

立即调当前 member，并看到运行中反馈。

### 页面结构

推荐三栏或二栏：

1. Playground
2. AGUI Panel
3. History / run summary

支持布局切换：

1. split
2. stacked
3. canvas + history

### 必须包含

1. request editor
2. send button
3. request history
4. streaming chunks
5. AGUI timeline / tabs / trace / bubbles / raw
6. human approval / input response

## 4.4 Observe

### 页面目标

看当前 member 最近一次或某一次 run 的可追踪事实。

### 页面结构

1. run compare
2. human escalation playback
3. governance snapshot
4. health & trust rail
5. unavailable / delayed honest states

### Observe 的主语限制

Observe 默认只讲：

1. 当前 member
2. 当前 selected run
3. 当前 member 的 bindings / revisions / trust

不是 team-wide 运营总览。

---

## 5. 导航规则

### 5.1 Studio 内只保留两类导航

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

### 5.2 资产型内容的处理

资产型内容应改成次级入口：

1. Roles
   Build inspector / modal / drawer
2. Connectors
   Build / Bind 的 context picker
3. Settings
   全局设置入口

### 5.3 Platform deep links

Studio 中保留少量深链动作：

1. Open Service
2. Open Governance
3. Open Deployment
4. Open Runtime Trace

这些都是次级动作，不抢 Studio 主链路。

---

## 6. 深链与路由语义

### 6.1 路由应承载的最低语义

Studio 路由最低应稳定表达：

1. `scopeId`
2. `memberId`
3. `step`
4. 可选 `buildMode`

### 6.2 兼容期允许保留的历史参数

兼容期可以映射但不应继续作为产品主语：

1. `workflowId`
2. `scriptId`
3. `tab`
4. `executionId`

### 6.3 推荐的内部状态解析顺序

1. 先解析 Team 上下文
2. 再解析 selected member
3. 再解析当前 lifecycle step
4. 最后解析 Build mode / selected run

---

## 7. 页面标题与文案规范

### 7.1 页面标题

推荐标题结构：

1. `Team Name`
2. `Member Name`
3. `Current Step`

例如：

1. `Support Ops / Support Triage Router / Build`
2. `Support Ops / Risk Review / Bind`
3. `Support Ops / Escalation Decider / Observe`

### 7.2 主语规则

所有主文案都应该满足：

1. 先说当前 member
2. 再说当前阶段
3. 最后再说实现细节

避免：

1. 直接以 workflowId 为标题
2. 直接以 script draft 为标题
3. 用 `Studio` 作为页面主标题

### 7.3 状态规则

所有运行与观察状态必须诚实表达：

1. `live`
2. `delayed`
3. `partial`
4. `seeded`
5. `unavailable`

---

## 8. 从当前代码到目标结构的映射

### 8.1 当前结构

当前页面职责大致分散如下：

1. [studio/index.tsx](../../apps/aevatar-console-web/src/pages/studio/index.tsx)
   承载 workflows/editor/scripts/roles/connectors/settings/execution
2. [ScriptsWorkbenchPage.tsx](../../apps/aevatar-console-web/src/modules/studio/scripts/ScriptsWorkbenchPage.tsx)
   承载脚本编辑完整链路
3. [scopes/invoke.tsx](../../apps/aevatar-console-web/src/pages/scopes/invoke.tsx)
   承载 invoke + stream + AGUI
4. [ScopeServiceRuntimeWorkbench.tsx](../../apps/aevatar-console-web/src/pages/scopes/components/ScopeServiceRuntimeWorkbench.tsx)
   承载 bindings / revisions / runs

### 8.2 目标结构

目标结构应收拢为：

1. Studio Shell
   `context bar + member rail + stepper`
2. Build Surface
   `workflow/script/gagent`
3. Bind Surface
   `binding + snippets + smoke-test`
4. Invoke Surface
   `playground + AGUI + history`
5. Observe Surface
   `run compare + governance + trust`

---

## 9. 不应再出现的结构误区

1. 左侧列表用 workflow/script/service 混合定义而不声明这是 member。
2. Build / Bind / Invoke / Observe 分散到三个不同页面。
3. Scripts 成为独立产品而不是 Build mode。
4. Executions 成为独立导航而不是 Observe / Invoke 的延伸。
5. Team 级问题在 Studio 里解决。
6. Member 级问题在 Team Detail 里强行堆叠。

---

## 10. 一句话架构准则

> Team 提供上下文，Member 提供主语，Build/Bind/Invoke/Observe 提供流程，Workflow/Script/GAgent 提供实现。

