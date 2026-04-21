---
title: "Studio 重构 PRD（Member-first Workbench）"
status: draft
owner: tbd
last_updated: 2026-04-20
---

# Studio 重构 PRD（Member-first Workbench）

## 1. 文档定位

本稿用于纠正此前对 `Studio`、`Team`、`Member` 关系的误判，并给出新的产品定义与重构方向。

本稿只基于两类真实输入：

1. `/Users/xiezixin/Downloads/aevatar-console` 里的设计原型与对话记录。
2. 当前仓库 `apps/aevatar-console-web` 内已经存在的 `studio / scopes/invoke / scope runtime workbench` 实现。

核心结论先写在最前面：

> `Studio` 不应该再被定义成 “Team Builder”。
>
> `Studio` 的正确主语应该是 “当前 Team 中某一个 Member 的 Build / Bind / Invoke / Observe 工作台”。

也就是说：

1. `Team` 是容器、上下文、协作边界。
2. `Team Member` 是 Studio 的一等编辑对象。
3. `workflow / script / gagent` 只是 Member 的三种实现方式，不是三个平级产品。
4. 左侧列表应理解为一组 `team members`，不是一组“无上下文的 workflows/services”。

---

## 2. 基于原型的关键判断

### 2.1 从 `aevatar-console` 读出来的真实产品意图

`aevatar-console` 的主原型虽然标题写的是 `Service Workbench`，但它已经非常清楚地表达出下面这套结构：

1. 顶部先给出 `team/support-ops` 这样的团队上下文。
2. 左侧是一个列表，列表项有 `kind / revision / binding / health / last run`。
3. 中间工作区不是“资产库”，而是围绕选中的对象展开完整生命周期：
   `Build -> Bind -> Invoke -> Observe`
4. Build 阶段支持三种实现方式：
   `Workflow / Script / GAgent`
5. Bind 阶段给出真实调用面：
   `Invoke URL / Bearer Token / cURL / Fetch / SDK / bindings`
6. Invoke 阶段直接内嵌 Playground 和 AGUI 事件流。
7. Observe 阶段直接展示 run compare、human escalation、governance、health。

这说明原型真正成立的心智不是：

1. “我在编辑一个 team”
2. “我在浏览一组 workflow 资产”

而是：

1. “我在某个 team 里选中了一个 member”
2. “我要继续把这个 member build / bind / invoke / observe 跑通”

### 2.2 用户补充澄清后的最终解释

你额外强调：

> 图片中左边红框内的内容可以理解为一个个 `team member`。

这条澄清会直接改写 Studio 的产品定义：

1. 左侧列表的对象语义从“service/workflow 列表”升级为“team member 列表”。
2. Studio 不再以“团队整体”作为编辑对象，而是以“团队内某个成员”作为编辑对象。
3. 团队级内容应该留在 `Team Detail`，成员级生命周期内容进入 `Studio`。

### 2.3 当前仓库里的问题不是能力缺失，而是语义拆散了

当前 `apps/aevatar-console-web` 已经具备大量现成功能，但被拆成了几块：

1. `/studio`
   以 `workflows / studio / scripts / roles / connectors / settings / execution` 多标签存在。
2. `/scopes/invoke`
   单独承载调用、SSE、AGUI 事件流。
3. `ScopeServiceRuntimeWorkbench`
   单独承载 bindings / revisions / recent runs。

结果是用户在产品层面需要自己拼：

1. 先去 Studio 编辑实现。
2. 再去 Invoke Lab 调。
3. 再去 Runtime Workbench 看绑定和运行。

这和 `aevatar-console` 给出的“一个对象，一个工作台，一条主链路”是相反的。

---

## 3. 问题定义

当前 Studio 的核心问题不是“不够强”，而是“主语错误”。

### 3.1 错误一：把 Studio 当成 Team Builder

旧思路把 Studio 定义成：

1. Team 的整体编辑器
2. 团队构建中心
3. 团队的一切高级操作入口

这会导致：

1. 团队级对象和成员级对象混在同一页
2. 用户不知道自己当前正在改 `team`、`member`、`workflow draft` 还是 `script draft`
3. `保存 / 发布 / 绑定 / 调用 / 测试` 都失去明确主语

### 3.2 错误二：把实现方式当成产品一级导航

当前 Studio 的一级导航接近于：

1. Workflows
2. Scripts
3. Roles
4. Connectors
5. Executions
6. Settings

这是一种“工具中心”信息架构，不是“成员工作台”信息架构。

用户真正的问题不是：

1. 我要去 Workflows
2. 我要去 Scripts
3. 我要去 Executions

而是：

1. 我要修改这个 member 的实现
2. 我要重新绑定它
3. 我要立即调一下它
4. 我要看到它刚刚做了什么

### 3.3 错误三：把 Team、Member、Implementation、Binding 混成一层

当前路由和页面状态同时携带：

1. `scopeId`
2. `memberId`
3. `workflowId`
4. `scriptId`
5. `teamMode`
6. `teamName`
7. `entryName`

这本身说明产品层把多层对象塞进了一个页面主语里。

新的语义必须拆开：

1. `Team` 负责归属和上下文。
2. `Member` 负责业务能力和运行责任。
3. `Implementation` 负责具体实现形态。
4. `Binding` 负责对外暴露与调用接入。

---

## 4. 产品定义

### 4.1 新定义

`Studio = Team Member Workbench`

Studio 负责一个 Team Member 的完整工作闭环：

1. `Build`
2. `Bind`
3. `Invoke`
4. `Observe`

### 4.2 Team 与 Studio 的边界

`Team Detail` 负责团队级视角：

1. Team Overview
2. Team Members 列表
3. Team Topology
4. Team Event Stream
5. Team Governance / Assets / Integrations 总览

`Studio` 负责成员级视角：

1. 当前选中 member 的实现编辑
2. 当前 member 的 binding 配置
3. 当前 member 的调用与调试
4. 当前 member 的 AGUI 运行观察

### 4.3 Member 的定义

对前端产品而言，一个 `Team Member` 是：

1. Team 内一个可命名、可选择、可绑定、可调用、可观察的能力单元。
2. 它有且仅有一个当前主实现形态：
   `workflow / script / gagent`
3. 它可以有多个 revision、多个 binding、多个 run。
4. 它可以是 Team 的入口 member，也可以是 Team 内部协作 member。

### 4.4 Implementation 的定义

`Workflow / Script / GAgent` 不再是 Studio 顶层导航，而是 Member 的 `Build mode`：

1. `Workflow`
   适合编排多个步骤或多个下游成员。
2. `Script`
   适合确定性逻辑和代码级控制。
3. `GAgent`
   适合单 actor 持有长期状态的能力单元。

---

## 5. 重构目标

### 5.1 目标

1. 把 Studio 改造成 `Member-first` 工作台。
2. 把当前分散在 `/studio`、`/scopes/invoke`、`ScopeServiceRuntimeWorkbench` 的能力收拢到一条成员主链路。
3. 让用户始终知道“我现在正在操作哪个 Team Member”。
4. 让 `Build / Bind / Invoke / Observe` 在同一工作台连续完成。
5. 让 Team 与 Member 的层级在界面上稳定、诚实、可预测。

### 5.2 非目标

1. 本轮不把 Studio 继续扩成“团队总控台”。
2. 本轮不新增一套虚构的 team analytics 后端。
3. 本轮不把 Team Detail 和 Studio 合成一个超级页。
4. 本轮不重新定义 backend 的 Scope / Binding / Revision 基础模型。

---

## 6. 目标用户与核心任务

### 6.1 Builder

我要修改某个 member 的实现，然后马上验证它是不是能跑。

### 6.2 Operator

我要看到某个 member 当前绑定了什么、最近怎么运行、是否需要人工介入。

### 6.3 Team Owner

我要在团队上下文里管理多个成员，并快速切换到某一个成员继续编辑。

---

## 7. 信息架构

### 7.1 全局路径

推荐的主路径：

1. `Teams`
2. 进入某个 `Team Detail`
3. 在成员列表中选择一个 member
4. 打开 `Studio`
5. 在 Studio 内继续围绕该 member 工作

### 7.2 Studio 页结构

Studio 的标准布局应为三段式：

1. 顶部 Context Bar
2. 左侧 Member Rail
3. 中间 Member Workbench

可选右侧：

1. Inspector / Context / Dry-run / Binding detail / Run detail

### 7.3 顶部 Context Bar

必须稳定显示：

1. 当前 Team 名称
2. 当前 Member 名称
3. 当前 Member 类型
4. 当前 revision / binding / health 摘要
5. 返回 Team 的入口

### 7.4 左侧 Member Rail

左侧列表是一组 Team Members，不是资产分类导航。

每个列表项至少展示：

1. Member Name
2. Implementation Kind
3. Binding Status
4. Health
5. Last Run
6. Revision

支持：

1. Search / Filter
2. New Member
3. 选择后切换工作台主体

### 7.5 中间主工作区

主工作区统一采用四步式 stepper：

1. `Build`
2. `Bind`
3. `Invoke`
4. `Observe`

这四步不是四个“孤立页面”，而是同一个 member 的四个连续阶段。

---

## 8. 功能需求

### 8.1 Build

Build 页负责编辑当前 member 的实现。

#### 8.1.1 共性要求

1. 顶部先选择实现方式：
   `Workflow / Script / GAgent`
2. 右侧保留 `preview / dry-run` 区域。
3. 保存后可直接进入 Bind。

#### 8.1.2 Workflow mode

1. 中心使用 DAG / canvas 编辑器。
2. 节点表达当前 member 的内部步骤，或该 member 编排的下游成员/外部节点。
3. 节点 inspector 支持输入、输出、角色、参数、连接关系。
4. 需要清晰区分：
   `team member / external / human`

#### 8.1.3 Script mode

1. Monaco 风格代码编辑器。
2. 诊断、校验、dry-run 结果与当前 member 绑定展示。
3. 从“脚本工作台”降级为 `Build` 下的一种 mode，而不是独立主导航。

#### 8.1.4 GAgent mode

1. 以表单方式配置 type、prompt、tools、state persistence。
2. 支持 preview / dry-run。

### 8.2 Bind

Bind 页负责把当前 member 暴露成可调用能力。

必须包括：

1. 当前 binding 生成的 `Invoke URL`
2. Copy 按钮
3. `Bearer token / NyxID` 说明
4. `cURL / Fetch / SDK` 示例切换
5. binding 参数表单：
   `scope / env / revision / rate limit / streaming`
6. 已有 binding 列表：
   `activate / rotate / revoke / rebind`

Bind 的主语始终是“当前 member 的 binding”，不是整个 team 的治理中心。

### 8.3 Invoke

Invoke 页负责直接调当前 member。

必须包括：

1. 内嵌 Playground
2. request body 编辑
3. request history
4. streaming 响应
5. AGUI 面板与调用区并排或联动
6. human-in-the-loop 交互入口

当前 `/scopes/invoke` 的能力应并入 Studio，而不是让用户跳去一个 legacy lab。

### 8.4 Observe

Observe 页负责运行后观察当前 member。

必须包括：

1. AGUI Timeline
2. Step / Tool / Thinking / Message 分类视图
3. Metrics strip：
   `event count / step count / tool count / errors / elapsed`
4. run compare
5. human escalation playback
6. governance snapshot
7. health & trust rail

Observe 默认展示当前 member 的运行事实，不承诺伪造强一致。

### 8.5 Shared Requirements

#### 8.5.1 状态诚实性

所有观察态必须带 provenance，至少支持：

1. `live`
2. `delayed`
3. `partial`
4. `seeded`
5. `unavailable`

#### 8.5.2 跨步骤连续性

同一个 member 在四步之间必须共享上下文：

1. 当前 revision
2. 当前 binding
3. 当前 invoke draft
4. 当前 selected run

#### 8.5.3 从 Team 进入的深链

Studio deep link 必须至少能表达：

1. `scopeId`
2. `memberId`
3. 可选 `build mode`
4. 可选当前 step

不应再把 `workflowId / scriptId / teamMode / entryName` 混成产品一级主语。

---

## 9. 当前页面的重组建议

### 9.1 现有能力映射

当前前端能力可以大致这样重组：

1. `pages/studio/index.tsx`
   主要复用为 `Build`
2. `modules/studio/scripts/ScriptsWorkbenchPage.tsx`
   下沉为 `Build -> Script mode`
3. `pages/scopes/invoke.tsx`
   主要复用为 `Invoke`
4. `pages/scopes/components/ScopeServiceRuntimeWorkbench.tsx`
   主要复用为 `Bind` 与 `Observe` 的 runtime 数据面

### 9.2 要降级的一级导航

以下内容不应再作为 Studio 的主导航项：

1. `Workflows`
2. `Scripts`
3. `Executions`
4. `Roles`
5. `Connectors`

它们应改成：

1. Member rail + Build mode
2. 右侧 inspector / drawer
3. 次级 catalog 页面
4. Team Detail 的辅助入口

### 9.3 Roles / Connectors / Settings 的位置

推荐处理方式：

1. `Roles`
   变成 Build 中可引用的角色资产，支持从 inspector 或 drawer 打开。
2. `Connectors`
   变成 Build / Bind 中的上下文资源，而不是 Studio 的产品主语。
3. `Settings`
   保留为 Studio 全局设置，但不参与默认主链路。

---

## 10. 页面文案与心智规则

### 10.1 必须强化的文案

1. `Team`
2. `Member`
3. `Implementation`
4. `Binding`
5. `Invoke`
6. `Observe`

### 10.2 应避免的误导性文案

1. `Studio = Team Builder`
2. `编辑整个团队`
3. `Workflow 列表 = Team Members`
4. `Scripts / Workflows / GAgents` 作为平行产品主语

### 10.3 关键心智规则

1. 进入 Studio 时，用户必须先知道“当前在哪个 Team、正在操作哪个 Member”。
2. `Build` 改的是实现。
3. `Bind` 改的是暴露方式。
4. `Invoke` 看的是一次调试调用。
5. `Observe` 看的是运行事实。

---

## 11. 数据与后端约束

本 PRD 不要求新造一套后端主语，而是按现有能力重新组织前端。

### 11.1 可直接复用的已有能力

1. Studio workflow 编辑与 YAML 序列化
2. Scripts 编辑、校验、test run、promote
3. Scope binding 查询
4. Service revisions / bindings / recent runs
5. SSE / AGUI 事件流
6. run audit / run detail / runtime trace

### 11.2 V1 不承诺的能力

1. 虚构的全局 team analytics
2. 脱离 scope 事实源的 canonical member directory
3. query-time 拼装出来的伪强一致“完整团队图谱”

---

## 12. 成功标准

### 12.1 心智正确性

1. 用户能够明确区分 Team 和 Member。
2. 用户能明确知道当前自己是在编辑 member，而不是“编辑整个 team”。
3. 用户不会再在 `Studio / Invoke / Runtime Workbench` 三处跳来跳去完成一次成员调试。

### 12.2 任务完成率

围绕单个 member 的核心任务应该可以在一个工作台完成：

1. 修改实现
2. 绑定 revision
3. 发起调用
4. 看到 AGUI 结果
5. 处理 human approval / input

### 12.3 界面一致性

1. Team 级页面只谈 Team。
2. Studio 页面只谈 selected member。
3. 实现方式不再和页面层级混淆。

---

## 13. 分阶段实施建议

### Phase 1：语义重构

1. 调整 Studio 路由主语为 `scopeId + memberId`
2. 引入 Member Rail
3. 移除以 `Workflows / Scripts / Executions` 为主的一级导航
4. 建立 `Build / Bind / Invoke / Observe` 主 stepper

### Phase 2：能力收拢

1. 把现有 workflow editor 接入 Build
2. 把 scripts workbench 接入 Build 的 Script mode
3. 把 invoke lab 接入 Invoke
4. 把 runtime bindings / revisions / runs 接入 Bind / Observe

### Phase 3：体验打磨

1. 完成 provenance 体系
2. 完成 run compare / human playback / governance snapshot
3. 加入多种 AGUI 呈现模式与布局切换

---

## 14. 开放问题

1. 一个 Team Member 的显示名是否始终等价于当前 service/binding 的 display name？
2. Team 内“入口 member”和“内部 member”是否需要显式标签？
3. Roles / Connectors 是否需要保留独立目录页，还是全部下沉为 contextual drawers？
4. 新建 Team 流程进入 Studio 时，默认打开“第一个入口 member”，还是先进入空的 member rail？

---

## 15. 最终结论

Studio 下一轮重构不应继续沿着“团队构建器”推进，而应回到 `aevatar-console` 原型真正擅长的方向：

1. Team 提供上下文。
2. Member 是工作对象。
3. Build / Bind / Invoke / Observe 是主链路。
4. Workflow / Script / GAgent 是实现模式，不是顶层产品。

一句话总结：

> `Studio` 应该成为 “在一个 Team 里持续打磨某个 Member 的工作台”，而不是“承载所有 Team 概念的超级编辑器”。
