---
title: "Aevatar Console Web 产品需求文档（Team-first / Frontend-only）"
status: draft
owner: potter-sun
---

# Aevatar Console Web 产品需求文档（Team-first / Frontend-only）

## 1. 文档目的

这份文档用于重新定义 `apps/aevatar-console-web` 的产品需求，目标不是描述理想中的新平台，而是基于以下三类真实输入，产出一份**可执行、可落地、前端-only** 的产品需求文档：

- 当前 Aevatar 后端与 Console Web 的真实能力
- issue [#146](https://github.com/aevatarAI/aevatar/issues/146)
- PR [#145](https://github.com/aevatarAI/aevatar/pull/145)
- 最近几轮关于产品定位、团队视角、Studio 入口、Connector 语义的讨论

本轮文档的核心目标是：

1. 明确 Aevatar 现在到底是什么产品。
2. 明确 Console Web 本轮重构到底要做什么，不做什么。
3. 把“Team-first”讲法和“后端真实能力”对齐，避免产品文档空转。

## 2. 一句话结论

Aevatar 当前本质上是一个 **多 Agent 应用平台**；  
Console Web 本轮重构的目标，是在**不改后端 contract** 的前提下，把它从“工程控制台”重构成“AI 团队控制台”。

换句话说：

> 这不是一次后端能力开发，而是一次前端产品翻译工程。

## 3. 本轮边界

### 3.1 明确范围

本轮重构明确收敛为 **Frontend-only**：

- 不新增后端 API
- 不改后端数据模型
- 不新增 team-level 聚合能力
- 不调整 `scope-first`、`service`、`revision`、`run`、`audit` 等正式 contract

### 3.2 本轮真正要做的事

- 重写产品叙事
- 重构信息架构
- 重组路由和导航
- 用更接近用户心智的语言重新解释现有能力
- 把 Studio 放回正确的产品位置

### 3.3 本轮不做的事

- 不重新设计 Runtime
- 不实现新的 Team 嵌套数据模型
- 不强行把后端没有的“团队总览聚合接口”做成前端假事实
- 不把 Connector 重建模成 Agent

## 4. 输入对齐：我们采纳什么，不采纳什么

### 4.1 来自 issue #146 / PR #145 的核心采纳点

以下方向被采纳：

- `Scope = Team` 的用户层表达
- `Teams + Platform` 双层信息架构
- `Studio = 团队构建器`，不再作为一级导航
- 团队详情页成为核心页面
- 团队详情中应看到：
  - 团队概览
  - 团队成员
  - 事件拓扑
  - 事件流
  - 高级编辑

### 4.2 基于真实后端能力做出的修正

PR #145 里有一部分描述在产品方向上是对的，但在实现语义上过于乐观。本轮文档做如下修正：

1. `Teams 首页`
   PR 假设存在稳定 `listScopes()`。
   当前代码里并未确认存在这条正式前端/后端主链，因此 V1 不能把“多团队卡片首页”当成硬前提。

2. `团队事件拓扑`
   当前更接近 `actorId -> graph`，不是 `scopeId -> full team topology`。
   因此 V1 必须按“选中成员后查看拓扑”来定义。

3. `团队事件流`
   当前更接近 `scopeId + serviceId + runId -> audit`，不是纯 scope 级总瀑布流。
   因此 V1 必须按“选中 service/run 后查看事件流”来定义。

4. `Connector`
   PR 中“连接器”页方向成立，但本轮文档明确：
   Connector 默认不等于一个团队成员，它更接近外部能力或外部系统。

## 5. 产品定义

### 5.1 Aevatar 是什么

Aevatar 是一个基于虚拟 Actor 的 AI Agent 平台。

它当前提供三类核心构建原语：

- `GAgent`
- `Workflow`
- `Script`

它们都可以被发布成正式 `Service`，并进入：

- 版本治理
- 服务绑定
- 正式运行
- 审计与观测

因此，Aevatar 的本体不是“一个聊天机器人”，而是：

- 可构建的 AI 协作系统
- 可运行的 AI 服务系统
- 可治理的 AI 平台系统

### 5.2 Console Web 是什么

Console Web 不是 Studio 的宿主壳，也不是 Runtime 的纯管理后台。

它应该被定义成：

> 用户进入 Aevatar 后理解、配置、运行、观察 AI 团队的主要控制台。

从这个定义出发，Console Web 要优先回答的是：

- 我有哪些团队
- 这个团队由谁组成
- 他们之间怎么协作
- 最近发生了什么
- 我从哪里进入配置这个团队

而不是先回答：

- Workflow 在哪里
- GAgent 在哪里
- Capability 在哪里
- Runtime Explorer 在哪里

## 6. 产品心智模型

### 6.1 后端真实心智模型

当前后端已经稳定存在的核心对象是：

- `Scope`
- `Service`
- `Revision`
- `Run`
- `Audit`
- `Actor`
- `Actor Graph`
- `Studio capability`

从 Mainnet Host 的正式 README 看，当前运行 contract 已经明确是 `scope-first`。

### 6.2 前端用户层心智模型

为了让用户能理解，前端采用如下映射：

| 后端对象 | 用户层表达 |
|---|---|
| `Scope` | 团队 |
| `Actor / GAgent / RoleGAgent` | 团队成员 |
| `Workflow` | 团队协作流程 |
| `Script` | 团队成员行为 |
| `Service` | 团队能力入口 |
| `Run` | 团队任务 / 一次执行 |
| `Audit` | 团队活动记录 / 事件流 |
| `Studio` | 团队构建器 |

### 6.3 Scope 的产品语义

本轮文档对 `Scope` 采取双层理解：

- **用户层表达**：一个 Team
- **内部产品理解**：一个运行工作空间 / 组织单元

原因是 `Scope` 当前不仅代表团队，还承载：

- 消息互通边界
- Secret / Connector 绑定边界
- Service / Revision 运行边界
- 运行时隔离边界

所以：

- 在前端 V1 上，把 `Scope` 讲成 Team 是对的
- 但在产品长期演进上，不应把 `Scope` 限死为“最小不可再分团队”

### 6.4 团队图模型

团队详情页的核心可视模型，不应是技术对象树，而应是：

- **成员节点**：接待员、处理专员、跟进员等职责型成员
- **外部系统节点**：Telegram、Feishu、Knowledge API、CRM、LLM Provider
- **关系连线**：成员之间、成员与外部系统之间的事件协作关系

这意味着前端默认主语应当是：

- 角色
- 协作
- 事件

而不是：

- workflow
- script
- actor type
- endpoint type

### 6.5 Connector 的产品语义

本轮明确采用如下定义：

- `Connector` 默认不是一个 Agent
- `Connector` 更接近团队成员可调用的外部能力
- 或者团队连接到的外部系统

只有在系统中真的存在“专门负责某种集成编排的 Agent”时，才把那个 Agent 当作成员节点展示。

因此在前端里：

- 常规 Connector 应优先表现为“集成 / 外部系统”
- 不应默认把每个 Connector 画成一个成员

### 6.6 未来模型：团队可递归组合

最近讨论里提出的一个重要方向是：

- 团队本身也可以被理解成一个更大的复合 Agent
- 多个团队未来可以继续组合成更高层的组织网络

本轮不实现这套递归模型，但产品文档需要为未来保留这条方向。

因此，本轮的语言约束是：

- Team 是当前最重要的产品主语
- 但 Team 不应被描述成永远不可嵌套的最终单位

## 7. 用户与核心任务

### 7.1 主要用户 1：团队构建者

这类用户关心：

- 我怎么搭建一个 AI 团队
- 团队里有哪些成员
- 每个成员负责什么
- 我怎么配置流程、角色、集成和行为
- 我怎么进入编辑器调整这个团队

他们最需要的入口是：

- 团队首页
- 团队详情
- Studio

### 7.2 主要用户 2：平台管理员 / 技术运营

这类用户关心：

- 哪些服务在运行
- 当前 serving 的 revision 是什么
- 某次执行发生了什么
- 哪些服务、绑定、部署存在风险
- Actor / Service / Deployment 之间的关系

他们最需要的入口是：

- Governance
- Services
- Topology
- Deployments

### 7.3 商业角色修正

如果把本轮重构放到“未来对外卖给用户”的语境里，还需要补一层角色修正：

- 日常 champion 更可能是：AI workflow 技术 PM 或自动化负责人
- 日常使用者更可能是：团队构建者、技术运营、平台管理员
- 早期真正买单者更可能是：工程负责人、平台负责人、CTO

这意味着：

- `Team-first` 可以是前台叙事
- 但它不能掩盖产品真正要证明的价值：
  - 统一运行
  - 协作可见
  - 日志可追
  - 版本可控

### 7.4 当前最大问题

当前 Console Web 的主要问题不是“没有能力”，而是“用户不知道怎么用”。

其根因是：

- 菜单按工程对象组织，不按用户任务组织
- Team 不是主语
- Studio 是一个孤立入口
- Runtime 能力分散在多个技术名词页面中
- 用户看不到“一个团队”的完整心智

## 8. 产品目标

### 8.1 本轮目标

1. 用户打开 Console 后，能在 10 秒内理解：
   - 这是一个 AI 团队管理与运行平台
2. 用户能沿一条自然路径使用产品：
   - 进入团队
   - 看团队
   - 看活动
   - 再进入 Studio 配团队
3. 团队页能够讲清楚：
   - 谁是成员
   - 成员间如何协作
   - 最近发生了什么
4. Platform 仍然保留，但明确是后台治理层，不是首页主叙事

### 8.2 产品叙事约束

本轮需要同时守住 3 层真相：

- `External narrative = Team-first`
- `Proof of value = control-plane outcomes`
- `Internal product truth = unified runtime / control plane`

因此：

- 首页可以先讲 Team
- 但团队页必须能让技术用户看到运行、协作、活动、版本和变化
- 不能把产品讲成“只是一个更好看的 AI 团队故事”

### 8.3 非目标

本轮不是：

- 新增后端 Team API
- 新做 Team 嵌套运行时
- 改造 Studio 内部能力模型
- 改造 Connector 数据模型
- 改造 Service / Revision / Run 的正式语义

## 9. 信息架构策略

### 9.1 一个 Console，两层体验

本轮采用两层架构：

- `Teams`：面向团队构建者
- `Platform`：面向管理员和技术运营

### 9.2 推荐导航结构

**Teams**

- 我的团队
- 组建团队

**Platform**

- Governance
- Services
- Topology
- Deployments

**System**

- Settings

### 9.3 旧导航处理原则

以下现有一级入口不再保留为主导航主语：

- Studio
- Workflows
- Capabilities
- Chat
- Invoke Lab
- Runs
- GAgents

它们的处理策略应为：

- `hideInMenu`
- `redirect`
- 或被吸收入团队详情页内部

## 10. 默认用户路径

为了解决“现在根本不知道怎么用”的问题，产品必须提供清晰路径：

1. 用户进入 `Teams`
2. 用户先看到“我当前所在团队”的工作台首页
3. 用户进入某个团队详情
4. 用户先理解：
   - 这个团队由谁组成
   - 它们之间怎么协作
   - 最近发生了什么
   - 当前运行状态如何
5. 当用户需要调整团队定义时，再进入 `高级编辑`
6. `高级编辑` 打开 Studio，并始终带着当前 Team 上下文

这条路径意味着：

- Studio 是 Team 的二级入口
- 团队页才是主入口
- `Teams` 默认首页必须稳定，不因是否存在 `listScopes()` 而改变第一屏心智

### 10.1 默认首页信息层级

V1 明确规定：

- `Teams` 默认首页 = `当前团队工作台`
- 不采用“团队列表”和“当前团队首页”并列双态
- 如果后续确认存在稳定 `listScopes()`，它也只能升级为：
  - header 内的 team switcher
  - 或单独的“全部团队”页
- 不能替换 V1 的默认第一屏

默认首页的视觉层级必须是：

1. 当前团队是谁
2. 当前谁在接手、哪里阻塞、最近发生了什么
3. 我下一步最应该做什么

不允许默认退化为：

- 团队卡片列表
- 模块入口宫格
- 多个摘要卡片并列堆叠的 dashboard 首屏

最小页面结构如下：

```text
Teams Home / Current Team Workspace
├─ Team identity header
│  ├─ team name
│  ├─ current mission / scope
│  └─ primary CTA
├─ Live collaboration snapshot
│  ├─ current owner
│  ├─ latest handoff
│  └─ blocked / at-risk state
├─ Recent activity rail
│  ├─ latest run
│  ├─ latest change
│  └─ latest anomaly
└─ Secondary actions
   ├─ open team detail
   └─ continue in Studio
```

对应的导航流如下：

```text
Teams Home
  -> Team Detail
    -> Members / Activity / Topology / Integrations
    -> Advanced Edit
      -> Studio (with current scopeId)
```

### 10.2 用户旅程与情绪路径

V1 的默认体验不只是“能走通”，还必须让用户在每一步都减少不确定感。

```text
STEP | USER DOES                | USER FEELS         | PRODUCT MUST DO
-----|--------------------------|--------------------|--------------------------------------------------
1    | 打开 Teams               | 我先看看这是什么   | 直接给出当前团队工作台，而不是模块列表
2    | 看首页主舞台             | 我大概看懂了       | 告诉我当前谁在接手、哪里阻塞、下一步做什么
3    | 点进 Team Detail         | 我想确认细节       | 自动聚焦当前活跃路径，而不是让我先自己筛选
4    | 看协作画布 + 活动轨      | 我知道发生了什么   | 用 handoff / anomaly / change 解释当前运行态
5    | 决定下一步               | 我可以行动了       | 把最值得做的动作放成主 CTA
6    | 进入 Team Builder/Studio | 我要继续调整它     | 保持当前团队上下文，不制造“跳去另一个工具”的割裂感
```

约束如下：

- 首次进入团队详情时，默认情绪目标应是“我看懂了”，而不是“我要先学会怎么操作这个页面”
- 当页面存在阻塞或异常时，情绪目标应从“理解团队”切换为“立即知道该处理哪里”
- 只有在用户明确进入高级编辑后，产品才把主语从“运行中的团队”切换到“被配置的团队”

## 11. 页面需求（V1）

### 11.1 Teams 首页

#### 产品目标

让用户先进入“团队”语境，而不是技术模块语境。

#### V1 定义

由于当前未确认存在稳定 `listScopes()` 正式主链，V1 对 `Teams 首页` 采用**单态策略**：

- 默认固定为“当前团队首页 / 当前 scope 工作台”
- 第一屏的目标不是盘点有多少团队，而是让用户立刻进入一个可理解的团队上下文
- 如果未来确认存在稳定的 scope 列表接口：
  - 增加 team switcher
  - 或增加“全部团队”页
  - 但不替换 V1 默认首页

#### 页面应展示

按优先级从上到下展示：

1. 当前团队身份
   - 团队名称
   - 当前职责 / mission
   - 当前运行摘要
2. 当前协作快照
   - 当前由谁接手
   - 最近一次 handoff
   - 当前阻塞或风险
3. 最近活动摘要
   - 最近 run
   - 最近 change
   - 最近异常
4. 明确操作入口
   - 进入团队详情
   - 进入高级编辑
   - 必要时进入组建团队

#### 页面布局约束

- 默认首屏必须是一个完整构图，而不是一组均权卡片
- `当前协作快照` 必须是首屏主锚点，不能被摘要卡片抢走注意力
- `最近活动摘要` 应作为辅助信息轨道存在，而不是首页主舞台
- 如果当前没有可进入的团队，空态主 CTA 必须是 `组建第一个团队`
- 空态下进入 Platform 只能作为次级出口，不能抢首页主语

#### 主 CTA 规则

`Teams Home` 的主 CTA 必须随当前团队状态动态变化：

- 无当前团队：`组建第一个团队`
- 有明确阻塞 / 异常：`处理当前阻塞`
- 团队处于正常运行：`查看当前团队`

约束如下：

- 不允许把 `打开 Team Builder` 作为所有状态下的固定主 CTA
- `高级编辑` 默认只能是次级动作，除非页面处于纯空态
- 主 CTA 必须帮助用户减少判断成本，而不是把“接下来做什么”重新丢回给用户

#### 页面不应默认承诺

- 多团队真实列表
- 团队级全局 KPI 聚合

### 11.2 团队详情 / 统一工作台

#### 页面定义

V1 的 `Team Detail` 明确定义为一个**统一工作台布局**，而不是一组并列的说明页、tab 页或摘要卡片集合。

这意味着：

- 用户进入团队详情后，首先进入的是一个稳定的 workspace shell
- 成员、拓扑、事件流、集成都在这个 shell 内联动切换和展开
- 不允许把团队详情实现成“Overview / Members / Topology / Activity”几页松散拼接

#### 页面目标

先让用户形成“这个团队现在正在做什么”的态势感知，再进入技术实现细节。

#### 默认布局

```text
Team Detail Workspace
├─ Team header
│  ├─ team identity
│  ├─ mission / serving summary
│  └─ primary / secondary actions
├─ Primary workspace
│  └─ collaboration canvas
├─ Activity rail
│  ├─ latest run
│  ├─ latest handoff
│  ├─ latest anomaly
│  └─ latest change
└─ Context inspector
   ├─ selected member
   ├─ selected integration
   ├─ selected run
   └─ advanced edit entry
```

#### 主锚点规则

- `collaboration canvas` 是团队详情的**唯一主锚点**
- 用户进入团队详情后，视线首先应落在：
  - 当前由谁接手
  - 最近一次 handoff
  - 当前阻塞 / 风险点
- `activity rail` 的职责是解释“刚刚发生了什么”
- `context inspector` 的职责是解释“当前选中的对象到底是什么”
- 不允许把 `activity rail` 与 `collaboration canvas` 做成双主舞台并列竞争

#### 布局约束

- `collaboration canvas` 必须是主工作区，而不是附属图表
- `activity rail` 必须长期可见，用来承载时间感和变化感
- `context inspector` 负责承载技术细节，不得反客为主
- 团队详情不能退化成一列摘要卡片 + 下方详情块的传统 dashboard 结构
- 如果屏幕空间不足，应先压缩辅助面板，而不是削弱 `collaboration canvas` 的主舞台地位

#### 响应式规则

`Team Detail Workspace` 在不同 viewport 下都必须保持 **画布优先** 的主层级：

- Desktop：`collaboration canvas + activity rail + context inspector` 三段式同时可见
- Tablet：保留 `collaboration canvas` 为主列，`activity rail` 与 `context inspector` 合并为次级侧栏或可切换侧栏
- Narrow screen / mobile：`Team header` 在上，`collaboration canvas` 紧随其后，底部固定使用 segmented panel 承载 `活动 / 详情`

约束如下：

- 不允许在窄屏时把页面直接退化成“从上到下全部堆叠”的阅读页
- 不允许在窄屏时移除 `collaboration canvas`
- 屏幕变窄时，应优先折叠次级信息，而不是先牺牲主工作区
- 不允许在窄屏时把辅助信息随机做成 drawer、popover 或临时浮层混用

#### 默认聚焦规则

用户首次进入 `Team Detail Workspace` 时，界面必须默认自动聚焦**当前活跃路径**，而不是让用户从空白状态自己选择：

- 自动选中当前 owner 对应成员
- 自动高亮最近一次 handoff 对应关系
- 自动带出当前最相关的 run / activity

设计目标是让用户第一眼就能回答：

- 现在球在谁手里
- 刚刚发生了什么
- 当前哪里卡住了

不允许默认进入以下状态：

- 只有一张未聚焦的大画布
- 只有一组待选择的成员列表
- 活动轨为空、需要用户先手动选 run 才有内容

#### 应包含

- 团队名称
- 团队简介 / 当前职责
- 团队成员摘要
- 当前绑定能力摘要
- 当前 revision / current serving 摘要
- 最近运行或最近活动摘要
- 进入高级编辑的入口

#### 应弱化

- workflow/script/gagent 的底层实现差异

#### 页面关系

- `概览` 不再是独立心智上的“第一页”
- 它只是统一工作台加载后的默认状态
- `成员 / 事件拓扑 / 事件流 / 集成` 都是这个工作台中的不同观察面
- `高级编辑` 是从工作台进入 Team Builder 的出口，不是平级产品页

### 11.3 团队详情 / 团队成员

#### 页面目标

把成员心智建立起来，但成员视图是统一工作台中的一个观察面，不是独立产品页。

#### 应包含

- 成员名称
- 成员职责
- 成员当前状态
- 成员对应的实现类型
- 成员映射到的 Governance Service ID

#### 产品要求

实现类型可以展示，但不能作为主标题主语。
- 成员选择后，应驱动同一工作台中的拓扑、事件流和 inspector 联动更新。

### 11.4 团队详情 / 事件拓扑

#### 页面目标

让用户看到“团队怎么协作”，而不是“系统里有哪些 Actor”。

#### 实际后端约束

当前真实能力更接近：

- `actorId -> graph`

而不是：

- `scopeId -> full team topology`

#### V1 实现要求

- 以“选中成员后查看拓扑”为准
- 主图用成员节点和外部系统节点来讲故事
- 技术字段放在侧栏或详情区
- 拓扑视图默认承载在统一工作台的主工作区中，而不是独立的 full page 技术页

### 11.5 团队详情 / 事件流

#### 页面目标

让用户看到“最近发生了什么”。

#### 实际后端约束

当前真实能力更接近：

- `scopeId + serviceId + runId -> audit`

#### V1 实现要求

- 以“选择 service / run 后查看事件流”为准
- 事件流默认应讲成团队活动，而不是原始 runtime log 面板
- 事件流优先作为统一工作台的活动轨存在，而不是独立日志页
- 如果需要进入更深的 run detail，应从活动轨进入，而不是让首页先退化成 run list
- 活动轨的默认文案和排序必须服务于 `collaboration canvas`，解释 handoff、阻塞、异常和变化

### 11.6 团队详情 / 集成

#### 页面目标

让用户理解这个团队接了哪些外部系统。

#### 页面语义

- 这里展示的是“集成 / 外部系统 / 连接能力”
- 不是默认展示为“团队成员”

#### 数据策略

V1 只使用现有可读到的 scope binding / governance / studio 范围信息。
如果某些 Connector 事实无法稳定读取，不强行伪造完整清单。

#### 布局要求

- 集成信息默认进入统一工作台的 inspector 或辅助面板
- 不应抢占主工作区主语

### 11.7 团队详情 / 高级编辑

#### 页面目标

让 Studio 变成“编辑这个团队”的地方。

#### 产品要求

- 从团队详情进入
- 带当前 `scopeId`
- 在 UI 上明确当前正在编辑哪个团队
- 从语言上弱化“打开独立 Studio 工具”的感觉，强化“继续配置当前团队”的感觉
- 作为统一工作台中的明确出口存在，不作为与 `成员 / 拓扑 / 活动` 并列的内容 tab

### 11.8 交互状态与诚实性

#### 全局原则

V1 明确采用**诚实状态设计**：

- 不把缺失数据包装成完整实时事实
- 不把延迟状态包装成健康状态
- 不把局部推断包装成全团队真实状态
- 宁可显示 `partial / delayed / unavailable`，也不伪造“看起来完整”的团队页

#### Provenance 标签

用户层统一使用以下 provenance / freshness 标签：

- `live`：该信息直接来自当前可用的实时或近实时能力
- `delayed`：该信息存在刷新延迟，但仍可作为参考
- `partial`：该信息只覆盖了团队事实的一部分
- `unavailable`：该信息当前无法可靠读取
- `seeded`：该信息是基于当前已有绑定、成员或配置推导出的初始视图，不代表完整实时事实

#### 使用规则

- provenance 标签必须出现在用户能看到的一级信息层，而不是只藏在 tooltip 里
- `partial` 和 `seeded` 不能用绿色健康态样式伪装
- `unavailable` 必须解释缺的是什么，不允许只写“加载失败”
- 页面即使处于 `partial`，也应保持主布局稳定，不因为局部缺失而整页塌陷

#### 状态矩阵

```text
FEATURE                  | LOADING                         | EMPTY                                   | ERROR                                      | SUCCESS                                  | PARTIAL
-------------------------|---------------------------------|-----------------------------------------|--------------------------------------------|------------------------------------------|-------------------------------------------------------
Teams Home               | 保留完整工作台骨架 skeleton     | 无当前团队，主 CTA=组建第一个团队        | 无法解析当前团队，显示重试与 fallback 说明 | 当前团队工作台可见，主 CTA 随状态切换      | 只展示可确认的团队摘要，并标记 missing modules
Team Detail Workspace    | 保留 header/canvas/rail 外壳    | 无可用团队上下文，说明 scope 缺失        | 团队详情加载失败，保留 shell 并显示错误区   | 统一工作台完整可见                        | shell 保持稳定，局部面板以 provenance 标签降级
Collaboration Canvas     | 画布骨架 + 当前 owner 占位      | 无可展示协作关系，提示先选成员或无数据   | 拓扑读取失败，画布区显示明确错误原因        | 当前成员 / 外部系统关系可见               | 只展示 seeded/member-level 关系，并标记 partial
Activity Rail            | 时间轴骨架 + 最近事件占位       | 暂无最近活动，给出运行入口或解释          | audit / run 读取失败，活动轨显示错误状态    | 最近 handoff/异常/变化按时间顺序可见      | 仅展示可读到的 run 或 change，并标记 delayed/partial
Integrations Inspector   | 集成条目占位                    | 当前未连接外部系统                        | 集成信息读取失败                            | 已连接系统与连接能力可见                  | 仅显示可确认集成，未确认部分标记 unavailable
Advanced Edit Handoff    | 按钮 resolving 中               | 当前团队不可编辑，解释权限或上下文缺失    | Studio 跳转失败，保留当前页并给出重试       | 成功带着 scopeId 进入 Team Builder        | 仅带部分上下文进入 Studio，并明确提示缺失信息
```

#### 页面级要求

- `Teams Home` 必须优先稳住“当前团队工作台”布局，即使只有部分数据可读
- `Teams Home` 处于 empty 时，必须明确说明“当前没有可进入团队”，并把 Team Builder 作为第一行动
- `Teams Home` 处于 success / partial 时，主 CTA 必须优先指向当前最值得处理的动作，而不是默认把用户送去编辑器
- `Team Detail Workspace` 在 `partial` 状态下仍保持 `collaboration canvas + activity rail + inspector` 三段式结构
- `Team Detail Workspace` 进入 success / partial 时，必须先自动聚焦当前活跃路径，而不是让用户先做筛选
- `Collaboration Canvas` 若只能展示成员级或 seeded 关系，必须明确告诉用户不是 full team topology
- `Activity Rail` 若只能展示 service/run 级事实，必须明确告诉用户这是局部活动视图
- `Advanced Edit` 若上下文不完整，也不能悄悄跳成无上下文 Studio

#### 文案原则

- 优先说明“现在能确定什么”
- 然后说明“还缺什么”
- 最后给出“下一步可以做什么”
- 避免使用会掩盖不确定性的文案，例如：
  - healthy
  - all synced
  - fully connected
  - no issues

#### 关键运行证明模块

V1 的团队工作台不能只停留在“看得懂团队”，还必须明确补上 **runtime truth / control-plane proof** 模块。

至少包含以下 4 个：

1. `Run Compare / Change Diff`
   - 目标：让技术 owner 能快速看出“这次为什么和上次成功运行不一样”
   - 位置：`Team Activity / Run Detail`
   - 规则：默认比较当前运行与最近一次可作为 baseline 的成功运行
   - 诚实性要求：如果没有可比较 baseline，必须明确显示 `no successful baseline yet / compare unavailable`

2. `Human Escalation Playback`
   - 目标：证明系统真的支持 human-in-the-loop，而不是只展示理想化自动流
   - 位置：`Team Activity / Run Detail`
   - 规则：至少能看出哪里阻塞、在等谁、何时恢复、恢复后如何回到主流程
   - 不纳入：独立 human inbox / work queue

3. `Health / Trust Rail`
   - 目标：让第一次打开页面的人在 30 秒内知道这个团队现在是否健康、是否可相信
   - 位置：`Team Detail Header` 或持续可见的 side rail
   - 最少回答：
     - 现在是否 healthy / blocked / degraded
     - 是否存在 human override
     - 当前是否 risky to change
   - 诚实性要求：关键事实 unknown / delayed 时只能降级为 `attention / degraded`，不能显示为 `healthy`

4. `Governance Snapshot`
   - 目标：让 champion 和 buyer 能快速回答“这个团队能不能放心上线 / 试点”
   - 位置：`Team Detail` 中共享摘要模块
   - 最少回答：
     - 谁在 serving
     - 最近改了什么
     - 是否可追踪 / 可审计
     - 是否存在 known fallback 或 prior good state
   - 不纳入：完整 governance console replacement

这些模块共享同一组 runtime truth，不能各自拼一套事实口径。

### 11.9 Platform 页面

Platform 页面的目标不是重做，而是重新归位。

V1 原则：

- Governance、Services、Topology、Deployments 保留
- 保持技术密度
- 明确它们属于平台治理层
- 不再承担产品首页职责

### 11.10 首个验证工作流

如果本轮除了前端重构之外，还需要为后续产品验证准备一个最小演示主线，推荐默认采用：

- `Support Escalation Triage`

推荐原因：

- 成员职责容易理解
- handoff 明确
- 失败点明确
- 事件流和拓扑都容易讲清楚
- 很适合验证“Team-first 是否真的让复杂系统更易懂”

建议的团队成员：

- Intake Member：识别问题类型、优先级、意图
- Knowledge Member：生成基于知识库的候选答复
- Risk Review Member：检查退款、SLA、合规或策略风险
- Escalation Member：决定自动回复还是转人工

团队页至少应能讲清：

- 当前由谁接手
- 上一步 handoff 发生在哪里
- 当前哪里失败或阻塞
- 本次运行对应哪个 workflow / script / config version
- 与上一次成功运行相比，哪里发生了变化

这些信息中，V1 默认应优先通过 `collaboration canvas + activity rail` 的组合被看懂，
而不是要求用户先读一组摘要卡片或先切到 run 日志页。

## 12. 数据与能力映射（基于当前真实代码）

| 产品需求 | 当前能力 | 现状判断 |
|---|---|---|
| 当前团队上下文 | `studioApi.getAuthSession()` + scope 解析 | 已有 |
| scope 概览 | `studioApi.getScopeBinding(scopeId)` | 已有 |
| scope workflows | `scopesApi.listWorkflows(scopeId)` | 已有 |
| scope scripts | `scopesApi.listScripts(scopeId)` | 已有 |
| scope services | `servicesApi.listServices({ tenantId: scopeId })` | 已有 |
| 团队成员 | `runtimeGAgentApi.listActors(scopeId)` | 已有 |
| 事件拓扑 | `runtimeActorsApi.getActorGraphEnriched(actorId)` | 已有，但成员级 |
| service runs | `scopeRuntimeApi.listServiceRuns(scopeId, serviceId)` | 已有 |
| 事件流 | `scopeRuntimeApi.getServiceRunAudit(scopeId, serviceId, runId)` | 已有，但 service/run 级 |
| Studio 能力 | `/api/editor` `/api/connectors` `/api/roles` `/api/workspace` 等 | 已有 |
| 多团队列表 | `scopesApi.listScopes()` | 当前未确认 |

补充策略：

- V1 建议采用 `partially live` 模式
- 即：尽量直接复用已有 runtime / topology / audit / Studio 能力
- 允许前端在 Team 语义层做轻量组合与包装
- 如果某处 Team 抽象被后端粒度卡住，优先收窄到“一个 workflow / 一个 team page”而不是扩 backend scope

### 12.1 执行级收口

为避免主 PRD 停留在产品叙事层，V1 还需锁定以下执行约束：

- 团队页所有 `service / actor / run / version / health / fallback / human-override` 派生事实，统一经由共享 `team runtime lens` 组合。
- `team runtime lens` 第一版放在 Team 页面本地编排层中实现；只有稳定的纯派生逻辑和 route/type helper 才进入 shared 层。
- Team-first 页面禁止建立第二套 runtime model；也禁止靠 query-time replay、页面私有缓存或影子事实源去“补真相”。
- `/teams` 在 V1 不是“全部团队列表”，而是“解析当前认证团队上下文并跳转到 `/teams/:scopeId`”的入口；只有确认存在稳定 `listScopes()` 后，才考虑真实 team switcher。
- `Studio` 团队深链必须显式带 `scopeId`；只有缺失时才允许退回 app context / auth session fallback。
- Team-first 默认首页和导航迁移必须走前端 feature flag，先 internal / demo，再切默认；旧工程页在迁移期继续保留为 hidden route 或 redirect，不再承担首页主语。

## 13. 术语规范

### 13.1 用户层默认术语

- Team / 团队
- Team Member / 团队成员
- Team Activity / 团队活动
- Event Stream / 事件流
- Team Builder / 团队构建器
- Integrations / 集成

### 13.2 仅在高级信息区出现的术语

- workflow
- script
- static gagent
- revision
- endpoint
- actor id
- type url

### 13.3 术语处理原则

- 用户层先用业务心智
- 技术术语不消失，但降到二级信息
- 不在首页和一级导航中强推实现名词
- Team 是默认主语，但真正证明价值的仍然是可观察的 control-plane 结果

### 13.4 视觉方向与反 slop 约束

V1 明确采用 **温暖的 operator control-plane** 方向，而不是通用 SaaS 后台风格。

该方向基于当前 `console-web` 已存在的视觉基础继续演进：

- 字体基线：`AlibabaSans`
- 底色方向：暖白 / 纸面感背景
- 主文字色：深墨色
- 强调色：克制蓝色用于主操作和当前焦点
- 辅助强调：铜棕色用于次级标签、来源、说明与状态分层

#### 页面气质要求

- 看起来像“有人在认真值守的工作台”，不是冷冰冰监控墙
- 看起来像“一个有判断力的控制台”，不是营销页，也不是模板化后台
- 页面记忆点来自：
  - 协作画布
  - 活动轨
  - typography 层级
  - 状态诚实性
- 页面记忆点不能来自：
  - 大量阴影
  - 装饰性渐变
  - 圆角卡片堆叠
  - 图标色块阵列

#### 组件与表面规则

- 卡片只有在“卡片本身就是交互单元”时才允许存在
- 页面骨架、首屏和主工作区不得默认用均权卡片拼接
- 阴影只能作为轻微分层辅助手段，不能承担主要层级表达
- 主层级必须依赖：
  - 布局
  - 留白
  - 字号 / 字重
  - 边框与底色对比

#### 明确禁止

- 紫白渐变、蓝紫渐变、霓虹 AI 风格背景
- 三栏功能卡片首屏
- 居中大标题 + 居中说明 + 居中按钮的营销式 hero 套板
- 所有元素使用同一套大圆角
- 用阴影和磨砂感假装“高级”
- 用成排彩色 icon circle 充当信息层级

#### 实现约束

- 必须抽取稳定的 color / typography / spacing tokens
- 首屏主视觉必须围绕 `collaboration canvas` 组织，而不是围绕卡片摘要组织
- 去掉装饰性阴影后，页面仍必须保持 premium 感和清晰层级
- 新增界面默认先问：如果删掉 30% 的装饰，这页还成立吗

#### 动效预算

V1 明确采用**最小 motion budget**，只允许少量对理解当前状态有帮助的动效：

- 当前 handoff 边高亮 / 脉冲
- 活动轨中新事件的 reveal
- 从团队工作台进入 Team Builder 时的面板过渡

约束如下：

- 除上述场景外，默认静态
- 不允许加入装饰性漂浮、呼吸光效、循环背景动画
- 动效的职责只能是：
  - 强调当前焦点
  - 解释状态变化
  - 让上下文切换更连贯
- 动效不能承担品牌表达主任务，也不能掩盖信息层级不足

### 13.5 最小设计系统

由于当前仓库不存在独立 `DESIGN.md`，V1 必须在本 PRD 内先定义一套**最小设计系统**，避免实现阶段回落到默认样式。

#### 颜色角色

```text
ROLE                  | PURPOSE
----------------------|-------------------------------------------------
Paper / Warm Base     | 页面背景、工作台基底、弱分隔底色
Ink / Primary Text    | 主标题、正文、关键数据
Muted / Secondary Text| 辅助说明、时间、来源、次级状态说明
Action Blue           | 当前焦点、主按钮、当前选中、关键入口
Copper Accent         | 来源标签、上下文说明、次级强调、辅助分层
Success Green         | 明确成功且已确认的 live 状态
Warn Amber            | delayed / partial / risk / attention
Danger Red            | blocked / failed / unavailable
```

约束：

- `Warn Amber` 优先用于 `partial / delayed / risk`
- `Success Green` 不能用于 seeded、partial、unknown 状态
- 状态色必须服务于事实诚实性，不能只为了“页面更有颜色”

#### 字体层级

```text
LEVEL                 | USAGE
----------------------|-------------------------------------------------
Display / Page Title  | Teams Home 与 Team Detail 顶部主标题
Section Heading       | collaboration canvas / activity rail / inspector 标题
Body / Primary Text   | 正文、状态说明、成员职责
Meta / Label          | provenance、来源、更新时间、技术副信息
Mono / Code Accent    | revision、serviceId、actorId、runId 等技术字段
```

约束：

- 主标题必须依赖字重和留白建立层级，不能靠放大阴影或彩色背景取胜
- meta 信息必须明显比主正文弱，但仍可读
- 技术字段只在 inspector 或高级信息区进入 monospace

#### 间距与布局刻度

```text
TOKEN       | USAGE
------------|----------------------------------------------
space-4     | 紧密标签、icon 与文本微间距
space-8     | 小型控件、rail 内部条目间距
space-12    | inspector 行间距、表单局部节奏
space-16    | 常规面板内边距、区块节奏
space-24    | 主工作区内部模块间距
space-32    | 页面级主要区块切分
space-40+   | 首屏呼吸感、标题区与主舞台分隔
```

约束：

- 主工作台依赖大块留白和布局分区，不依赖卡片堆叠
- `activity rail` 的节奏应比主画布更紧
- `inspector` 的节奏应比活动轨更稳、更规整

#### 边框、表面与圆角

- 首选轻边框和底色差做表面层级
- 阴影只能做弱辅助手段
- 圆角使用应克制，避免所有元素同一套大圆角
- 主画布、活动轨、inspector 可以是不同表面层，但必须看起来属于同一个系统

#### 组件级规则

- `collaboration canvas`：主舞台组件，优先保证可读布局，不追求装饰质感
- `activity rail`：次级但长期可见，强调时间序和变化，不堆彩色 tag
- `context inspector`：信息密度最高，但视觉存在感最低
- `header select / scope switcher`：沿用当前暖白 + 铜棕 + 蓝色焦点的语言，不改成冷灰 dropdown 模板
- `status tag`：必须与 provenance 语义绑定，不能只表达情绪色

### 13.6 响应式与可访问性基线

#### 响应式断点原则

```text
VIEWPORT             | PRIORITY
---------------------|--------------------------------------------------
Desktop              | 保持三段式完整工作台
Tablet               | 保住画布主舞台，压缩或合并次级信息
Narrow screen/mobile | 先保 header + canvas，再用 segmented panel 承载活动与详情
```

约束：

- `Teams Home` 与 `Team Detail Workspace` 都必须先保住主舞台，再处理次级内容
- 不接受“移动端就是桌面端全部往下堆”的默认响应式
- 首页和团队详情都必须在 375px 宽度下仍能一眼看出当前团队、当前状态和主 CTA

#### 移动端辅助面板规则

- 默认采用底部 `segmented panel`
- 仅保留两个入口：
  - `活动`
  - `详情`
- `活动` 承载 activity rail
- `详情` 承载 context inspector 与集成、技术细节
- 不新增第三个“设置/更多”泛化入口，避免把移动端做成另一套 IA

#### 键盘与焦点

- 所有一级操作必须可通过键盘到达
- 焦点顺序必须遵循：
  - `header`
  - `primary CTA`
  - `collaboration canvas`
  - `activity rail`
  - `context inspector`
- 焦点态必须清晰可见，不能只依赖颜色细微变化
- 画布中的可交互节点、关系和过滤控件必须可通过键盘聚焦和切换

#### 屏幕阅读器与语义

- 页面必须有明确 landmark：
  - header
  - main
  - complementary
  - navigation
- `collaboration canvas` 需要提供文字摘要，说明：
  - 当前 owner
  - 最近 handoff
  - 当前阻塞 / 风险
- provenance、状态、错误和空态不能只用颜色表达，必须有文本语义

#### 触控与点击目标

- 触控设备上的主要点击目标最小尺寸为 `44px`
- rail 条目、member 节点入口、drawer trigger、segmented control 不得做成难点中的小字链接
- 任何关键操作不得只依赖 hover 才能发现

#### 对比度与可读性

- 主正文、状态文案、meta 信息都必须满足基本对比度要求
- 铜棕和辅助色只能在可读性达标时使用，不能为了“温暖感”牺牲可读性
- provenance 标签、warn / danger 状态在暖白底上必须保持足够对比

### 13.7 当前可复用设计基础

本轮实现应明确复用以下现有设计基础，而不是重新发明一套样式语言：

- [global.less](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/global.less)
  - 已有 `AlibabaSans`
  - 已有暖白基底与深墨文字方向
- [AevatarHeaderSelect.tsx](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/shared/ui/AevatarHeaderSelect.tsx)
  - 已有暖白 + 铜棕 + 蓝色焦点的控件语言
- [StudioShell.tsx](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/pages/studio/components/StudioShell.tsx)
  - 已有工作台式导航与壳子思路
- [overview.tsx](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/pages/scopes/overview.tsx)
  - 已有 scope 上下文与数据组合基础，可作为 Team Detail 的能力底座

复用原则：

- 复用语言，不复用旧的信息架构
- 复用组件气质，不复用“工程对象页”心智
- 复用现有暖白/铜棕/蓝色系统，不回退到冷灰 Ant 默认风格

### 13.8 本轮明确不纳入设计范围

以下内容在本轮设计中明确不做：

- 营销式 hero 首页
- 新品牌系统或整站 rebrand
- 装饰性 3D、霓虹、玻璃拟态视觉实验
- 因移动端而重做第二套信息架构
- 为缺失后端能力发明“看起来完整”的团队总图
- 用额外卡片网格包装旧技术页面，假装这就是 Team-first

## 14. 前端交付策略

对应的文件级实施拆解见：

- [2026-04-09-aevatar-console-web-frontend-implementation-checklist.md](./2026-04-09-aevatar-console-web-frontend-implementation-checklist.md)

### 14.1 P0：先建立正确主语

- 前端 feature flag
- 重组路由
- 重组菜单
- 调整首页入口
- 隐藏工程术语页面入口

### 14.2 P1：做团队详情外壳

- 统一工作台壳子
- 团队成员观察面
- `Health / Trust Rail`
- 高级编辑入口

### 14.3 P2：接入事件拓扑和事件流

- 基于现有 actor/service/run 粒度实现
- 不假装有全团队聚合接口
- 将拓扑和活动接入统一工作台，而不是新增分裂页面
- `Run Compare / Change Diff`
- `Human Escalation Playback`

### 14.4 P3：补 Platform 分组与旧路由收口

- `Governance Snapshot`
- Governance / Services / Topology / Deployments 分层
- redirects
- 文案统一

### 14.5 条件项

如果后续确认存在稳定 `listScopes()`，再新增：

- 团队切换器
- 或“全部团队”页

但不改变 V1 的默认首页主语。

### 14.6 Phase 1 验证计划

如果本轮要承担“为后续产品对外验证准备 demo”的任务，建议采用两周验证节奏：

1. 第 1 周
   - 做出一个 Team-first 原型
   - 范围收敛在单一 workflow
   - 至少包含：
     - Team overview
     - Team activity
     - Team collaboration map
     - version / change context
     - Health / Trust Rail
   - 首次打开默认锚定到 `Support Escalation Triage`
   - 允许采用 `partially live + seeded example history` 的 demo 方式，但必须清楚标注 provenance
2. 第 2 周
   - 用该原型跑 5 次左右的访谈 / 演示
   - 访谈对象优先覆盖：
     - 技术 PM / 自动化负责人
     - 平台负责人 / 工程负责人 / CTO

判定标准：

- 如果对方只觉得“好看”“更易懂”，但说不出它替代什么旧方案，说明 Team-first 过度停留在叙事层
- 如果技术用户能理解价值，但买方不感到 urgency，说明对外话术还需要更靠近 control-plane
- 如果没有人能清楚说出它替代了哪段当前的脚本 / dashboard / 人肉调度链路，说明 wedge 还不够锋利

### 14.7 Phase 1 Gate

只有在以下条件同时成立时，才建议把本轮方向继续放大：

1. 用户能复述：
   - 谁在处理当前步骤
   - 最近一次 handoff 在哪里发生
   - 当前失败或阻塞点是什么
2. 至少有 2 个目标用户明确说出：
   - 这能替代他们当前的一部分脚本 / dashboard / 人工调度混搭方案
3. 至少有 1 位平台或工程负责人对 pilot、治理、上线方式产生继续讨论意愿

## 15. 成功标准

本轮成功的标志是：

1. 新用户能快速理解“这是 AI 团队控制台”。
2. 用户能自然走完“看团队 -> 看活动 -> 进 Studio 配团队”的路径。
3. 用户层主界面不再被工程术语主导。
4. Platform 层仍可满足管理员工作，但不再污染首页主叙事。
5. 全部改动在前端内完成，不依赖后端新增能力。
6. 团队页不只“更易懂”，还能够支撑对运行、协作、事件和版本变化的真实理解。

## 16. 风险与待定问题

### 16.1 已知风险

1. 多团队列表 / team switcher 依赖未确认的 `listScopes()`。
2. 团队事件拓扑在技术上仍是成员级，不是 scope 总图。
3. 团队事件流在技术上仍是 service/run 级，不是 team 总流。
4. Connector 的展示完整度受现有可读信息限制。

### 16.2 待定但不阻塞本轮的问题

1. 未来 Team 是否要升级为“组织单元”而不只是“团队”
2. Team 是否要支持嵌套与递归展示
3. Connector 是否在某些场景中需要“成员化”呈现

这些都属于后续产品演进问题，不阻塞本轮前端重构。
