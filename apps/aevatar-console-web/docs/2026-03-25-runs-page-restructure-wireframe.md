# Runs Page 重构草图

## 1. 目标

`Runs` 页当前同时承担：

- run 启动入口
- run 实时观测
- workflow 概览
- actor 快照
- human interaction 处理
- recent/preset 辅助入口

问题不是功能不够，而是这些信息被放在同一层级展示，导致：

- 首屏信息密度过高
- 卡片高度不一致
- 主次不清，用户视线在多个面板之间来回跳
- `Console` 反而被挤到页面下半部，失去主舞台地位

重构目标：

1. 让 `Run trace` 成为唯一主视图。
2. 把 run 启动、运行摘要、待处理交互收敛为左右两侧辅助面板。
3. 把大块 `ProDescriptions` 从首屏主区域移走，改成扫读式 summary。
4. 统一卡片语义与高度规则，减少“每块都像一个独立页面”的感觉。

## 2. 页面结构

### 2.1 桌面版草图

```text
+-----------------------------------------------------------------------------------------------------------+
| Runtime Run Console                                                                                       |
| Start run | Open workflows | Open actor | Open settings                                                   |
+-----------------------------------------------------------------------------------------------------------+
| STATUS STRIP                                                                                              |
| [Running] [RunId: xxx] [Elapsed: 02:14] [Workflow: human_input_manual_triage] [WS] [Pending Interaction] |
+------------------------------+------------------------------------------------------+----------------------+
| LAUNCH RAIL                  | RUN TRACE                                            | INSPECTOR            |
| 360px                        | flexible                                              | 320px                |
|                              |                                                      |                      |
| [Compose | Recent | Presets] | [Timeline | Messages | Events]                      | [Run Summary]        |
|                              |                                                      | status               |
| Prompt                       | Step group: classify_incident                         | actorId              |
| Workflow                     |   10:21:02  step.request   waiting for manual input  | commandId            |
| Transport                    |   10:21:05  assistant      "Need severity..."        | active steps         |
| Existing actorId             |                                                      |                      |
|                              | Step group: human_approval                           | [Interaction]        |
| Start / Abort                |   10:21:11  approval.required                         | approval/input form  |
|                              |                                                      |                      |
| Selected workflow mini card  | Step group: finalize                                 | [Workflow Snapshot]  |
|                              |   10:21:20  run.finished                              | group/source         |
| Recent / Presets             |                                                      | primitives           |
|                              | sticky filter + live scroll + selected row detail    |                      |
|                              |                                                      | [Actor Snapshot]     |
|                              |                                                      | updatedAt            |
|                              |                                                      | lastOutput preview   |
+------------------------------+------------------------------------------------------+----------------------+
```

### 2.2 移动版草图

```text
+----------------------------------+
| Header + Status strip            |
+----------------------------------+
| Primary action                   |
| Start run / Abort                |
+----------------------------------+
| Trace                            |
| [Timeline | Messages | Events]   |
| full height main area            |
+----------------------------------+
| Bottom sheet tabs                |
| Compose | Summary | Interaction  |
+----------------------------------+
```

移动端原则：

- `Trace` 永远优先
- `Launch rail` 和 `Inspector` 进入底部抽屉或分段页签
- 不保留三栏

## 3. 信息分层

### 3.1 第一层：始终可见

- run 状态
- runId
- elapsed
- workflow
- transport
- pending interaction

这一层用 `status strip / metric pills`，不再单独占用 `Metric HUD` 卡片。

### 3.2 第二层：主任务区

- timeline
- messages
- events

这是用户在 run 中最常看的区域，应该占据页面中心和主要高度。

### 3.3 第三层：上下文辅助

- workflow profile
- actor snapshot
- latest message preview
- recent runs
- presets

这些都不应该和 trace 抢主舞台。

### 3.4 第四层：操作性内容

- resume
- signal
- approval

这类内容只在存在 pending interaction 时高亮，否则收起。

## 4. 卡片规则

统一收敛为三类卡片。

### 4.1 Metric Pill

- 高度：`64-72`
- 内容：`label + value + status dot`
- 数量：最多 `6`
- 不出现大段描述

用于：

- status
- messages
- events
- active steps
- transport
- pending interaction

### 4.2 Summary Card

- 高度：`160-220`
- 只放 `3-5` 个关键字段
- 最多一段两行摘要
- tag 最多显示 `3` 个，剩余 `+N`

用于：

- workflow snapshot
- run summary
- actor snapshot

### 4.3 Detail Panel

- 高度：`fill`
- 允许滚动
- 承载 timeline / messages / events / forms

用于：

- trace 主面板
- compose 表单
- interaction 表单

## 5. 模块拆分草图

建议把当前 `RunsPage` 拆成下面几个稳定子模块。

```text
RunsPage
├── RunsStatusStrip
├── RunsLaunchRail
│   ├── RunsComposeForm
│   ├── RunsRecentList
│   └── RunsPresetList
├── RunsTracePane
│   ├── RunsTimelineView
│   ├── RunsMessagesView
│   └── RunsEventsView
└── RunsInspectorPane
    ├── RunsSummaryCard
    ├── RunsInteractionCard
    ├── RunsWorkflowCard
    └── RunsActorSnapshotCard
```

## 6. 现有数据到新布局的映射

### 6.1 Status Strip

直接复用现有状态数据：

- `session.status`
- `session.runId`
- `commandId`
- `elapsedLabel`
- `workflowName`
- `activeTransport`
- `hasPendingInteraction`
- `session.messages.length`
- `session.events.length`
- `session.activeSteps.size`

### 6.2 Launch Rail

直接复用现有左侧内容：

- `Compose`
- `Recent`
- `Presets`
- `selectedWorkflowRecord`

但把“workflow profile”从大 `ProDescriptions` 改成一个简洁 mini card。

### 6.3 Trace Pane

复用现有：

- `eventRows`
- `session.messages`
- `runFocus`
- `latestStepRequest`
- `waitingSignal`

建议在 `Timeline` 里按 `stepId` 分组，降低流式信息的碎片感。

### 6.4 Inspector

复用现有：

- `runSummaryRecord`
- `humanInputRecord`
- `waitingSignalRecord`
- `selectedWorkflowDetails`
- `actorSnapshotQuery.data`

但只显示精简字段，完整信息用 drawer。

## 7. 视觉细节

### 7.1 节奏

- 页面主 gap 统一 `12`
- 卡片内 gap 统一 `12`
- summary 卡片正文上下 padding 统一 `16`
- 同一区域不混用 `12 / 16 / 20 / 24`

### 7.2 文案

- 卡片标题尽量 1 到 2 个词
- 描述句最多两行
- 避免在首屏出现大段 `extra` 说明

### 7.3 标签

- `Workflow / Transport / Pending / Primitive` 这种强语义保留 tag
- `RunId / ActorId / CommandId` 改为 copyable code row，不用 tag
- 同类 tag 颜色固定，不同区域不要重复换色

### 7.4 高度

- 页面主容器继续 full-height
- 取消“底部 30vh Console”
- 改为“中间主 trace 填满剩余高度”
- 左右栏跟随主区域等高

## 8. 第一版可落地方案

不改后端、不改数据模型的前提下，可以先做一版低风险重构。

### Phase 1

- 删掉独立 `Metric HUD` 卡片，改为 `Status Strip`
- 把底部 `Console` 提升为中间主区域
- 把 `Live overview + Workflow profile` 改成右侧 `Inspector`
- 维持 `Compose / Recent / Presets` 左侧 rail 不变

### Phase 2

- 为 `Timeline` 增加 `step group`
- 为 `Messages / Events` 增加选中态与 detail drawer
- 为 `Inspector` 增加折叠区块

### Phase 3

- 根据 trace 类型增加视觉层次
- `assistant`、`step.request`、`approval.required`、`run.finished` 用不同密度的 row 模板

## 9. 组件实现建议

优先新增组件，而不是继续把逻辑堆回 `runs/index.tsx`：

- `src/pages/runs/components/RunsStatusStrip.tsx`
- `src/pages/runs/components/RunsLaunchRail.tsx`
- `src/pages/runs/components/RunsTracePane.tsx`
- `src/pages/runs/components/RunsInspectorPane.tsx`
- `src/pages/runs/components/RunsSummaryCard.tsx`
- `src/pages/runs/components/RunsInteractionCard.tsx`
- `src/pages/runs/components/RunsWorkflowCard.tsx`
- `src/pages/runs/components/RunsActorSnapshotCard.tsx`

样式建议新增到：

- `src/pages/runs/runsWorkbenchLayout.ts`

避免继续把布局 token 和字段列定义混在 `runWorkbenchConfig.tsx`。

## 10. 评审结论

这版草图的核心不是“做得更炫”，而是把 `Runs` 页从：

- 多个同权卡片并列的信息工作台

改成：

- 以 `Run trace` 为中心，左右辅助的运行控制台

这更符合 Aevatar 当前产品语义：

- 左边启动
- 中间观察执行
- 右边处理上下文和人工交互

而不是让用户在首屏同时阅读五六块大卡片。
