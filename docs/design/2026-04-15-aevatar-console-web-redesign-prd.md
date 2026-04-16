---
title: "Aevatar Console Web 重设计 PRD（Capability-first AI Teams V1）"
status: draft
owner: potter-sun
last_updated: 2026-04-15
---

# Aevatar Console Web 重设计 PRD（Capability-first AI Teams V1）

## 1. 文档定位

本稿只基于两类输入重写，不再参考 `2026-04-08-aevatar-product-definition.md`：

- 设计方向文档：[2026-04-08-console-web-ai-teams.md](https://github.com/aevatarAI/aevatar/blob/docs/2026-04-08_console-web-ai-teams-design/docs/designs/2026-04-08-console-web-ai-teams.md)
- 设计预览页：[2026-04-08-console-web-ai-teams-preview.html](https://github.com/aevatarAI/aevatar/blob/docs/2026-04-08_console-web-ai-teams-design/docs/designs/2026-04-08-console-web-ai-teams-preview.html)

本稿的核心原则不是“把设计稿直接搬成 PRD”，而是：

1. 保留设计稿里正确的产品叙事。
2. 用仓库里已经存在的后端能力重新约束前端范围。
3. 把不具备稳定后端事实源的展示降级为非 V1 承诺。

一句话定义：

> Aevatar Console Web V1 应该首先被理解成“运营你的 AI Team 的工作台”，并在需要时自然下钻到 Platform 层的 service、governance、deployment 与 traffic 管理。

---

## 2. 产品判断

参考设计稿和 preview 后，以下判断成立，且应保留：

1. `Scope = Team` 仍然是本轮最合理的前端产品映射。
2. `Teams + Platform` 双层导航是正确方向。
3. `Team Detail` 是产品核心页，不是 `Chat`、`Studio`、`Runs`。
4. `Chat` 应退为上下文动作，不应再做一级导航主语。
5. `Studio` 是高级编辑器，应从团队上下文进入。
6. 团队页必须能把“用户层语言”和“平台层真实对象”桥接起来。

但参考稿里有一部分内容不能原样承诺给 V1：

1. `我的团队 2 / 活跃团队 / 在线率 / 今日处理消息` 这类首页总览指标，在当前仓库里没有看到明确的一等 team catalog 与 team analytics 查询面。
2. `团队成员` 不能直接等价为一个稳定、强类型、跨 scope 的成员目录；当前更接近的是 run graph、actor snapshot、scope binding、service revision 的组合视图。
3. `连接器` 也不应只做 connector-only 视角；当前后端暴露的是更宽的 `bindings / policies / endpoint catalog` 治理模型。

所以本次 PRD 的目标不是“实现 preview 的每个数字”，而是“实现 preview 背后的正确产品结构，并让每个承诺都能落在现有后端能力上”。

---

## 3. 问题定义

当前 Console Web 的主要问题不是页面少，而是产品主语混乱：

- 导航大量暴露工程术语：`Studio`、`GAgents`、`Primitives`、`Mission Control`、`Invoke Lab`
- 用户先看到的是系统分层，不是“我的团队正在做什么”
- 运行态、治理态、编辑态都在抢一级入口
- 团队级体验和平台级体验没有明确分界

用户真正要完成的任务并不是“浏览系统对象”，而是：

1. 找到自己正在运营的 Team。
2. 看清这个 Team 当前是否正常、最近做了什么、是否需要人工介入。
3. 能在一个页面里发起测试、查看拓扑、查看事件流、继续处理挂起 run。
4. 需要深挖时，再进入 Platform 的 service/governance/deployment 视图。

---

## 4. 后端能力真相

本节只记录当前仓库里已经能证明存在的能力边界。

### 4.1 Team 运行与观察能力

已存在：

- `POST /api/chat`、`GET /api/ws/chat`
- `Workflow` run 的 SSE / WebSocket 实时事件输出
- `GET /api/workflows`、`/api/workflow-catalog`、`/api/capabilities`
- `GET /api/actors/{actorId}`
- `GET /api/actors/{actorId}/timeline`
- `GET /api/actors/{actorId}/graph-edges`
- `GET /api/actors/{actorId}/graph-enriched`
- `POST /api/workflows/resume`
- `POST /api/workflows/signal`
- scope-service 路径下的 `resume / signal / stop` run control 端点

这些能力意味着前端可以稳定构建：

- 当前 run 状态
- timeline / audit / topology
- 人工输入、人工审批、wait signal 等介入动作
- 基于同一条 Projection Pipeline 的实时流和读模型视图

特别重要的信号：

- `RUN_STARTED / RUN_FINISHED / RUN_ERROR`
- `STEP_STARTED / STEP_FINISHED`
- `TEXT_MESSAGE_*`
- `TOOL_CALL_*`
- `aevatar.human_input.request`
- `aevatar.workflow.waiting_signal`

这使得 `Team Detail` 里的“活动流 + 待处理事项 + 干预操作”是有后端基础的。

### 4.2 Scope / Team 资产与上下文能力

已存在：

- `GET /api/studio/context` / `GET /api/app/context`
- `GET /api/scopes/{scopeId}/binding`
- `GET /api/scopes/{scopeId}/revisions`
- `GET /api/scopes/{scopeId}/runs`
- `GET /api/scopes/{scopeId}/runs/{runId}`
- `GET /api/scopes/{scopeId}/runs/{runId}/audit`
- `GET /api/scopes/{scopeId}/workflows`
- `GET /api/scopes/{scopeId}/scripts`
- `GET /api/scopes/{scopeId}/chat-history`

已存在的编辑相关能力：

- scope workflow upsert / detail
- scope script upsert / detail / catalog / evolution proposal
- Studio generator / validator / draft run

这些能力意味着前端可以稳定构建：

- 当前 Team 的绑定状态
- 当前 Team 的工作流与脚本资产面
- 当前 Team 的最近 run 列表
- 当前 Team 的聊天历史或会话记录
- 带 `scopeId` 的 Studio 深链

### 4.3 Platform 控制面能力

已存在：

- `GET /api/services`
- `GET /api/services/{serviceId}`
- `GET /api/services/{serviceId}/revisions`
- `GET /api/services/{serviceId}/deployments`
- `GET /api/services/{serviceId}/serving`
- `GET /api/services/{serviceId}/rollouts`
- `GET /api/services/{serviceId}/traffic`
- `GET /api/services/{serviceId}/bindings`
- `GET /api/services/{serviceId}/endpoint-catalog`
- `GET /api/services/{serviceId}/policies`
- `GET /api/services/{serviceId}:activation-capability`

这些能力意味着 Platform 层应继续保留，且应该更清晰：

- `Services`：service lifecycle / revisions / primary actor / deployment status
- `Governance`：bindings / endpoint exposure / policies
- `Deployments`：serving targets / rollout / traffic / deployment catalog
- `Topology`：专家工具入口，不是 Teams 层主语

### 4.4 当前后端没有给出的稳定事实

V1 不应承诺：

- 一等 `team catalog / list scopes` 查询能力
- 组织级 `active teams / running members / avg uptime` 汇总指标
- 强类型 `team member roster` 主数据
- 可直接复用的全局 team health 排名
- 跨 team 的统一 human inbox / queue center

这意味着 preview 里的以下表达要降级为“视觉方向”，不能写成 V1 需求：

- 首页统计卡的团队总数、在线率、全局消息量
- 团队成员表里的“24h 在线率”作为稳定指标
- 真正意义上的“多团队运营首页”

---

## 5. 产品策略与取舍

### 5.1 取舍一：`/teams` 做“当前团队入口”，不是运营总览大盘

原因：

- 当前仓库里能确认的是 scope 上下文与 scope-scoped 查询，不是 team catalog
- 做假的多团队列表会让前端重新发明事实源

V1 决策：

- `/teams` 主体是“当前团队入口页”
- 若现有 Console 已有额外 scope 来源，可展示 `Recent Teams / Available Teams`
- 若没有，就诚实呈现 `current-scope-first`

### 5.2 取舍二：团队详情页主语是“运行与协作”，不是“高级编辑”

原因：

- 当前最强的后端能力是 run observation + projection query + intervention
- 这正好支撑 `Overview / Activity / Topology / Intervention`

V1 决策：

- `Studio` 保留，但降为团队详情页里的高级编辑入口
- 团队详情页优先服务“看状态、测一次、排问题、继续执行”

### 5.3 取舍三：`Members` 改成 `Participants` 语义更诚实

原因：

- 现有后端更像“参与者图谱 + 绑定 service + actor snapshot”，不是 canonical member directory
- graph node 也是 `node_type + properties` 的通用结构

V1 决策：

- 页面标题可以保留“成员”以贴近设计语言
- 但实现语义应按“参与者/运行参与单元”建模
- 不承诺 HR 式成员主数据、在线率、席位管理

### 5.4 取舍四：`Connectors` 应扩成 `Bindings`

原因：

- 当前治理面里真实存在的是 connector、service、secret、policy、endpoint exposure
- 单做 connector tab 会把治理模型压扁

V1 决策：

- 团队详情页使用 `Bindings` 或 `Connections & Policies`
- 在用户文案里可继续强调“连接器”
- 但信息结构必须容纳 service binding、secret、policy

### 5.5 取舍五：Platform 继续保留 service-centric 视角

原因：

- 这部分后端最成熟
- 团队页需要一个可靠的“下钻平台对象”出口

V1 决策：

- Teams 层负责“运营和理解”
- Platform 层负责“治理和追查”

---

## 6. 目标用户

### 6.1 Team Operator

关心：

- 这个 Team 现在正常吗
- 最近发生了什么
- 需不需要我批准、补输入、发 signal、停止 run
- 我怎么快速测一次

### 6.2 Builder / Automation Owner

关心：

- 当前 Team 绑定的 workflow/script 是什么
- 最近改动影响了哪个 service / revision / actor
- 我怎么带着 `scopeId` 进入 Studio

### 6.3 Platform Admin / DevOps

关心：

- service revision、deployment、serving、traffic、rollout
- governance policy、binding、endpoint exposure
- 某个 Team 问题最终映射到哪个 service / actor / deployment

---

## 7. 顶层信息架构

## 7.1 一级导航

### Teams

- `/teams`
- `/teams/:scopeId`

### Platform

- `/governance`
- `/services`
- `/deployments`
- `/runtime/explorer`

### System

- `/settings`

## 7.2 降级为二级入口或深层工具

| 当前入口 | V1 定位 | 处理方式 |
|---|---|---|
| `/chat` | Team 上下文动作 | `hideInMenu` |
| `/studio` | Team 的高级编辑 | `hideInMenu` + scope deep link |
| `/runtime/runs` | Team Activity 的深层页 | `hideInMenu` |
| `/runtime/mission-control` | 专家排障页 | `hideInMenu` |
| `/runtime/gagents` | 平台/专家工具 | `hideInMenu` 或合并 |
| `/runtime/workflows` | 资产面/高级编辑 | `hideInMenu` 或重定向 |
| `/runtime/primitives` | builder 辅助资料 | `hideInMenu` |
| `/scopes/invoke` | 调试工具 | `hideInMenu` |

---

## 8. Teams 层页面定义

## 8.1 `/teams` 团队入口页

### 页面目标

让用户第一屏理解：

- 我正在运营一个 AI Team
- 我可以进入当前 Team 查看活动、拓扑、绑定和资产
- 如果平台已有 scope 列表来源，我也可以切换或进入其它 Team

### 页面结构

1. 当前 Team Hero
2. 当前绑定状态摘要
3. 最近 run / 最近对话
4. 快捷动作
5. 可选的 `Recent Teams / Available Teams`

### 数据来源

- `GET /api/studio/context`
- `GET /api/scopes/{scopeId}/binding`
- `GET /api/scopes/{scopeId}/runs`
- `GET /api/scopes/{scopeId}/chat-history`

### V1 明确不做

- 假的全局 team KPI strip
- 没有事实源支撑的 team 排行榜
- “活跃团队数/在线率/今日消息总量”类总览数字

## 8.2 `/teams/:scopeId` Team Workbench

这是本轮最重要的页面。

### 页面定位

它不是：

- 纯 builder 页面
- 纯治理页面
- 纯聊天页面

它应该是：

- Team 当前状态的主工作台
- 用户层语言与平台对象的桥梁
- 运行观察、人工介入、资产查看、高级编辑的汇合点

### Header 必须回答的 6 个问题

1. 当前 Team 是谁
2. 当前绑定到哪个 service / revision
3. 当前 deployment / serving 状态如何
4. 最近一次 run 结果如何
5. 是否有待处理的人机中断
6. 下一步最可能动作是什么

### Header 主动作

- `测试对话`
- `查看活动`
- `继续处理`
- `打开高级编辑`
- `查看对应 Service`

## 8.3 Team Workbench Tabs

### Tab A：概览

目标：

- 用业务化语言总结当前 Team 是否健康、是否阻塞、最近输出了什么

内容：

- 当前 binding / revision / deployment 状态
- 最近 run 摘要
- 最近成功输出 / 最近错误
- 待处理 `human_input / human_approval / waiting_signal`
- Team 到 Platform 的映射摘要

后端来源：

- `GET /api/scopes/{scopeId}/binding`
- `GET /api/scopes/{scopeId}/runs`
- `GET /api/scopes/{scopeId}/runs/{runId}/audit`

### Tab B：活动

目标：

- 把 Team 最近发生的事情按 run 和事件流真实展示出来

内容：

- recent runs 列表
- 选中 run 的 audit / timeline
- `RUN_* / STEP_* / TEXT_* / TOOL_* / CUSTOM` 事件
- 错误、暂停、待信号、待审批高亮

主动作：

- `resume`
- `signal`
- `stop`
- `查看 audit`

后端来源：

- `GET /api/scopes/{scopeId}/runs`
- `GET /api/scopes/{scopeId}/runs/{runId}`
- `GET /api/scopes/{scopeId}/runs/{runId}/audit`
- `POST /api/scopes/{scopeId}/runs/{runId}:resume`
- `POST /api/scopes/{scopeId}/runs/{runId}:signal`
- `POST /api/scopes/{scopeId}/runs/{runId}:stop`

### Tab C：事件拓扑

目标：

- 展示 EventEnvelope 在 Team 参与者之间如何流动

内容：

- graph root actor
- participant nodes
- external system nodes
- edge types
- 当前焦点路径

后端来源：

- `GET /api/actors/{actorId}/graph-enriched`

说明：

- 这里适合保留 preview 的视觉方向
- 但节点语义应以 `actor / external system / model / connector` 为准
- 不应伪装成“群聊”

### Tab D：成员

目标：

- 显示当前 Team 的参与者视图，并把它们映射到 platform object

V1 实现语义：

- 用“参与者视图”实现“成员页”
- 数据来自 graph、run report、binding、service revision 的组合

内容：

- 名称
- 角色或职责
- 实现类型：workflow / scripting / static
- 关联 actorId
- 关联 governance serviceId
- 最近活动 / 错误状态

V1 不承诺：

- 绝对准确的“24h 在线率”
- 跨 scope 的 canonical 成员目录
- seat / permission / organization roster

### Tab E：Bindings

目标：

- 让团队页能诚实显示 Team 实际依赖了哪些外部能力与治理规则

内容：

- scope default binding
- service bindings
- connectors
- secrets
- policies
- endpoint exposure 摘要

后端来源：

- `GET /api/scopes/{scopeId}/binding`
- `GET /api/scopes/{scopeId}/services/{serviceId}/bindings`
- `GET /api/services/{serviceId}/endpoint-catalog`
- `GET /api/services/{serviceId}/policies`

### Tab F：Assets

目标：

- 让 Builder 能看到当前 Team 下有哪些 workflow/script 资产，而不用先进入 Studio

内容：

- scope workflows
- scope scripts
- workflow detail quick preview
- script revision / catalog 摘要

后端来源：

- `GET /api/scopes/{scopeId}/workflows`
- `GET /api/scopes/{scopeId}/scripts`
- `GET /api/workflow-catalog`
- `GET /api/workflows/{workflowName}`

### Tab G：高级编辑

目标：

- 让高级用户带着明确 Team 上下文进入 Studio，而不是脱离上下文跳过去

行为：

- 深链到 Studio
- 必须显式带 `scopeId`
- 若进入 workflow/script 编辑，也应尽量保留当前 Team 上下文

---

## 9. Platform 层页面定义

## 9.1 Governance

聚焦：

- service bindings
- endpoint catalog
- policies
- activation capability

它解决的问题是：

- 为什么这个 Team 能调用某个外部系统
- 哪些 binding 缺失导致 activation 失败
- 哪些 policy 阻止了请求进入生产服务

## 9.2 Services

聚焦：

- service list / detail
- revisions
- implementation kind
- primary actor
- deployment status

它是 Team 页“成员/绑定”下钻后的权威对象页。

## 9.3 Deployments

聚焦：

- deployments
- serving targets
- rollout stages
- traffic view

它回答的问题是：

- 当前到底谁在 serving
- rollout 卡在哪一阶段
- 流量分配是否异常

## 9.4 Topology

定位：

- 专家工具
- 平台级拓扑或 Actor 级追查入口

要求：

- 保留，但不再承担用户层主叙事
- 任何 Team 层进入这里，都应带明确上下文跳转

---

## 10. 核心用户流

## 10.1 理解当前 Team

```text
Open Console
  -> /teams
  -> 看见当前 Team 与当前绑定状态
  -> 点击进入 /teams/:scopeId
  -> 在 Overview 理解当前 Team 是否正常
```

## 10.2 测一次并看结果

```text
/teams/:scopeId
  -> 测试对话
  -> 进入活动视图
  -> 实时看到 run / step / text / tool 事件
  -> 若失败，继续下钻 audit / topology / service
```

## 10.3 处理挂起 run

```text
/teams/:scopeId
  -> Overview 或 Activity 看到 waiting_signal / human_input
  -> resume / signal
  -> 继续观察事件流直到完成
```

## 10.4 从用户层下钻到平台层

```text
/teams/:scopeId
  -> 成员 / Bindings
  -> 点击 governance serviceId 或 actorId
  -> 进入 /services /governance /deployments /runtime/explorer
```

## 10.5 从团队上下文进入高级编辑

```text
/teams/:scopeId
  -> 高级编辑
  -> /studio?...scopeId=xxx
  -> 查看或修改 workflow / script
```

---

## 11. 数据映射表

| 前端能力 | V1 数据源 |
|---|---|
| 当前 Team 入口 | `GET /api/studio/context` |
| 当前 binding 状态 | `GET /api/scopes/{scopeId}/binding` |
| Team runs 列表 | `GET /api/scopes/{scopeId}/runs` |
| Team run audit | `GET /api/scopes/{scopeId}/runs/{runId}/audit` |
| run 干预动作 | `resume / signal / stop` scope-service endpoints |
| 事件拓扑 | `GET /api/actors/{actorId}/graph-enriched` |
| actor 当前态 | `GET /api/actors/{actorId}` |
| actor timeline | `GET /api/actors/{actorId}/timeline` |
| workflow catalog / capabilities | `/api/workflow-catalog`、`/api/workflows/{name}`、`/api/capabilities` |
| Team 资产 | `GET /api/scopes/{scopeId}/workflows`、`GET /api/scopes/{scopeId}/scripts` |
| Team 聊天历史 | `GET /api/scopes/{scopeId}/chat-history` |
| Services | `/api/services*` |
| Governance | `/api/services/{serviceId}/bindings`、`/endpoint-catalog`、`/policies` |
| Deployments / Traffic | `/api/services/{serviceId}/deployments`、`/serving`、`/rollouts`、`/traffic` |

---

## 12. 非目标

本轮明确不做：

1. 新建任何后端 contract 来“配合设计稿”
2. 假的多团队运营大盘
3. 假的全局健康 KPI
4. 伪造“成员在线率”“全局消息量”之类没有稳定事实源的指标
5. 将 `Chat` 重新抬回一级导航
6. 将 `Studio` 继续当成普通业务用户主入口
7. 在前端单独维护第二套 runtime truth
8. query-time replay 或前端拼装 shadow state

---

## 13. Success Criteria

### 13.1 产品认知

1. 新用户在 10 秒内理解“这是 AI Team 运营工作台，而不是工程对象菜单”
2. Teams 层不再暴露 `Primitives / GAgents / Invoke Lab / Mission Control` 这类工程术语

### 13.2 主任务完成度

1. 用户能从 `/teams/:scopeId` 完成“看状态 -> 测一次 -> 看活动 -> 处理挂起”闭环
2. 高级用户能从团队页一跳进入对应 `Service / Governance / Studio`

### 13.3 事实一致性

1. 团队页所有关键状态都来自现有 query/read model/API
2. 不因为缺数据而伪装成 healthy
3. 所有“成员/连接器/状态”文案都与真实后端语义对齐

---

## 14. 分阶段实施

## P0：导航重构

- 收敛一级导航到 `Teams / Platform / Settings`
- `Chat / Studio / Runs / Mission Control` 全部降级

## P1：当前 Team 入口页

- 交付 `/teams`
- 默认按 current-scope-first 实现
- 只有在确有 scope catalog 来源时才扩展多团队视图

## P2：Team Workbench 壳层

- 交付 `/teams/:scopeId`
- 先打通 `Overview / Activity / Topology`

## P3：Bindings / Assets / 高级编辑

- 交付 `Members / Bindings / Assets / Advanced Edit`
- 建立 Team 到 Platform 的稳定跳转关系

## P4：Platform 叙事收口

- 让 `Services / Governance / Deployments / Topology` 各司其职
- 减少与 Teams 层的概念重叠

---

## 15. 待确认问题

1. 当前 Console 是否已经在别处拥有可复用的 scope catalog 来源。
2. `/runtime/explorer` 现有前端是否足够承担 Platform `Topology` 的专家入口。
3. 团队页里的“成员”最终是否以 `Participants` 文案落地，还是保留 `成员` 但在实现语义上按参与者处理。

这 3 个问题都不阻塞本轮 PRD 成立，但会影响具体前端实现细节。

---

## 16. 最终结论

本次重写后的产品方向可以概括为：

- 设计稿的方向是对的：`Teams + Platform`
- preview 的界面气质是对的：先 Team、后 Platform、用 Team Detail 做桥梁
- 但 V1 不能照搬 preview 里的指标与实体定义
- 真正可落地的版本应该是：

> 一个以 `current-team-first` 为入口、以 `Team Workbench` 为核心、以 `Platform service/governance/deployment` 为下钻面的 AI Teams Console

这版定义既保留了参考稿最好的产品判断，也不会越过当前后端真实能力边界。
