---
title: "Aevatar Console Web AI Teams + Studio 实施设计（基于 PR #145 与 refactor/frontend）"
status: draft
owner: AbigailDeng
---

# Aevatar Console Web AI Teams + Studio 实施设计

## 1. 文档目的

这份文档用于把 PR [#145](https://github.com/aevatarAI/aevatar/pull/145) 的产品文档，翻译成一份适用于 `refactor/frontend` 分支的前端实施设计。

它不重复复制原始产品文档，而是回答三个更直接的问题：

- PR #145 中哪些产品结论继续采纳。
- 在 `refactor/frontend` 当前代码基线下，这些结论应该落到哪些文件和模块。
- 哪些能力本轮直接实现，哪些能力保留为后续波次。

## 2. 输入依据

- PR #145 原始文档分支：`docs/2026-04-08_console-web-ai-teams-design`
- 原始文档：
  - `docs/designs/2026-04-08-console-web-ai-teams.md`
  - `docs/designs/2026-04-08-studio-redesign.md`
- 当前仓库已有设计文档：
  - [`docs/design/2026-04-08-aevatar-product-definition.md`](./2026-04-08-aevatar-product-definition.md)
  - [`docs/design/2026-04-09-aevatar-console-web-frontend-implementation-checklist.md`](./2026-04-09-aevatar-console-web-frontend-implementation-checklist.md)
- 当前前端分支已有实现基线：
  - `apps/aevatar-console-web/config/routes.ts`
  - `apps/aevatar-console-web/src/pages/teams/`
  - `apps/aevatar-console-web/src/shared/navigation/scopeRoutes.ts`
  - `apps/aevatar-console-web/src/shared/studio/navigation.ts`
  - `apps/aevatar-console-web/src/shared/config/consoleFeatures.ts`

## 3. 本轮核心结论

### 3.1 继续采纳的产品结论

- `Scope = Team` 继续作为用户层表达。
- Console Web 继续采用 `Teams + Platform` 双层信息架构。
- `Studio` 继续定义为团队构建器，不作为一级导航核心入口。
- 团队详情页继续作为产品主工作台。
- 本轮继续坚持 `frontend-only`，不新增后端 contract，不重写 runtime truth。
- 预览稿中的 HTML 仍只作为参考，实现以现有 Ant Design 和当前页面骨架为准。

### 3.2 面向当前分支的修正

- `refactor/frontend` 已经有 `/teams`、`/teams/:scopeId`、`team runtime lens` 和 `scopeId` 深链能力，因此本轮不从零开始设计。
- `/teams` 在当前分支更适合作为“当前团队上下文解析入口”，而不是必须先做多团队卡片首页。
- 团队详情页在当前分支已经是 workbench 形态，因此设计上采用“6 个概念分区 + 响应式分段面板”，而不是强制回退成传统 dashboard。
- `Studio` 已经支持 `scopeId` 查询参数，本轮重点是补齐入口、面包屑和术语映射，而不是重做编辑器内核。
- `/teams/new` 暂不作为当前波次交付项。原因是 PR #145 强调零后端变更，而当前分支也没有稳定的团队模板与创建流。

## 4. 当前分支基线判断

| 领域 | 当前基线 | 本文决策 |
|---|---|---|
| 默认首页 | `getConsoleHomeRoute()` 已固定返回 `/teams` | 保留，作为 Team-first 的默认入口 |
| 功能开关 | `consoleFeatures.ts` 当前等价于常开 | 保留常开语义，后续如需回滚再恢复 env 开关 |
| 路由骨架 | `routes.ts` 已有 Teams / Platform 主结构 | 保留主结构，继续收口旧路径 |
| 团队入口页 | `src/pages/teams/index.tsx` 已负责 scope 解析 | 继续作为团队上下文解析页 |
| 团队详情页 | `src/pages/teams/detail.tsx` 已有 runtime workbench | 以现有 workbench 为基础做术语和分区对齐 |
| Studio 深链 | `buildStudioRoute()` 已支持 `scopeId` | 强化从团队页进入的深链与上下文显示 |
| 术语层 | 仍有大量英文和工程术语暴露 | 本轮重点清理用户层术语 |

## 5. 信息架构与路由设计

### 5.1 导航层级

Console Web 继续分成两层：

- 用户层：围绕“我的团队”组织，强调团队、成员、事件、协作。
- 平台层：围绕治理、服务、拓扑、部署组织，强调管理员视角和平台可观测性。

### 5.2 一级路由

| 路由 | UI 名称 | 层级 | 说明 |
|---|---|---|---|
| `/teams` | 我的团队 | 用户层 | 解析当前团队上下文并跳转到详情页 |
| `/teams/:scopeId` | 团队详情 | 用户层 | 团队主工作台 |
| `/governance` | Governance | 平台层 | 治理与策略 |
| `/services` | Services | 平台层 | 服务与绑定 |
| `/runtime/explorer` | Topology | 平台层 | 全局拓扑探索 |
| `/deployments` | Deployments | 平台层 | 部署与版本 |
| `/settings` | 设置 | 底部 | 账户与环境设置 |

### 5.3 过渡与隐藏路径

以下路径保留为兼容入口，但不再承担一级叙事：

- `/overview`
- `/scopes`
- `/scopes/overview`
- `/studio`
- `/runtime/workflows`
- `/runtime/primitives`
- `/runtime/runs`
- `/runtime/gagents`
- `/chat`

设计要求：

- 用户层只保留团队叙事，不再暴露孤立的工程概念。
- 平台层保留英文技术名词，避免和现有平台能力脱节。
- 旧路径允许跳转，但菜单和主入口不能再让用户误以为它们是并列产品。

## 6. 页面与组件层设计

### 6.1 页面层文件划分

| 文件 | 角色 | 设计要求 |
|---|---|---|
| `apps/aevatar-console-web/src/pages/teams/index.tsx` | 团队入口页 | 只做 scope 解析、跳转和 blocked state，不伪造 team list |
| `apps/aevatar-console-web/src/pages/teams/detail.tsx` | 团队详情主工作台 | 承载概览、事件拓扑、事件流、成员、连接器、高级编辑 6 个核心分区 |
| `apps/aevatar-console-web/src/pages/teams/runtime/useTeamRuntimeLens.ts` | 团队页面数据组合 hook | 聚合 binding、services、runs、graph、audit 等事实 |
| `apps/aevatar-console-web/src/pages/teams/runtime/teamRuntimeLens.ts` | 团队派生模型 | 统一导出 health、compare、playback、governance、members 等派生事实 |
| `apps/aevatar-console-web/src/pages/teams/runtime/teamIntegrations.ts` | 连接器摘要层 | 把 governance / connector 信息压成团队可读摘要 |
| `apps/aevatar-console-web/src/pages/scopes/overview.tsx` | 旧入口兼容页 | 降级为 legacy workspace，文案改成“我的团队” |
| `apps/aevatar-console-web/src/pages/studio/index.tsx` | 团队构建器 | 接受团队上下文，显示团队/成员面包屑，并完成 Studio 术语替换 |

### 6.2 共享层文件划分

| 文件 | 角色 | 设计要求 |
|---|---|---|
| `apps/aevatar-console-web/config/routes.ts` | 路由声明 | 维持 Teams + Platform 主结构，旧路径继续 redirect / hide |
| `apps/aevatar-console-web/src/shared/navigation/consoleHome.ts` | 默认首页 | 统一返回 `/teams` |
| `apps/aevatar-console-web/src/shared/config/consoleFeatures.ts` | Team-first 开关 | 当前常开，避免页面自行判断 |
| `apps/aevatar-console-web/src/shared/navigation/scopeRoutes.ts` | `scopeId` 解析与链接构建 | 统一 `/teams`、`/teams/:scopeId`、legacy scope 页面路由语义 |
| `apps/aevatar-console-web/src/shared/studio/navigation.ts` | Studio 深链构建 | 统一 `scopeId`、`workflowId`、`scriptId`、`executionId` 传递 |

## 7. 团队详情页设计

`/teams/:scopeId` 是本轮最重要的页面。它不再是“项目详情”或“运行时调试页面”，而是“团队视角下的统一工作台”。

### 7.1 六个核心分区

| 分区 | 目标 | 当前实现要求 |
|---|---|---|
| 概览 | 告诉用户这个团队现在是什么状态 | 展示 team title、health、当前 owner、最新 handoff、关键操作 |
| 事件拓扑 | 告诉用户团队如何协作 | 复用现有 graph / XYFlow 能力，聚焦当前 actor 或活跃路径 |
| 事件流 | 告诉用户最近发生了什么 | 展示 run audit、playback、异常事件和 compare 入口 |
| 团队成员 | 告诉用户谁在团队里、各自负责什么 | 展示成员、实现类型、service 映射、编辑入口 |
| 连接器 | 告诉用户团队接入了哪些外部能力 | 展示 Telegram / HTTP / MCP 等连接器与绑定摘要 |
| 高级编辑 | 告诉用户从哪里进入构建器 | 提供带 `scopeId` 的 Studio deep link |

### 7.2 布局原则

- 桌面端保持高密度 workbench 风格，不回退成单纯的卡片首页。
- 窄屏继续使用 segmented panel / drawer，将 `activity` 与 `details` 分段展示。
- 首屏优先展示当前活跃路径；无活跃 run 时退回 serving binding 视角。
- 所有健康度、审计、compare、governance 信息必须来自同一份 `team runtime lens` 派生事实。

### 7.3 与 PR #145 的差异处理

- PR #145 中“6 个 Tabs”在当前实现中可以表现为“6 个稳定工作区块”，不要求视觉上必须是传统 tab strip。
- 事件拓扑和事件流都允许基于当前 focus actor / current run 展开，而不是伪造一个永远完整的 scope 总图。
- 团队详情页继续保留调试价值，不能只剩产品包装文案。

## 8. Studio 设计

Studio 的产品定位保持不变：

- `Studio = 团队构建器`
- `Studio` 服务于当前 `Scope`
- `Studio` 从团队上下文进入，而不是脱离团队的独立一级导航

### 8.1 入口设计

必须支持三种入口：

- 团队详情页“高级编辑”
- 团队成员表中单成员的“编辑”
- 未来预留的团队创建流入口

### 8.2 上下文设计

进入 `Studio` 时显式传递：

- `scopeId`
- `workflowId` 或 `scriptId`
- `executionId`（如从测试运行或回放进入）

Studio 顶部应显示团队上下文：

- 团队名
- 成员名（如果有）
- 当前工作区名

### 8.3 术语重映射

| 当前术语 | 新术语 | 适用范围 |
|---|---|---|
| Teams | 我的团队 / 团队 | 用户层首页与详情 |
| GAgents | 成员 | 用户层成员相关 UI |
| Workflows | 行为定义 | Studio 工作流入口与文案 |
| Scripts | 脚本行为 | Studio 脚本编辑入口 |
| Roles | Agent 角色 | Studio 角色管理 |
| Connectors | 集成 | Studio 连接器页与团队构建上下文 |
| Executions | 测试运行 | Studio 执行追踪 |
| Scope Overview | 我的团队 | legacy 页面文案 |
| Primitives | 连接器 / 集成能力 | 只在必须暴露时替换；用户层默认隐藏 |

术语规则：

- 路由路径、代码标识符继续保持英文。
- 用户层 UI 文案以中文为主。
- 平台层可保留 `Governance`、`Services`、`Topology`、`Deployments` 等英文，以减少认知错位。

## 9. 数据源与事实来源

本轮继续坚持“零后端变更，统一事实来源”。

| 前端能力 | 事实来源 |
|---|---|
| 当前团队上下文 | `studioApi.getAuthSession()` + shared scope context |
| 团队绑定与当前 serving revision | `studioApi.getScopeBinding(scopeId)` |
| 团队成员 | `runtimeGAgentApi.*` |
| 事件拓扑 | `runtimeActorsApi.getActorGraphEnriched(actorId)` |
| 事件流 / 审计 | `scopeRuntimeApi.getServiceRunAudit(...)` |
| 服务与部署视角 | `servicesApi.*` |
| 连接器与绑定摘要 | `governanceApi.*` + `teamIntegrations.ts` |

约束：

- 不为团队页建立影子缓存真相。
- 不因为数据缺失就伪造 healthy / live / fully connected。
- 不在页面层各自散落拼装同一组运行时事实。

## 10. 实施范围

### 10.1 本轮要做

- 在 `refactor/frontend` 基线之上固定 Team-first 叙事。
- 保持 `/teams` 为默认首页与上下文解析入口。
- 强化 `/teams/:scopeId` 的团队工作台表达。
- 把 `scopes/overview` 的残留文案改到“我的团队”。
- 把 Studio 入口、面包屑和主要 UI 术语对齐到团队构建器语义。
- 让用户层减少工程术语暴露，但保留平台层调试价值。

### 10.2 本轮不做

- 不新增后端 team create API。
- 不强制交付 `/teams/new`。
- 不重做 XYFlow、Monaco、Studio 执行内核。
- 不把平台层页面全部中文化。
- 不构造 scope 级伪聚合总图来掩盖真实数据边界。

## 11. 验收标准

- 登录后默认入口进入 `/teams`。
- `/teams` 能稳定解析当前团队并进入 `/teams/:scopeId`。
- 团队详情能清晰表达 6 个核心分区。
- `GAgents`、`Workflows`、`Roles`、`Connectors`、`Executions` 在用户层和 Studio 中有明确的新文案映射。
- 用户从团队页进入 Studio 时，`scopeId` 上下文不丢失。
- legacy 页面仍可用，但不再承担主导航职责。

## 12. 建议实施顺序

1. 稳定 Team-first 默认入口和旧路由收口。
2. 统一用户层术语替换。
3. 完成团队详情六个分区的命名、导航和可见性对齐。
4. 完成团队成员与连接器视图的产品化包装。
5. 完成 Studio 面包屑、入口和术语重映射。
6. 最后清理残余 legacy 文案与菜单噪音。
