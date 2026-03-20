# Aevatar Console 前端迭代清单（不改后端前提）

更新时间：2026-03-12  
状态：Active  
范围：`apps/aevatar-console-web`  
约束：**不改 Aevatar 主后端能力，只使用当前已有 API**

## 1. 目标

在不改动主后端的前提下，继续推进 `Aevatar Console Web`，把当前已可用的 MVP 收敛成更稳定、更高效的运行控制台。

本清单只关注：

1. 前端页面深化
2. 现有后端接口的更充分消费
3. 页面联动、状态表达、交互效率
4. 前端测试、布局、打包与可维护性

本清单**不包含**以下事项：

1. `run-centric query` 新接口
2. `run history / run detail` 的正式后端模型
3. 原生 `logs / traces / metrics` 聚合查询后端
4. Workflow 编辑/发布后端
5. `6677` 本地配置能力的完整迁移

## 2. 当前完成情况

### 2.1 已完成

当前前端已具备以下可用模块：

1. `Overview`
   - workflow / actor / capability 总览
   - 快捷入口
   - Grafana 跳转入口

2. `Runs`
   - 基于 `POST /api/chat` 的运行发起
   - AGUI SSE 流式展示
   - `resume / signal` 人工交互
   - 预设 workflow 场景
   - 最近一次本地运行恢复

3. `Actors`
   - actor snapshot
   - timeline
   - graph-enriched
   - 图过滤基础参数

4. `Workflows`
   - workflow library
   - workflow detail
   - YAML / roles / steps / graph 查看

5. `Settings`
   - 项目内控制台偏好设置
   - `6677` 健康检查
   - 打开本地配置 UI 按钮

### 2.2 未完成

当前仍未完成的能力主要有：

1. `Runs`
   - WebSocket 模式未接入
   - 事件流过滤与检索较弱
   - 当前运行态缺少更强的分区表达

2. `Actors`
   - 未接 `/graph-edges`
   - 未接 `/graph-subgraph`
   - 图节点/边详情面板不足
   - timeline 过滤能力不足

3. `Workflows`
   - 缺少更细粒度过滤
   - 缺少 graph / roles / steps 联动
   - 缺少从 workflow 直接发起 run 的高效入口

4. `Overview / Settings`
   - 首页聚合能力偏薄
   - 观测入口仍是弱集成
   - 本地配置工具只做了健康检查与跳转

5. 工程侧
   - 页面行为测试覆盖仍不够
   - 打包体积仍有继续压缩空间
   - 页面空状态/错误态/密度还可继续统一

## 3. 现有后端可直接复用的能力

不改后端前提下，可继续消费的主接口：

1. `POST /api/chat`
2. `GET /api/ws/chat`
3. `POST /api/workflows/resume`
4. `POST /api/workflows/signal`
5. `GET /api/agents`
6. `GET /api/workflows`
7. `GET /api/workflow-catalog`
8. `GET /api/capabilities`
9. `GET /api/workflows/{workflowName}`
10. `GET /api/actors/{actorId}`
11. `GET /api/actors/{actorId}/timeline`
12. `GET /api/actors/{actorId}/graph-edges`
13. `GET /api/actors/{actorId}/graph-subgraph`
14. `GET /api/actors/{actorId}/graph-enriched`

结论：

- `Runs / Actors / Workflows / Overview / Settings` 仍有明显前端深化空间
- 当前优先级应放在“把已有接口吃透”，而不是先补新后端

## 4. 前端任务优先级

### P0

1. `Runs` 接入 WebSocket 模式，补运行通道切换。
2. `Runs` 增强事件流：分类过滤、错误聚焦、长 payload 折叠。
3. `Runs` 增强人工交互面板：等待态、当前 step、操作反馈更明确。
4. `Actors` 接入 `/graph-edges` 与 `/graph-subgraph`，补图视图切换。
5. `Actors` 增加节点/边详情面板。
6. `Workflows` 增加从 workflow 直接发起 run 的入口。

### P1

1. `Overview` 做成真正的运行首页：最近使用、推荐场景、状态提醒。
2. `Workflows` 增加按 `group / source / requiresLlm / primitive` 的筛选。
3. `Workflows` 增加 step / role / graph 联动高亮。
4. `Actors` 增加 timeline 过滤：`stage / eventType / stepType`。
5. `Overview / Runs / Actors / Workflows` 增加更多深链接联动。

### P2

1. 新增轻量 `Observability` 页面，仅做外部观测入口聚合。
2. 统一空状态、错误态、加载态。
3. 统一卡片高度、摘要密度、滚动区节奏。
4. 按页懒加载和拆包，收敛 chunk 体积。
5. 补页面关键路径测试。

### P3

1. 继续优化 `Settings` 的非侵入能力。
2. 只读方式扩展 `6677` 状态摘要。
3. 补键盘快捷操作、复制、快速跳转等效率功能。

## 5. 可执行迭代任务

## Iteration 1：Runs 深化

目标：把 `Runs` 从“能跑”提升到“能稳定调试”。

任务：

1. 为 `Runs` 增加 `SSE / WebSocket` 运行通道切换。
2. 为事件流增加过滤器：
   - `message`
   - `tool`
   - `human_input`
   - `human_approval`
   - `wait_signal`
   - `error`
3. 对长事件 payload 增加折叠与展开。
4. 强化当前运行摘要：
   - active step
   - current actor
   - current commandId
   - waiting state
5. 调整消息流与事件流布局，保证长时间运行时页面仍可读。
6. 为 `resume / signal` 表单增加更清晰的当前上下文提示。

交付物：

1. `Runs` 页面交互增强完成
2. `SSE / WS` 两种运行模式都可用
3. 事件流过滤和折叠可用

验收标准：

1. 能从 UI 选择运行通道
2. 能明显区分等待输入、等待审批、等待 signal、错误结束
3. 长事件 payload 不再直接撑爆页面

## Iteration 2：Actors 深化

目标：把 `Actors` 从“基础查询页”提升到“可调试的运行上下文视图”。

任务：

1. 接入 `/graph-edges`。
2. 接入 `/graph-subgraph`。
3. 增加 `enriched / subgraph / edges` 三种图视图切换。
4. 增加节点详情面板：
   - node id
   - node type
   - updatedAt
   - properties
5. 增加边详情面板：
   - edge id
   - edge type
   - from / to
   - properties
6. 为 timeline 增加筛选器：
   - `stage`
   - `eventType`
   - `stepType`
7. 优化 actor 页面布局，让 `snapshot / timeline / graph` 的主次更清晰。

交付物：

1. Actor 图视图切换完成
2. timeline 过滤可用
3. 节点/边详情面板可用

验收标准：

1. 同一 actor 能在 UI 中切换三种图查询模式
2. 点击图元素可看到结构化详情
3. timeline 可以按事件维度快速缩小范围

## Iteration 3：Workflows 深化

目标：把 `Workflows` 从“查看页”提升到“运行前入口页”。

任务：

1. 增加 library 过滤：
   - keyword
   - group
   - source
   - requiresLlm
   - primitive
2. 增加 step / role / graph 联动。
3. 在 detail 中增加：
   - `Run this workflow`
   - `Run with prompt`
   - `Open in Runs`
4. 强化 YAML / roles / steps / graph 的信息联动。
5. 优化 graph 布局和 tab 层次，减少页面跳动。

交付物：

1. Workflow 筛选器完成
2. 从 workflow 发起 run 的入口完成
3. detail 联动能力完成

验收标准：

1. 可以直接从 workflow detail 跳到 runs 并带上 workflow 参数
2. 可以从 graph 或 step 快速定位相关角色和配置
3. library 在 workflow 数量增大后仍可高效筛选

## Iteration 4：Overview / Settings / Observability

目标：把首页和系统入口补齐成真正的控制台入口层。

任务：

1. `Overview` 增加：
   - 最近使用 workflow
   - 最近访问 actor
   - 常用 run 场景
   - 配置提醒
2. 新增轻量 `Observability` 页面：
   - Grafana
   - Jaeger/Tempo
   - Loki
   - 常用 Explore 链接
3. `Settings` 增加：
   - 项目内偏好说明更清晰
   - `6677` 状态说明更清晰
   - 入口模块收敛
4. 全站统一空状态、错误态、加载态。
5. 补页面之间的深链接：
   - `workflow -> runs`
   - `run -> actor`
   - `actor -> workflow`

交付物：

1. 首页增强完成
2. 观测入口页完成
3. 站内导航联动更顺畅

验收标准：

1. 用户从首页可以快速进入常用运行路径
2. 不需要记 URL 就能进入观测系统
3. 各页面跳转关系自然，不依赖手工输入参数

## 6. 每个迭代的固定验证项

每完成一个迭代，至少执行：

1. `cd apps/aevatar-console-web && pnpm tsc`
2. `cd apps/aevatar-console-web && pnpm test`
3. `cd apps/aevatar-console-web && pnpm build`

若迭代中新增测试或修改测试逻辑，应补充页面级关键交互测试。

## 7. 建议执行顺序

固定顺序如下：

1. `Iteration 1：Runs 深化`
2. `Iteration 2：Actors 深化`
3. `Iteration 3：Workflows 深化`
4. `Iteration 4：Overview / Settings / Observability`

原因：

1. `Runs` 是当前控制台价值最高的主链路。
2. `Actors` 是运行问题定位的直接 drill-down。
3. `Workflows` 是运行前入口和结构化查看层。
4. `Overview / Settings / Observability` 更适合在主链路稳定后再补齐。

## 8. 结论

在不改后端的前提下，当前前端仍有一条明确的高价值推进路线：

1. 先把 `Runs` 做成真正的运行控制台
2. 再把 `Actors` 做成调试入口
3. 然后强化 `Workflows` 的运行前价值
4. 最后补齐首页、设置和观测入口

这条路线不依赖新的主后端能力，适合先把现有控制台打磨到可长期使用的程度。
