# 2026-04-15 Aevatar Console Web Implementation Checklist

## 0. 目标

把 [2026-04-15-aevatar-console-web-redesign-prd.md](./2026-04-15-aevatar-console-web-redesign-prd.md) 转成一份可以直接开工的前端实施清单。

本清单的原则是：

- 不改后端 contract
- 不伪造 team catalog / team KPI / canonical member roster
- 继续复用已存在的 Team-first 骨架
- 优先做结构收口，再做视觉 polish

---

## 1. 本轮范围锁定

### 必须坚持

- `/teams` 采用 `current-team-first`
- `Team Detail` 是一级主工作台
- `Chat` 是上下文动作，不回到一级导航
- `Studio` 作为高级编辑入口，必须带显式 `scopeId`
- `成员` 在实现语义上按 `participants` 处理
- `连接器` 在信息结构上扩为 `Bindings / Connections & Policies`

### 本轮不做

- 真实多团队运营首页
- fake KPI strip
- 人造“在线率 / 团队数 / 全局消息量”
- 新后端接口
- query-time replay
- Team 页面自建 shadow truth

---

## 2. 当前代码基线

当前仓库不是从零开始，已经具备一批可复用基础：

- `/teams` 默认首页已经建立，[config/routes.ts](../../apps/aevatar-console-web/config/routes.ts)
- 导航已经是 `Teams / Platform / Settings`，[src/shared/navigation/navigationGroups.tsx](../../apps/aevatar-console-web/src/shared/navigation/navigationGroups.tsx)
- Team-first 开关已存在，[src/shared/config/consoleFeatures.ts](../../apps/aevatar-console-web/src/shared/config/consoleFeatures.ts)
- team runtime lens 已存在，[src/pages/teams/runtime/useTeamRuntimeLens.ts](../../apps/aevatar-console-web/src/pages/teams/runtime/useTeamRuntimeLens.ts)
- 团队详情页已存在，但体量很大，[src/pages/teams/detail.tsx](../../apps/aevatar-console-web/src/pages/teams/detail.tsx)
- Platform 页已存在：
  - [src/pages/services/index.tsx](../../apps/aevatar-console-web/src/pages/services/index.tsx)
  - [src/pages/governance/index.tsx](../../apps/aevatar-console-web/src/pages/governance/index.tsx)
  - [src/pages/Deployments/index.tsx](../../apps/aevatar-console-web/src/pages/Deployments/index.tsx)
  - [src/pages/actors/index.tsx](../../apps/aevatar-console-web/src/pages/actors/index.tsx)

当前最需要收口的 2 个现实问题：

1. `/teams` 仍然挂在 `./scopes/overview`，还没有完全切到 `teams` 页面实现。
2. `src/pages/teams/detail.tsx` 已经承载过多责任，继续叠需求会显著增加维护风险。

---

## 3. 交付阶段

## Phase 0: 范围冻结与命名统一

目标：

- 把 PRD 的核心取舍真正冻结成开发边界，避免实现过程中又回到“大而全首页”

任务：

- [x] 在 `teams` 相关页面和注释里统一 4 个产品判断：
  - `current-team-first`
  - `participants semantics`
  - `bindings over connectors`
  - `platform deep-link`
- [x] 盘点仍然对外可见的工程术语，形成替换清单：
  - `GAgents`
  - `Primitives`
  - `Mission Control`
  - `Invoke`
- [x] 标记所有当前依赖 fake summary 的 UI 文案，避免继续扩散

当前结论：

- `Teams` 主路径已收口为 `Teams / Studio / Services / Governance / Deployments / Runtime Explorer`
- `Teams` 页面不再把 `GAgents / Primitives / Mission Control / Invoke` 作为用户主语暴露
- `/teams` 首页的 summary strip 已改成 `当前 Scope / 当前可见团队 / 可见运行信号 / 草稿条目`
- 已移除 `活跃团队 / 运行中成员 / 健康团队率` 这类容易被误解为全局 KPI 的文案

验收：

- 团队层页面不再新增工程术语主语
- 代码评审时不再对 `/teams` 提“未来可做多团队总览”式隐含承诺

---

## Phase 1: `/teams` 首页迁移

目标：

- 把 `/teams` 从 `scopes/overview` 的过渡实现迁移到真正的 `teams` 页面入口

关键文件：

- [config/routes.ts](../../apps/aevatar-console-web/config/routes.ts)
- [src/pages/teams/index.tsx](../../apps/aevatar-console-web/src/pages/teams/index.tsx)
- [src/pages/scopes/overview.tsx](../../apps/aevatar-console-web/src/pages/scopes/overview.tsx)
- [src/pages/teams/TeamsHomeRosterV0.tsx](../../apps/aevatar-console-web/src/pages/teams/TeamsHomeRosterV0.tsx)
- [src/pages/teams/LegacyTeamsHome.tsx](../../apps/aevatar-console-web/src/pages/teams/LegacyTeamsHome.tsx)

任务：

- [x] 把 `/teams` route 组件从 `./scopes/overview` 切到 `./teams`
- [x] 保留 `/scopes/overview -> /teams` 的过渡跳转
- [x] 让 `teams/index.tsx` 成为唯一的首页入口壳层
- [x] 首页明确写清 `current session team only`
- [x] 只有在确有 scope 来源时，才展示 `Recent Teams / Available Teams`
- [x] 清理 `scopes/overview.tsx` 中仅为首页叙事存在的逻辑

验收：

- `/teams` 不再依赖 `scopes/overview` 承担主叙事
- 首页没有 fake KPI、没有多团队排名幻觉
- 无当前 scope 时，页面显示诚实 empty/blocking state

依赖：

- 无后端依赖

---

## Phase 2: Team Detail 拆分与减重

目标：

- 把超大的 `detail.tsx` 拆成可持续维护的 Team Workbench 结构

关键文件：

- [src/pages/teams/detail.tsx](../../apps/aevatar-console-web/src/pages/teams/detail.tsx)
- [src/pages/teams/runtime/teamRuntimeLens.ts](../../apps/aevatar-console-web/src/pages/teams/runtime/teamRuntimeLens.ts)
- [src/pages/teams/runtime/useTeamRuntimeLens.ts](../../apps/aevatar-console-web/src/pages/teams/runtime/useTeamRuntimeLens.ts)
- [src/shared/navigation/teamRoutes.ts](../../apps/aevatar-console-web/src/shared/navigation/teamRoutes.ts)

建议新增目录：

- `src/pages/teams/components/`
- `src/pages/teams/tabs/`

任务：

- [x] 从 `detail.tsx` 拆出固定壳层：
  - `TeamHeader`
  - `TeamTabBar`
  - `TeamActionRail`
- [x] 拆出独立 tab 组件：
  - `OverviewTab`
  - `ActivityTab`
  - `TopologyTab`
  - `MembersTab`
  - `BindingsTab`
  - `AssetsTab`
  - `AdvancedTab`
- [x] `detail.tsx` 只保留：
  - route state 解析
  - tab 切换
  - query hook 组合
  - 页面级 fallback
- [x] team runtime lens 只负责数据组合，不把展示逻辑继续塞进去

验收：

- `detail.tsx` 体量显著下降
- tab 组件可以独立测试
- 后续再加 Team 功能时，不需要继续在一个大文件里堆叠

依赖：

- 优先完成 Phase 1

---

## Phase 3: 语义收口

目标：

- 把现有页面结构和 PRD 的产品语义对齐

任务：

- [x] `connectors` tab 收口为 `Bindings` 或 `Connections & Policies`
- [x] 新增 `Assets` 视图，承接 scope workflows / scripts
- [x] `members` tab 的实现说明改成“参与者视图”
- [x] 所有成员卡片或表格里，明确展示：
  - actorId
  - serviceId
  - implementation kind
- [x] 把 `testing conversation`、`open service mapping`、`open studio` 都定义成上下文动作，而不是新的一级工作区

关键文件：

- [src/pages/teams/detail.tsx](../../apps/aevatar-console-web/src/pages/teams/detail.tsx)
- [src/pages/teams/workflowOperationalUnits.ts](../../apps/aevatar-console-web/src/pages/teams/workflowOperationalUnits.ts)
- [src/pages/teams/runtime/teamIntegrations.ts](../../apps/aevatar-console-web/src/pages/teams/runtime/teamIntegrations.ts)

验收：

- 页面文案与 PRD 一致
- 团队页里没有“成员目录”假象
- `Bindings` 能容纳 connector / service / policy 视图

---

## Phase 4: Platform 深链收口

目标：

- 建立 Teams -> Platform 的稳定下钻体验

任务：

- [x] 从 Team Header、Members、Bindings 中稳定跳到：
  - `/services`
  - `/governance`
  - `/deployments`
  - `/runtime/explorer`
- [x] 统一 actorId / serviceId / scopeId 的 route builder
- [x] 避免散落的硬编码路径拼接
- [x] 明确哪类跳转保留 query state，哪类直接跳 detail

关键文件：

- [src/shared/navigation/runtimeRoutes.ts](../../apps/aevatar-console-web/src/shared/navigation/runtimeRoutes.ts)
- [src/shared/navigation/scopeRoutes.ts](../../apps/aevatar-console-web/src/shared/navigation/scopeRoutes.ts)
- [src/shared/navigation/teamRoutes.ts](../../apps/aevatar-console-web/src/shared/navigation/teamRoutes.ts)

验收：

- 从团队页到平台页的跳转语义稳定
- 用户能从一个 Team 问题追到对应 service / deployment / topology

---

## Phase 5: Studio 深链与高级编辑

目标：

- 让高级编辑真正成为 Team 上下文中的能力，而不是脱离上下文的页面

任务：

- [x] 所有进入 Studio 的入口都显式带 `scopeId`
- [x] `workflow / script / execution / tab` 等现有 query 语义保持兼容
- [x] 没有 `scopeId` 时才允许回退到 app context

关键文件：

- [src/shared/studio/navigation.ts](../../apps/aevatar-console-web/src/shared/studio/navigation.ts)
- [src/pages/studio/index.tsx](../../apps/aevatar-console-web/src/pages/studio/index.tsx)
- [src/pages/teams/detail.tsx](../../apps/aevatar-console-web/src/pages/teams/detail.tsx)

验收：

- 团队页进入 Studio 后，用户不会丢失当前 Team 上下文
- 现有 Studio 打开方式不被破坏

---

## Phase 6: 测试与清理

目标：

- 在不放大范围的前提下，把关键回归风险兜住

优先测试文件：

- [src/pages/teams/index.test.tsx](../../apps/aevatar-console-web/src/pages/teams/index.test.tsx)
- [src/pages/teams/detail.test.tsx](../../apps/aevatar-console-web/src/pages/teams/detail.test.tsx)
- [src/pages/teams/runtime/teamRuntimeLens.test.ts](../../apps/aevatar-console-web/src/pages/teams/runtime/teamRuntimeLens.test.ts)
- [src/pages/studio/index.test.tsx](../../apps/aevatar-console-web/src/pages/studio/index.test.tsx)
- [src/routesConfig.test.ts](../../apps/aevatar-console-web/src/routesConfig.test.ts)

任务：

- [x] 为 `/teams` route 迁移补测试
- [x] 为 tab 组件拆分补最小回归测试
- [x] 为 `bindings/assets/participants` 语义补展示测试
- [x] 为 Studio 深链优先级补测试
- [x] 清理失效或重复的旧首页逻辑

验收：

- 首页、详情页、Studio 深链、Platform 跳转这四条核心路径有测试保护

---

## 4. 推荐开发顺序

严格建议按下面顺序推进：

1. `Phase 1`：先把 `/teams` 真正切到 `teams` 页面
2. `Phase 2`：拆 `detail.tsx`
3. `Phase 3`：做 `Bindings / Assets / Participants` 语义收口
4. `Phase 4`：统一 Platform 深链
5. `Phase 5`：补 Studio 团队上下文
6. `Phase 6`：做测试与清理

不要先做的事：

- 不要先重画视觉
- 不要先做多团队首页
- 不要先扩展新的平台页

---

## 5. 里程碑验收

## Milestone A

- `/teams` 已经脱离 `scopes/overview`
- 首页只承诺 `current-team-first`

## Milestone B

- Team Detail 已拆 tab 组件
- `detail.tsx` 不再继续膨胀

## Milestone C

- Team -> Platform -> Studio 三条主链都顺畅
- 团队页语义与 PRD 一致

---

## 6. 风险提示

### 风险一：继续在 `detail.tsx` 上叠功能

处理：

- 把“拆分 detail”视为必要工作，不是可选优化

### 风险二：首页需求膨胀回多团队总览

处理：

- 没有 team catalog 事实源前，严禁承诺多团队运营首页

### 风险三：`connectors` 文案收口后，信息结构不够

处理：

- 优先按 `Bindings / Connections & Policies` 组织，不要只换标签不换模型

---

## 7. 建议直接开工的第一刀

如果下一步立刻进入代码，建议先做这一组最小闭环：

- [x] `config/routes.ts`：把 `/teams` 改为 `./teams`
- [x] `src/pages/teams/index.tsx`：成为真正首页壳层
- [x] `src/pages/scopes/overview.tsx`：降为 legacy 过渡实现
- [x] `src/routesConfig.test.ts`：补 route 断言

这一步完成后，整个 Team-first 主入口才算真正站稳。
