# 2026-04-09 Aevatar Console Web Frontend Implementation Checklist

## 0. 2026-04-10 Frontend-only 降级说明

这份清单最初为更完整的 `Team-first` 首页与详情工作台准备，但 2026-04-10 已明确收紧为 **frontend-only V0**：

- 不改任何后端 contract
- `/teams` 不再只做自动跳详情，而是渲染一个 **current-session-team roster preview**
- 页面承诺降级为：帮助用户判断“当前 session 这支 team 值不值得现在点进去”
- 不做真实多团队列表、不做 queue groups、不做 fake KPI strip、不做 unsupported row actions
- feature flag 仅作为前端本地 build-time 开关使用；关闭时回退到 legacy `/teams` 首页

## 1. 目标

将 [2026-04-08-aevatar-product-definition.md](./2026-04-08-aevatar-product-definition.md) 落成一份可直接执行的前端实施清单，确保 `console-web` 从当前的 `Projects / Platform / Studio` 组合入口，收敛到：

- `Team-first` 的默认入口
- `control-plane` 价值不丢失的团队详情工作台
- `Studio` 从团队上下文进入，而不是孤立入口
- 全程复用现有后端能力，不发明第二套 runtime truth

## 2. 本轮边界

- 只做 `frontend-only` 交付，不改后端 contract。
- 不引入第二套 runtime 模型，所有团队事实统一来自同一个 `team runtime lens`。
- 不做真实多团队列表；在没有 `listScopes()` 或等价接口前，`/teams` 只负责渲染当前 session team 的 roster-style preview。
- 不做 dedicated human inbox / work queue。
- 不做 full historical replay editor。
- 不做 organization-level health center。

## 3. 设计与工程原则

- 外部叙事使用 `Team-first`，价值证明仍然是 `运行 / 协作 / 日志 / 版本`。
- `collaboration canvas` 是团队详情的主工作区，不能退化成普通 dashboard。
- `unknown / delayed / partial` 必须诚实暴露，不能伪装成 healthy。
- `Studio` 深链必须带显式 `scopeId`；只有在缺失时才允许退回 app context / auth session fallback。
- 旧入口需要保留过渡路径，但新默认首页必须可通过 feature flag 切到 `Teams`。

## 4. 交付顺序

### Wave 0: 开关与默认入口

- [ ] 在 [config/config.ts](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/config/config.ts) 暴露 `process.env.AEVATAR_CONSOLE_TEAM_FIRST_ENABLED`。
- [ ] 新增 [src/shared/config/consoleFeatures.ts](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/shared/config/consoleFeatures.ts)，集中解析 Team-first 开关，避免在页面里散落读 env。
- [ ] 保持 [src/shared/navigation/consoleHome.ts](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/shared/navigation/consoleHome.ts) 稳定指向 `/teams`，不要让 rollout 开关改全局 home route。
- [ ] `Team-first` 开关只在 `/teams` route 内切换 `LegacyTeamsHome` 与 `TeamsHomeRosterV0`，不扩散到全局 app bootstrap。

### Wave 1: 路由与团队入口

- [ ] 修改 [config/routes.ts](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/config/routes.ts)：
  - 新增 `/teams`
  - 新增 `/teams/:scopeId`
  - 保留 `/overview`、`/scopes`、`/scopes/overview` 的过渡跳转
  - 根路由 `/` 在 flag 打开时默认跳到 `/teams`
- [ ] 修改 [src/pages/teams/index.tsx](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/pages/teams/index.tsx)，将其收成 `route shell + LegacyTeamsHome + TeamsHomeRosterV0`。
- [ ] 新增 [src/pages/teams/detail.tsx](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/pages/teams/detail.tsx)，承载团队详情统一工作台。
- [ ] 约束 `/teams` 行为：
  - 有当前 team 时渲染 roster-style preview，而不是自动跳 `/teams/:scopeId`
  - 无当前 team 时进入诚实 empty / blocked state，不伪造 team list
  - 只有用户主动点击 `View details` 时，才进入 `/teams/:scopeId`

### Wave 2: Scope 上下文与共享 helper 收口

- [ ] 把 [src/pages/scopes/components/resolvedScope.ts](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/pages/scopes/components/resolvedScope.ts) 提升到共享层，建议新位置：
  - [src/shared/scope/context.ts](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/shared/scope/context.ts)
- [ ] 把 [src/pages/scopes/components/scopeQuery.ts](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/pages/scopes/components/scopeQuery.ts) 提升到共享导航层，建议新位置：
  - [src/shared/navigation/scopeRoutes.ts](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/shared/navigation/scopeRoutes.ts)
- [ ] 让 `Teams`、`Scopes Overview`、`Studio` 使用同一套 `scopeId` 解析与 URL 构建 helper，避免重复 query 语义。
- [ ] 清理页内重复的 binding / revision 展示逻辑；若现有 [src/shared/studio/models.ts](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/shared/studio/models.ts) 不够用，则新增共享 summary helper，不要把 Team 页面逻辑塞回 `scopes` 私有目录。

### Wave 3: Team Runtime Lens

- [ ] 在 [src/pages/teams/](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/pages/teams/) 下新增 `team runtime lens` 组合层，第一版保持 page-local，不要一开始就抽成全局平台层。
- [ ] `team runtime lens` 必须统一产出这些前端派生事实：
  - 当前 `scopeId`
  - 当前绑定 revision / target / actor
  - 服务与 deployment 基本状态
  - 最近 run / 当前活跃 run / attention state
  - compare 所需的当前版本与最近成功版本
  - governance snapshot 所需的最小可信事实
- [ ] `team runtime lens` 只做组合和派生，不承担持久状态，不做 query-time replay，不建立进程内事实缓存。
- [ ] `team runtime lens` 先加载核心团队上下文，再按 tab / selection 懒加载 graph、compare、playback 所需数据。

建议首批实现文件：

- [src/pages/teams/runtime/useTeamRuntimeLens.ts](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/pages/teams/runtime/useTeamRuntimeLens.ts)
- [src/pages/teams/runtime/teamRuntimeLens.ts](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/pages/teams/runtime/teamRuntimeLens.ts)
- [src/pages/teams/runtime/teamRuntimeLens.test.ts](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/pages/teams/runtime/teamRuntimeLens.test.ts)

### Wave 4: 团队详情统一工作台

- [ ] 以 [src/pages/scopes/overview.tsx](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/pages/scopes/overview.tsx) 的 `page shell + inspector + context drawer` 结构为复用基座，而不是从零另起一套页面骨架。
- [ ] 以 [src/pages/studio/components/StudioShell.tsx](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/pages/studio/components/StudioShell.tsx) 的工作台密度为参考，确保团队详情不是传统卡片堆叠页。
- [ ] 在团队详情中落地这几个固定模块：
  - `Team header`
  - `Collaboration canvas`
  - `Activity rail`
  - `Context inspector`
  - `Health / Trust Rail`
  - `Governance Snapshot`
- [ ] 默认首屏自动聚焦当前活跃路径；没有活跃 run 时回到当前 serving binding。
- [ ] 窄屏保持 `canvas first`，活动与详情进入 segmented panel / drawer，不允许页面退化成纯纵向长列表。

### Wave 5: Run Compare / Change Diff

- [ ] 在团队 activity 中落地 `Run Compare / Change Diff`，让团队页具备调试价值，而不只是讲故事。
- [ ] compare 默认比较：
  - 当前 serving / active run
  - 最近一次成功且可比较的 run
- [ ] 若缺 comparator，必须诚实展示 `not enough history`，不能伪造 diff。
- [ ] compare 模块的数据只能来自共享 `team runtime lens` 的衍生事实或其受控懒加载，不允许页面各自重复拼查询。

### Wave 6: Health / Trust Rail 与 Governance Snapshot

- [ ] 团队详情顶部或右侧放置紧凑的 `Health / Trust Rail`，至少能回答：
  - 当前是否健康
  - 当前是否 degraded / blocked
  - 当前是否被 human override
  - 当前是否 risky to change
- [ ] 加入轻量 `Governance Snapshot`，回答四个买家级问题：
  - Who is serving now
  - What changed recently
  - Can a human intervene
  - Is there enough audit trail to trust this team
- [ ] `Health / Trust Rail` 与 `Governance Snapshot` 共享同一 runtime truth，不允许各自产生一套健康口径。

### Wave 7: Studio 团队上下文深链

- [ ] 修改 [src/shared/studio/navigation.ts](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/shared/studio/navigation.ts)，为 `buildStudioRoute()` 及其上层 helper 增加显式 `scopeId`。
- [ ] 修改 [src/pages/studio/index.tsx](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/pages/studio/index.tsx)：
  - 优先读取 route 上的 `scopeId`
  - route 无 `scopeId` 时，再退回 app context / auth session
  - 保持现有 query 语义：`workflow / script / execution / tab / draft / legacy`
- [ ] 团队页、activity、run detail、binding detail 进入 `Studio` 时，全部使用显式 `scopeId` 深链。
- [ ] 不把 `scopeId` 可见就当成授权成立；授权仍由既有 auth / backend 返回值决定。

### Wave 8: 旧页面与过渡清理

- [ ] 删除或下线路由孤儿页 [src/pages/overview/index.tsx](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/pages/overview/index.tsx)，避免继续存在第二套首页叙事。
- [ ] 评估 [src/pages/overview/useOverviewData.ts](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/pages/overview/useOverviewData.ts) 是否还有复用价值：
  - 有复用价值则迁移到共享层
  - 无复用价值则一并删除
- [ ] 在过渡期保留 `/scopes/overview`，但它降级为 legacy project workspace，不再承担默认首页角色。

## 5. 数据来源对齐

`Team runtime lens` 首批允许复用的真实数据来源：

- `studioApi.getAuthSession()`
- `studioApi.getScopeBinding(scopeId)`
- `scopesApi.listWorkflows(scopeId)`
- `scopesApi.listScripts(scopeId)`
- `servicesApi.listServices({ tenantId: scopeId, ... })`
- 现有 runtime actors / runs API
- 现有 governance / binding / deployment 页面已经读取到的事实

本轮禁止：

- 在 Team 页面里临时回放 event store 拼状态
- 为 Team 页面单独维护 service/run/binding 的影子缓存真相
- 因为数据不全就伪造 `healthy` 或 `fully live`

## 6. 测试清单

- [ ] 为新开关补测试：
  - [src/shared/config/consoleFeatures.test.ts](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/shared/config/consoleFeatures.test.ts)
  - [src/shared/navigation/consoleHome.test.ts](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/shared/navigation/consoleHome.test.ts)
- [ ] 为新路由与跳转补测试：
  - [src/pages/teams/index.test.tsx](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/pages/teams/index.test.tsx)
  - [src/pages/teams/detail.test.tsx](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/pages/teams/detail.test.tsx)
- [ ] 更新 [src/pages/studio/index.test.tsx](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/pages/studio/index.test.tsx)，覆盖 route `scopeId` 优先级。
- [ ] 更新 [src/pages/scopes/overview.test.tsx](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/pages/scopes/overview.test.tsx)，保证 legacy 路径在过渡期仍然可用。
- [ ] 为 `team runtime lens` 的派生逻辑补单测，重点覆盖：
  - missing data
  - partial data
  - degraded / blocked
  - compare baseline 缺失
  - human override

## 7. 验收标准

达到以下结果后，Wave 1 可视为完成：

- 打开 flag 后，登录后的默认入口进入 `Teams`，而不是 `Projects`。
- `/teams` 能稳定解析当前团队并渲染诚实的 single-team roster preview。
- 团队详情能在不改后端 contract 的前提下显示：
  - collaboration canvas
  - activity rail
  - health / trust rail
  - governance snapshot
  - Studio deep link
- 团队 activity 支持最小可用的 run compare。
- `Studio` 团队深链带 `scopeId` 后，仍然保持现有 `workflow / script / execution` 打开能力。
- 关闭 flag 后，现有 `/scopes/overview` 仍保持旧行为。

对 frontend-only V0，还要额外满足：

- 页面明确写清 `current session team only` / `reference roster`，不伪装成真实多团队排序
- 不再出现 fake KPI strip、queue notes、`Pause` 等 unsupported actions
- 首页只保留一个主动作 `View details`

## 8. Rollout 策略

- 阶段 1：flag 默认关闭，仅 internal / demo 环境开启。
- 阶段 2：团队页可 dogfood 后，切换默认首页到 `/teams`。
- 阶段 3：确认旧首页不再承担主叙事后，再清理 legacy 文案与孤儿入口。

## 9. 推荐实现顺序

建议严格按以下顺序开工：

1. local feature flag parsing
2. `/teams` route shell + honest roster preview
3. `team runtime lens`
4. `/teams/:scopeId` detail shell
5. team detail modules
6. Studio `scopeId` deep link
7. legacy cleanup + tests
