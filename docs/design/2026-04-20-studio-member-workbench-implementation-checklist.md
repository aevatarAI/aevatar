---
title: "Studio Member Workbench Implementation Checklist"
status: draft
owner: tbd
last_updated: 2026-04-20
---

# Studio Member Workbench Implementation Checklist

## 0. 目标

把 [2026-04-20-studio-member-workbench-prd.md](./2026-04-20-studio-member-workbench-prd.md) 和 [2026-04-20-studio-member-workbench-information-architecture.md](./2026-04-20-studio-member-workbench-information-architecture.md) 转成一份可以直接开工的前端实施清单。

本清单的原则是：

1. 不新造后端主语。
2. 优先重组现有前端能力，而不是推倒重写。
3. 先做语义和结构收口，再做视觉 polish。
4. 让 Studio 围绕 `selected member` 连成一条主链路。

---

## 1. 本轮范围锁定

### 必须坚持

1. `Studio = Team Member Workbench`
2. 左侧列表语义统一为 `team members`
3. `Workflow / Script / GAgent` 只作为 `Build mode`
4. `Build / Bind / Invoke / Observe` 成为 Studio 的主 stepper
5. 现有 `/scopes/invoke` 与 runtime binding/runs 能力要并回 Studio 主链路
6. Team 级信息留在 `Team Detail`

### 本轮不做

1. 新后端 member catalog
2. 新 team analytics
3. query-time 拼接伪强一致 team topology
4. Studio 之外的 Team Detail 全量重写
5. 全新 design system 替换

---

## 2. 当前代码基线

当前不是从零开始，已经有四块可复用基线：

1. 主 Studio 页：
   [studio/index.tsx](../../apps/aevatar-console-web/src/pages/studio/index.tsx)
2. 脚本工作台：
   [ScriptsWorkbenchPage.tsx](../../apps/aevatar-console-web/src/modules/studio/scripts/ScriptsWorkbenchPage.tsx)
3. Invoke 实验台：
   [scopes/invoke.tsx](../../apps/aevatar-console-web/src/pages/scopes/invoke.tsx)
4. Runtime workbench：
   [ScopeServiceRuntimeWorkbench.tsx](../../apps/aevatar-console-web/src/pages/scopes/components/ScopeServiceRuntimeWorkbench.tsx)

当前最关键的问题：

1. `/studio` 仍然是工具集合页，不是 member workbench。
2. `scripts / invoke / runtime bindings` 分散在不同页面。
3. 路由层同时混入 `team / member / workflow / script / execution` 多套主语。

---

## 3. 交付阶段

## Phase 0: 命名与主语冻结

目标：

先把产品主语统一下来，避免实现中继续摇摆。

任务：

- [ ] 在 Studio 相关页面和注释里统一使用 `member` 作为主对象术语
- [ ] 标记现有 `workflow/script/service` 列表中哪些实际对应 `member`
- [ ] 明确哪些页面是 `team-first`，哪些页面是 `member-first`
- [ ] 盘点现有对外文案中的误导术语：
  - `Team Builder`
  - `Workflows` 作为 Studio 一级导航
  - `Scripts` 作为平级主页面
  - `Executions` 作为平级主页面

关键文件：

- [studio/index.tsx](../../apps/aevatar-console-web/src/pages/studio/index.tsx)
- [navigation.ts](../../apps/aevatar-console-web/src/shared/studio/navigation.ts)
- [StudioShell.tsx](../../apps/aevatar-console-web/src/pages/studio/components/StudioShell.tsx)

验收：

1. 团队级、成员级、实现级、绑定级语义在代码和文案上不再混用。
2. 新增代码不再把 `workflow` 默认当成 Studio 的主对象。

---

## Phase 1: Studio Shell 重组

目标：

把现有 Studio 顶层壳子从“工具导航”改为“member workbench 壳子”。

任务：

- [ ] 新建或重构 `StudioWorkbenchShell`
- [ ] 把当前侧边导航从：
  - `workflows / scripts / roles / connectors / execution / settings`
  改成：
  - `member rail`
  - `lifecycle stepper`
- [ ] 顶部新增固定 `context bar`
- [ ] context bar 显示：
  - team name
  - selected member
  - build mode
  - revision
  - binding
  - health
- [ ] 梳理右上动作：
  - save
  - test
  - open platform
  - share

关键文件：

- [studio/index.tsx](../../apps/aevatar-console-web/src/pages/studio/index.tsx)
- [StudioShell.tsx](../../apps/aevatar-console-web/src/pages/studio/components/StudioShell.tsx)

建议新增：

- `src/pages/studio/components/StudioWorkbenchShell.tsx`
- `src/pages/studio/components/StudioContextBar.tsx`
- `src/pages/studio/components/StudioMemberRail.tsx`
- `src/pages/studio/components/StudioLifecycleStepper.tsx`

验收：

1. 打开 `/studio` 时，用户第一眼能看到 team 和 selected member。
2. Studio 顶层不再先暴露工具分类。

---

## Phase 2: 路由与状态模型收口

目标：

让 Studio 路由优先表达 `scopeId + memberId + step`。

任务：

- [ ] 为 Studio 引入新的 route state 解析模型：
  - `scopeId`
  - `teamId`
  - `memberId`
  - `step`
  - `buildMode`
  - `runId`
- [ ] 对现有参数做兼容映射：
  - `workflow`
  - `script`
  - `execution`
  - `tab`
- [ ] 把 create-team 草稿参数从普通编辑主链路剥离：
  - `draftTeamName`
  - `initialMemberName`
  - 旧 `entryName` 兼容映射
- [ ] 为 Team Detail -> Studio 的入口统一 route builder
- [ ] 明确深链优先级：
  1. member
  2. step
  3. build mode
  4. selected run

关键文件：

- [navigation.ts](../../apps/aevatar-console-web/src/shared/studio/navigation.ts)
- [studio/index.tsx](../../apps/aevatar-console-web/src/pages/studio/index.tsx)
- [teams/detail.tsx](../../apps/aevatar-console-web/src/pages/teams/detail.tsx)

验收：

1. URL 足够表达“当前 team 里的当前 member 正处于哪个阶段”。
2. 历史参数仍可回放，但不再主导产品主语。

---

## Phase 3: Member Rail 落地

目标：

把当前“workflow/script/service 切换”统一改造成成员列表。

任务：

- [ ] 设计 `StudioMemberSummary` 前端模型
- [ ] 从当前 scope 可见数据中组装 rail 列表
- [ ] 每个 member summary 至少展示：
  - display name
  - implementation kind
  - binding status
  - health
  - revision
  - last run
- [ ] 支持 search / filter
- [ ] 支持 `New Member`
- [ ] 从 Team Detail 深链进入时自动聚焦对应 member

可复用来源：

- scope bindings
- services catalog
- studio workflow/script summaries
- recent runs

注意：

1. 不要伪造 canonical member roster。
2. member rail 可以是“当前 scope 下可操作成员摘要”的产品视图。

验收：

1. 左侧 rail 在用户心智上明确等价于 `team members`。
2. 选择 rail 项后，Build/Bind/Invoke/Observe 全部切换到该 member。

---

## Phase 4: Build Surface 收口

目标：

把现有 workflow editor 和 scripts workbench 都收进 `Build`。

任务：

- [ ] 为 `Build` 建立统一容器组件
- [ ] 顶部加入 `Workflow / Script / GAgent` mode switch
- [ ] 保留现有 workflow editor
- [ ] 把 [ScriptsWorkbenchPage.tsx](../../apps/aevatar-console-web/src/modules/studio/scripts/ScriptsWorkbenchPage.tsx) 改造成 `Build -> Script mode`
- [ ] 为 GAgent 表单模式建立最小可用壳子
- [ ] 统一右侧 `dry-run / preview` 面板
- [ ] 统一底部动作：
  - save draft
  - validate
  - continue to bind

建议新增：

- `src/pages/studio/components/build/BuildSurface.tsx`
- `src/pages/studio/components/build/BuildModeSwitch.tsx`
- `src/pages/studio/components/build/BuildDryRunRail.tsx`

验收：

1. Scripts 不再以独立页面心智存在。
2. 当前 member 的实现方式切换都发生在同一条 Build 主链路内。

---

## Phase 5: Bind Surface 收口

目标：

把当前 binding / revisions / snippets 收到同一块。

细化文档：

1. [2026-04-21-studio-workflow-bind-information-architecture.md](./2026-04-21-studio-workflow-bind-information-architecture.md)
2. [2026-04-21-studio-workflow-bind-implementation-checklist.md](./2026-04-21-studio-workflow-bind-implementation-checklist.md)

任务：

- [ ] 从 [ScopeServiceRuntimeWorkbench.tsx](../../apps/aevatar-console-web/src/pages/scopes/components/ScopeServiceRuntimeWorkbench.tsx) 提取 binding 相关区域
- [ ] 建立 Bind 主页面：
  - invoke URL
  - auth token explainer
  - binding params
  - cURL / Fetch / SDK
  - existing bindings
- [ ] 加右侧 smoke-test 区域
- [ ] 让 `activate / retire / rebind / rotate` 动作围绕 selected member

建议新增：

- `src/pages/studio/components/bind/BindSurface.tsx`
- `src/pages/studio/components/bind/BindingSnippetTabs.tsx`
- `src/pages/studio/components/bind/BindingList.tsx`
- `src/pages/studio/components/bind/BindingSmokeTest.tsx`

验收：

1. 绑定不再需要跳去 scope runtime 页面才能完成。
2. Bind 页只讲 selected member，不变成 team governance 总览。

---

## Phase 6: Invoke Surface 收口

目标：

把现有 Invoke Lab 并回 Studio。

任务：

- [ ] 从 [scopes/invoke.tsx](../../apps/aevatar-console-web/src/pages/scopes/invoke.tsx) 提取 playground、SSE、AGUI 相关能力
- [ ] 统一当前 member 的 invoke contract
- [ ] 保留 request history
- [ ] 保留 streaming response
- [ ] 支持 human input / approval
- [ ] 支持 AGUI mode switch：
  - timeline
  - trace
  - tabs
  - bubbles
  - raw
- [ ] 支持 layout switch：
  - split
  - stack
  - canvas + history

建议新增：

- `src/pages/studio/components/invoke/InvokeSurface.tsx`
- `src/pages/studio/components/invoke/InvokePlayground.tsx`
- `src/pages/studio/components/invoke/InvokeAguiPanel.tsx`
- `src/pages/studio/components/invoke/InvokeHistoryRail.tsx`

验收：

1. 用户不需要离开 Studio 才能调当前 member。
2. AGUI 事件流和调用表单处于同一工作台。

---

## Phase 7: Observe Surface 收口

目标：

把 run compare、governance、health & trust 汇总为成员观察页。

任务：

- [ ] 从现有 run / binding / audit 数据中组装 Observe 页
- [ ] 落地：
  - run compare
  - human escalation playback
  - governance snapshot
  - health & trust rail
- [ ] 对 unavailable / delayed / partial 状态显式打 provenance
- [ ] recent runs 与 selected run 切换联动

建议新增：

- `src/pages/studio/components/observe/ObserveSurface.tsx`
- `src/pages/studio/components/observe/RunComparePanel.tsx`
- `src/pages/studio/components/observe/HumanPlaybackPanel.tsx`
- `src/pages/studio/components/observe/TrustRail.tsx`

验收：

1. Observe 默认回答“这个 member 最近一次做了什么、是否可信、是否需要处理”。
2. 不引入 team-wide 虚构汇总。

---

## Phase 8: Roles / Connectors / Settings 降级

目标：

把工具型页面从主导航降到次级能力。

任务：

- [ ] `Roles` 改成 Build inspector 或 modal 可达
- [ ] `Connectors` 改成 Build / Bind 中的 picker 或 drawer
- [ ] `Settings` 保留全局入口，但从默认工作流退出
- [ ] 保留兼容入口，避免现有链接直接失效

关键文件：

- [studio/index.tsx](../../apps/aevatar-console-web/src/pages/studio/index.tsx)
- [StudioWorkbenchSections.tsx](../../apps/aevatar-console-web/src/pages/studio/components/StudioWorkbenchSections.tsx)

验收：

1. 用户不再通过这些工具页理解 Studio。
2. 这些能力仍可访问，但不抢主语。

---

## Phase 9: Team Detail 联动收口

目标：

让 Team Detail 与新 Studio 的职责边界稳定。

任务：

- [ ] Team Detail 中的 `Open Studio` 深链统一传 `scopeId + memberId`
- [ ] Team Detail 的 member 列表动作统一成：
  - open in studio
  - open in platform
  - open runs
- [ ] Team Detail 不再承载 member 级完整 build/bind/invoke/observe
- [ ] Studio 顶部 `back to team` 一律返回当前 team detail

关键文件：

- [teams/detail.tsx](../../apps/aevatar-console-web/src/pages/teams/detail.tsx)
- [teamRoutes.ts](../../apps/aevatar-console-web/src/shared/navigation/teamRoutes.ts)
- [navigation.ts](../../apps/aevatar-console-web/src/shared/studio/navigation.ts)

验收：

1. Team Detail 管 team。
2. Studio 管 selected member。

---

## 4. 建议交付顺序

推荐按下面顺序做，能保证每一步都能看见成效：

1. Phase 0
2. Phase 1
3. Phase 2
4. Phase 3
5. Phase 4
6. Phase 6
7. Phase 5
8. Phase 7
9. Phase 8
10. Phase 9

说明：

1. 先把壳子和主语改对。
2. 再把 Build 和 Invoke 两个最高频面收进来。
3. 然后再收 Bind 和 Observe。

---

## 5. 验收标准

### 5.1 用户心智

1. 用户能明确回答当前正在编辑哪个 member。
2. 用户能明确区分 team 和 member。
3. 用户不再需要自己理解 `workflow / script / execution` 三套页面关系。

### 5.2 任务路径

围绕同一个 member，以下路径可以在同一工作台完成：

1. 修改实现
2. 保存
3. 绑定
4. 发调用
5. 看 AGUI
6. 处理 human input
7. 看 compare / health / trust

### 5.3 代码结构

1. `studio/index.tsx` 不再继续膨胀成超级页。
2. `scripts/invoke/runtime` 三块能力通过 surface 组件归位。
3. 新增 surface 与 shell 可以独立测试。

---

## 6. 风险提醒

1. 当前仓库对 `member` 没有一套现成 canonical model，前端需要在不造假前提下做摘要组装。
2. `workflow/script/service/binding` 之间的映射要明确“产品视图”与“后端事实”边界。
3. 兼容期 URL 解析会比较复杂，必须先定义优先级再改。

---

## 7. 一句话实施策略

> 先把 Studio 从“工具中心”改成“member workbench 壳子”，再把 Build / Bind / Invoke / Observe 四块能力一块块并回来。
