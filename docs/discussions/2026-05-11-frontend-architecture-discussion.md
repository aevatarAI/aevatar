---
title: "Console Web 前端设计讨论"
status: draft
owner: potter
last_updated: 2026-05-11
---

# Console Web 前端设计讨论

## 1. 结论

当前 [前端设计基线](../canon/frontend-design.md) 的方向是合理的，而且比普通 UI 规范更接近 Aevatar 真正需要的东西：它没有只讨论颜色、字体和卡片，而是把 `Team / Member / Service / Run / Governance` 这些产品主语和 `Command / Observation / ReadModel` 这些架构语义放进了前端规则里。

这很关键。Aevatar Console Web 不能只是一个后台管理系统。它应该帮助用户理解和运营一个 AI Team。

但这份规范还需要通过一个团队讨论稿继续补强。原因是：规范回答的是“以后前端应该遵守什么规则”，团队还需要回答“我们为什么这样做、先做哪几页、用哪些后端事实源、怎么判断做对了”。

本讨论稿的建议是：

1. 把 Console Web 明确定位成 `AI Team Workbench`，而不是对象浏览器。
2. 让前端围绕用户任务组织页面，而不是围绕后端对象平铺页面。
3. 把 `Command 已受理`、`Observation 正在推进`、`ReadModel 已物化` 做成用户能看懂的状态。
4. 把 Team、Member、Service、Run 的职责切开，避免一个页面同时扮演编辑器、调试器、治理台和审计台。
5. 先做少数关键闭环：Team 状态理解、Member 构建调用、Service 调用观察、Run 诊断。

## 2. 设计原则：从用户工作流出发，不从后端对象出发

后端有 Team、Member、Service、Workflow、Script、Run、Governance 这些概念。但用户打开 Console Web 的时候，脑子里想的不是这些词。

用户进来只有三种情况：

### 情况 A："我要做一个 AI 能力"

他需要的是：选一个 Team → 选一个 Member → 写/改实现 → 绑定 → 发一次 → 看结果。

这是一条**线性工作流**，不是五个独立页面。

### 情况 B："出事了，我要看看怎么了"

他需要的是：看到哪个 Team/Run 有问题 → 看失败在哪一步 → 看能不能介入 → 复制信息给同事。

这是一条**诊断工作流**，起点是异常信号，不是导航菜单。

### 情况 C："我要管一个已上线的能力"

他需要的是：看 Service 状态 → 看版本/流量/策略 → 改绑定或策略 → 确认生效。

这是一条**治理工作流**，核心是"当前状态是什么"和"我改了之后生效了吗"。

### 核心原则

**让每条工作流在一个屏幕内走完，而不是让用户在五个页面之间跳转。**

具体来说：

1. **第一屏不应该是"列表"，应该是"当前上下文 + 待办"。** 用户打开 Console Web，第一屏应该回答：我在哪个 Team？有没有正在运行的、等待我介入的、刚失败的？我上次做到哪一步了？列表本身就是状态面板，不是"名称 + 描述"的目录。

2. **核心页面不是"对象详情页"，而是"工作台"。** Workbench 的核心是"我正在做的事"，不是"这个对象的属性"。Team Workbench 的主区域应该是当前运行状态和待处理事项，Topology 是下钻能力，不是首页主视觉。

3. **"Service" 不应该是独立的列表页，而是 Member 发布后的自然延伸。** 用户从 Team → Member → Build → Bind → 发布 → 进入 Service Workbench。Platform Owner 需要的全局 Service 视图是平台治理的一部分，不是产品主体验。

4. **"Runs" 不应该是独立页面，应该是每个工作台的一部分。** Run Diagnostics 是一个共享视图，可以从任何工作台打开。高级 Operator 需要的全局 Run 视图是高级诊断入口，不是主体验。

5. **后端的 Team / Member / Service / Run / Governance 是事实模型，不是页面模型。** 前端应该把事实模型翻译成用户的工作流，而不是把事实模型平铺成列表页。

以上三种情况覆盖了 Builder、Operator、Platform Owner 三类主要用户。第四类用户 Frontend / Backend Contributor 的需求（知道哪些事实源可用、哪些状态来自哪里、PR 应该跑什么校验）由规范文档本身服务，不需要独立的工作流入口。

### 翻译到具体页面

| 用户场景 | 当前做法 | 理想做法 |
|---|---|---|
| "我要做一个 AI 能力" | 跳 Teams → Team Detail → Members → Studio → 执行 → 回到 Runs 看结果 | Team Workbench 内完成：选 Member → Build → Bind → Invoke → Observe，不离开当前页面 |
| "出事了" | 跳到 Runs 页 → 找到 run → 看 timeline → 看错误 | Team 首页有异常信号入口，点击直接进入 Run Diagnostics，诊断完成后可跳回 Member 修改 |
| "我要管一个已上线的能力" | 跳 Services → 详情 → Deployments → Governance | Service Workbench 内完成：看 contract → 看 serving/traffic → 改 policy/binding → 确认生效 |
| "我要看整个平台" | Platform 分组下有 5 个独立入口 | Platform Governance 作为高级入口保留，但主体验从 Teams 开始 |

## 3. 产品视角 Review

### 3.1 当前规范合理的地方

当前 `docs/canon/frontend-design.md` 最有价值的部分不是视觉规则，而是产品语义规则。

它明确了：

1. Console Web 的主语是 `AI Team Workbench`。
2. 页面模型应收敛到 `Team Workbench / Member Workbench / Service Workbench / Platform Governance / Run Diagnostics`。
3. 前端不能把 `HTTP 200`、`accepted receipt`、`running`、`observed`、`completed` 混成一个成功态。
4. Query 只读 readmodel，Observation 只表达运行过程，Raw event 只是高级调试视图。
5. 前端不能依赖 `actorId` 前缀、C# 类型名、`EventEnvelope` 内部字段或日志文案。
6. 视觉规范要求 token、交互状态、响应式、空态、stale 态和 paused 态，这些都服务真实使用，而不是只服务截图。

这是正确方向。

### 3.2 还需要补强的地方

当前规范仍然偏“规则”。团队讨论时还需要补齐以下内容：

| 缺口 | 为什么重要 | 建议补充 |
|---|---|---|
| 用户角色不够明确 | Builder、Operator、Platform Owner 的任务不同 | 明确每类用户进入 Console 后最常做的 3 件事 |
| 页面优先级不够明确 | 全部页面都重要，就等于没有优先级 | 给 Team、Member、Service、Run 排 P0/P1/P2 |
| 数据契约矩阵不足 | 前端容易为了视觉完整性拼假数据 | 每个页面列出必须读取的 API、readmodel、observation |
| 成功指标不足 | 团队很难判断设计是否变好 | 为每个工作台定义可验证的体验指标 |
| 组件策略不足 | Observation、状态条、诊断面板会重复造轮子 | 定义共享组件和禁止重复实现的范围 |
| 权限和降级状态不足 | 企业控制台最常见的问题不是空态，而是无权限、过期、延迟、部分失败 | 补充权限、stale、partial、unsupported endpoint 的 UI 规则 |

## 4. 用户与核心任务

前端设计应该从用户任务出发，不从后端对象出发。

### 4.1 Builder

Builder 想完成的是：

1. 创建或选择一个 Team。
2. 找到某个 Member。
3. 修改 workflow、script 或 GAgent 实现。
4. 绑定并发布。
5. 立刻发一次真实 invoke。
6. 看结果、看 timeline、看失败原因。

Builder 不应该被迫理解 `serviceId`、`actorId`、`EventEnvelope` 或投影内部字段。

### 4.2 Operator

Operator 想完成的是：

1. 看当前 Team 是否还在运行。
2. 看最近 run 是否失败、暂停或等待人工介入。
3. 处理 human input、approval、signal、stop、retry。
4. 打开 run diagnostics 找到失败步骤。
5. 复制 runId、commandId、错误信息或 trace 链接给工程同学。

Operator 最怕的是页面显示“成功”，但实际只是 command accepted。这个会直接误导操作。

### 4.3 Platform Owner

Platform Owner 想完成的是：

1. 看 Service 的 contract、revision、deployment、serving、traffic。
2. 管理 binding、policy、endpoint exposure。
3. 确认某次发布有没有进入正确 serving set。
4. 从 Service 下钻到一次具体 run。

Platform Owner 需要密集、稳定、可筛选的信息，不需要营销式大卡片。

### 4.4 Frontend / Backend Contributor

贡献者想完成的是：

1. 知道一个页面允许读取哪些事实源。
2. 知道哪些状态必须从 API、readmodel、observation 或本地 UI 推导。
3. 知道不能为了体验绕过 readmodel 或解析 actorId。
4. 知道 PR 提交前应该跑哪些校验。

这类用户需要的是规则和边界。规范文档应该服务他们。

## 5. 前端应该怎么服务用户

### 5.1 把架构复杂度翻译成用户语言

Aevatar 的后端架构很强，但用户不应该被迫直接消费架构术语。

前端应该这样翻译：

| 架构事实 | 用户语言 |
|---|---|
| Command accepted | 请求已受理 |
| Observation streaming | 正在运行，收到实时进展 |
| ReadModel observed | 已观察到最新可查询状态 |
| stateVersion | 已观察到版本 |
| actorId | 运行实体地址 |
| EventEnvelope raw payload | 原始事件，仅用于调试 |
| Projection lag | 数据可能稍有延迟 |

这个翻译层是前端的核心价值。否则 Console Web 只是把后端对象倒出来。

### 5.2 把一次工作闭环放在一个屏幕里

用户最常见的闭环不是“浏览一个列表”，而是：

1. 选择上下文。
2. 发起动作。
3. 观察过程。
4. 判断结果。
5. 失败时诊断。
6. 需要时治理或修改。

所以高频页面应该是 workbench，而不是 CRUD 列表。

### 5.3 不伪装强一致

如果后端只返回 accepted，页面就只能显示 accepted。

如果 readmodel 可能落后，页面就应该显示 `refreshedAt`、`stateVersion` 或“仍在处理”。

如果 observation 断开，页面不能假装 run 完成。应该显示“实时连接已断开，最后收到事件时间为 X，可刷新 readmodel 或打开 Run Diagnostics”。

这个诚实性会让产品看起来更复杂一点，但它会减少误判。对开发者工具来说，这是好事。

### 5.4 让 Raw 成为最后一层

普通用户的观察顺序应该是：

1. Summary
2. Response
3. Timeline
4. Trace / Tool Calls
5. Raw

Raw event 很重要，但不能成为默认解释路径。否则用户会觉得产品只做了一半。

## 6. 页面 Review 与建议

### 6.1 Teams

`/teams` 和 Team Detail 应该是 Console Web 的第一主屏。

当前合理点：

1. 路由已经把 `/overview` 和 `/` 重定向到 `/teams`。
2. Team 已经是主导航入口。
3. Team Detail 已经有 detail route，适合承接 workbench。

需要优化：

1. Team 列表页不应只像对象目录，应展示每个 Team 的可验证状态摘要。
2. Team Detail 不应只是 tab 管理页，应升级成运行工作台。
3. Team 页面应内置最近 run、activity、paused/intervention、members、bindings 的关系。
4. 不要展示没有契约支持的全局健康、组织级 KPI 或跨 team analytics。

建议 P0：

1. Team Header 固定显示 `Team / Scope / Member count / Last observed / Pending action`。
2. Activity 区域复用 Run/Observation 摘要。
3. Paused run 必须有一等入口，例如 human input、approval、signal。
4. Topology 和 Bindings 是下钻，不是首页主视觉。

### 6.2 Studio / Member Workbench

Studio 的正确主语是 `Member`，不是 workflow、script、GAgent 的资产集合。

当前合理点：

1. ADR-0016 已经锁定 `scope -> member -> implementation -> published service -> endpoint -> run`。
2. `docs/2026-04-27-member-first-studio-apis.md` 已经给出 member-first route。
3. `docs/design/2026-04-20-studio-member-workbench-prd.md` 已经把 Studio 定义成 `Build -> Bind -> Invoke -> Observe`。

需要优化：

1. `/studio` 目前是隐藏页面，这会让高频 Builder 工作流不够可发现。
2. Studio 和 Members 页的关系需要产品决策：是合并，还是保持 Members 为 roster，Studio 为 selected member workbench。
3. URL 和 UI 必须明确当前 `scopeId / teamId / memberId`，不能让用户不知道自己在改谁。

建议 P0：

1. 从 Team Detail 的 member roster 进入 Studio，并携带 `scopeId/teamId/memberId`。
2. Studio 左 rail 永远是当前 Team 的 members，不是资产类型导航。
3. Build、Bind、Invoke、Observe 的每一步都显示当前 member 和 published service contract。
4. 普通用户不输入 `serviceId`，只看到只读 contract metadata。

### 6.3 Services / Service Workbench

Service 页面应该从列表升级为 Service Workbench。

当前合理点：

1. 后端已经有 service catalog、revision、deployment、rollout、serving、traffic、binding、policy 等 readmodel。
2. `/services`、`/deployments`、`/governance` 已经存在，说明平台层能力是成熟方向。

需要优化：

1. Service 生命周期被拆在多个页面，用户很难理解一个 service 的完整状态。
2. Invoke 能力分散在 Chat、Scope Invoke、Runs 等页面，Service 下缺少一次真实请求闭环。
3. Governance 的 policy/binding/endpoint 和 Service 的关系需要更直接。

建议 P1：

1. `/services/:serviceId` 做成 Service Workbench。
2. 顶部显示 `Contract / Invoke / Observe / Govern`。
3. Invoke 面板必须有 actual request preview，且 preview 等于真实发送 payload。
4. Deployments 和 Governance 可以保留独立入口，但应能从 Service Workbench 下钻或回跳。

### 6.4 Runs / Run Diagnostics

Run Diagnostics 是跨页面诊断工具，不应只被理解为 Event Stream。

当前合理点：

1. `/runtime/runs` 已经是较完整的 observation 入口。
2. 后端存在 timeline、audit、insight report 等能力。

需要优化：

1. Runs 页应该成为所有工作台的诊断落点。
2. Team、Member、Service 页打开某个 run 时，应进入同一套诊断视图。
3. Run 状态必须区分 accepted、running、streaming、paused、observed、completed、failed。

建议 P0：

1. 抽取共享 `Observation Panel`。
2. 抽取共享 `Run Status Rail`。
3. 每个 run detail 支持复制 `runId / commandId / correlationId / error / trace link`。
4. Paused 状态必须显示等待什么，以及用户能做什么。

### 6.5 Governance / Deployments

Governance 和 Deployments 是 Platform Owner 的工作台，不是 Team 的主体验。

当前合理点：

1. 这些页面继续作为独立入口是合理的。
2. 它们信息密度可以更高，更接近传统控制台。

需要优化：

1. 需要从 Service Workbench 建立上下文跳转。
2. Policy、Binding、Endpoint、Traffic 的关系要可解释。
3. 异步动作必须有 loading、防重复提交、失败原因和回滚解释。

建议 P1：

1. Governance 页保留平台全局视角。
2. Service Workbench 内嵌该 Service 的 governance summary。
3. 用户从全局 Governance 点进具体对象后，应能回到对应 Service。

## 7. 导航建议

当前导航结构大体可用，但需要围绕任务重新解释。

建议的一级导航：

| 入口 | 定位 | 说明 |
|---|---|---|
| `Teams` | 运行与协作入口 | 默认首页 |
| `Studio` | Member Workbench | 可作为一级入口，但必须要求选择 Team/Member |
| `Services` | 已发布能力入口 | 从列表进入 Service Workbench |
| `Runs` | 诊断入口 | 可以保留在 Platform 或提升为一级，取决于用户是否高频排障 |
| `Governance` | 平台治理 | policy、binding、endpoint |
| `Deployments` | 发布与流量 | 可以保留，也可以并入 Services 的平台分组 |
| `Settings` | 用户配置 | 不变 |

不建议把所有隐藏页面都提升为一级入口。`Workflows / Scripts / GAgents / Connectors` 更适合作为 Member、Service 或 Governance 内的子对象。

真正要讨论的是 `Studio` 和 `Runs`：

1. 如果目标用户主要是 Builder，`Studio` 应提升为一级入口。
2. 如果目标用户主要是 Operator，`Runs` 或 `Operations` 应更可见。
3. 如果目标是统一团队体验，入口仍应从 `Teams` 开始，Studio 和 Runs 作为上下文动作。

## 8. 数据契约矩阵

每个页面都应该声明自己的事实源。没有事实源，就不要展示假状态。

| 页面 | 可展示事实 | 来源 |
|---|---|---|
| Team List | team identity、lifecycle、member count | Team readmodel |
| Team Detail | roster、activity、bindings、recent run summary | Team readmodel、member readmodel、run readmodel |
| Studio | selected member、implementation、binding、published service、member runs | member-first APIs、script/workflow readmodel、run readmodel |
| Service Workbench | contract、revision、deployment、traffic、policy、invoke result | service readmodels、invoke observation、run readmodel |
| Run Diagnostics | timeline、trace、summary、response、audit、errors | observation、run insight report、audit readmodel |
| Governance | policies、bindings、endpoints | governance/service configuration readmodels |
| Deployments | deployment、rollout、serving set、traffic | service deployment readmodels |

每个组件也应该知道自己的数据层级：

| UI 元素 | 数据层级 |
|---|---|
| form draft | Local UI |
| submit receipt | Command receipt |
| live token / step event | Observation |
| final summary after refresh | ReadModel |
| raw payload | Debug only |

## 9. 前端实现建议

### 9.1 共享组件

建议优先抽取这些共享组件：

1. `WorkbenchContextHeader`
2. `ObservationPanel`
3. `RunStatusRail`
4. `ReadModelFreshnessBadge`
5. `CommandReceiptBanner`
6. `PausedInterventionPanel`
7. `ActualRequestPreview`
8. `TraceCopyMenu`

这些组件不要绑定某个页面。它们应该服务 Team、Member、Service、Run 四类工作台。

### 9.2 状态模型

前端应把状态统一成四层：

1. `localDraft`
2. `commandReceipt`
3. `observation`
4. `readModel`

页面展示时再映射为：

1. `LocalDraft`
2. `Accepted`
3. `Running`
4. `Streaming`
5. `Paused`
6. `Observed`
7. `Completed`
8. `StillProcessing`
9. `Failed`

不要让每个页面各自发明一套状态名。

### 9.3 React Query 约束

建议统一 query key 形态：

1. `["team", scopeId, teamId]`
2. `["team", scopeId, teamId, "members"]`
3. `["member", scopeId, teamId, memberId]`
4. `["service", serviceId]`
5. `["service", serviceId, "runs"]`
6. `["run", runId]`

SSE/WebSocket 到达后，可以更新当前屏幕的临时 observation state，但页面恢复、刷新、分享和历史回看应回到 readmodel/query。

### 9.4 视觉系统

当前规范中关于 token、AlibabaSans、禁止模板化 SaaS 卡片墙的方向可以保留。

但落地时要注意：

1. Console Web 是工具，不是 landing page。
2. Team 和 Service 的首屏要高密度可扫描。
3. 视觉记忆点应该来自工作台结构、状态轨、timeline 和运行反馈，不是装饰性渐变。
4. 移动端至少要能看状态、处理暂停、复制诊断信息。

## 10. 后端配合建议

这些不是要求后端为了前端临时造假，而是把已有架构能力用更清晰的 DTO 暴露出来。

建议优先补齐：

1. Team scoped recent activity query。
2. Team/member scoped run summary query。
3. 每个 readmodel 返回 `stateVersion` 或 `refreshedAt`。
4. Run summary 中明确 `status / pausedReason / nextActions / lastObservationAt`。
5. Service detail 聚合 DTO，至少聚合 catalog、latest revision、serving、traffic、policy summary。
6. Endpoint capability DTO，明确某 endpoint 是否支持 streaming invoke。

不建议：

1. 让前端 query-time replay event store。
2. 让前端解析 actorId 字符串。
3. 用后端日志文案当 UI 状态。
4. 为了做 dashboard 指标先加没有权威事实源的伪字段。

## 11. 需求优先级

### P0：先让核心闭环诚实可用

1. Team Detail 显示真实 roster、recent runs、paused/intervention、bindings。
2. Studio 固化 Member Workbench，入口从 Team Member 进入。
3. Run Diagnostics 统一状态与 observation 展示。
4. 前端状态模型区分 command、observation、readmodel。
5. 文档和 PR checklist 明确禁止假事实源。

### P1：把 Service Workbench 做成闭环

1. Service detail 聚合 contract、invoke、observe、govern。
2. Invoke 支持 actual request preview。
3. Request history 只展示真实请求记录或明确标注本地 session。
4. Governance、Deployments 与 Service 建立上下文互跳。

### P2：提升规模化运营体验

1. Team scoped activity inbox。
2. 跨 run 比较。
3. 更完整的 platform topology。
4. 组织级 analytics，前提是后端有明确权威事实源。

## 12. 成功指标

团队可以用这些问题判断设计是否变好：

1. 新用户能否在 3 分钟内理解当前 Team 正在发生什么。
2. Builder 能否从 Team member 进入 Studio，并完成一次 build、bind、invoke、observe。
3. Operator 能否在不看 Raw event 的情况下判断一次 run 失败在哪里。
4. Platform Owner 能否从 Service 看清 revision、deployment、traffic、policy 的关系。
5. 页面刷新后，用户是否仍能通过 readmodel 恢复稳定状态。
6. 前端 PR 是否能明确说明每个状态来自 API、readmodel、observation 还是 local UI。

## 13. 团队讨论议题

建议会议只讨论这些决策，不要散开：

1. 是否认同"从用户工作流出发，不从后端对象出发"作为 Console Web 的设计原则？如果认同，§2 的三种用户场景是否完整？
2. `Studio` 是否提升为一级导航，还是保持从 Team 上下文进入？
3. `Runs` 是否作为一级诊断入口，还是保留在 Platform 分组？
4. Team Detail 第一版是否要从 tab 管理页改成 workbench 分栏？
5. Service detail 是否升级为 Service Workbench，Deployments/Governance 是否保持独立入口？
6. `ObservationPanel` 是否作为前端共享基础设施优先抽取？
7. readmodel freshness 是否要求所有关键页面统一展示？
8. 哪些指标明确不做，直到后端有权威事实源？

## 14. 附录：当前导航快照

当前 `apps/aevatar-console-web/config/routes.ts` 中，主要侧边栏入口是：

```text
Teams          -> /teams
Platform
  Services     -> /services
  Governance   -> /governance
  Deployments  -> /deployments
  Topology     -> /runtime/explorer
  Event Stream -> /runtime/runs
Settings       -> /settings
```

以下页面目前隐藏在侧边栏外：

1. `/studio`
2. `/runtime/workflows`
3. `/runtime/primitives`
4. `/chat`
5. `/runtime/mission-control`
6. `/runtime/gagents`
7. `/scopes/invoke`
8. `/scopes/overview`
9. `/scopes/assets`

这些隐藏页面不一定都应该提升。更合理的方式是把它们归位到 Team、Member、Service 或 Platform 的上下文里。

## 15. 附录：当前 ReadModel 与页面对照

### Studio / Team / Member

| ReadModel | 前端消费 | 说明 |
|---|---|---|
| `StudioTeamCurrentStateDocument` | Team pages | Team 列表和详情 |
| `StudioMemberCurrentStateDocument` | Members / Studio | Member 列表和当前 member |
| `UserConfigCurrentStateDocument` | Settings | 用户配置 |
| `ConnectorCatalogCurrentStateDocument` | Connectors / Governance | 连接器目录 |
| `RoleCatalogCurrentStateDocument` | 待定 | 角色目录 |
| `ChatConversationCurrentStateDocument` | Chat | 对话状态 |
| `ChatHistoryIndexCurrentStateDocument` | Chat | 对话历史 |
| `GAgentRegistryCurrentStateDocument` | Members / Studio | GAgent 注册表 |

### Workflow / Run

| ReadModel | 前端消费 | 说明 |
|---|---|---|
| `WorkflowRunInsightReportDocument` | Runs | 完整执行报告 |
| `WorkflowExecutionCurrentStateDocument` | Runs | 执行当前状态 |
| `WorkflowRunTimelineDocument` | Runs | 执行时间线 |
| `WorkflowRunGraphArtifactDocument` | Topology Explorer | 图拓扑 |
| `WorkflowActorBindingDocument` | Team Detail bindings | Actor 绑定 |

### Service

| ReadModel | 前端消费 | 说明 |
|---|---|---|
| `ServiceCatalogReadModel` | Services | Service 目录 |
| `ServiceRevisionCatalogReadModel` | Services detail | Revision 列表 |
| `ServiceDeploymentCatalogReadModel` | Deployments | 部署目录 |
| `ServiceRolloutReadModel` | Deployments | Rollout 状态 |
| `ServiceServingSetReadModel` | Deployments | Serving 目标 |
| `ServiceTrafficViewReadModel` | Deployments | 流量分布 |
| `ServiceRunCurrentStateReadModel` | Runs / Service | Run 状态 |
| `ServiceConfigurationReadModel` | Governance | 治理配置 |

### Scripting

| ReadModel | 前端消费 | 说明 |
|---|---|---|
| `ScriptReadModelDocument` | Studio | 脚本状态 |
| `ScriptCatalogEntryDocument` | Studio | 脚本目录 |
| `ScriptEvolutionReadModel` | Studio | 演化提案 |
| `ScriptDefinitionSnapshotDocument` | 待定 | 定义快照 |
