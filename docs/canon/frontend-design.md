---
title: "Aevatar 前端设计基线"
status: active
owner: potter
last_updated: 2026-05-11
---

# Aevatar 前端设计基线

本文档定义 Aevatar 仓库内前端实现的默认设计口径。凡是页面、组件、控制台、playground、样式系统、视觉 polish 或前端重构任务，默认以本文为准。

本文尤其约束 `apps/aevatar-console-web`：Console Web 不是普通后台管理系统，而是用户理解、运行、调试和治理 AI Team 的工作台。前端设计必须同时表达产品语义、架构语义和视觉系统，不能只停留在样式美化。

## 1. 适用范围

当前仓库内的主要前端工作面包括：

- `apps/aevatar-console-web`：控制台 Web 应用，是本文档的首要约束对象。
- `demos/Aevatar.Demos.Workflow.Web/wwwroot`：Demo Web 静态 playground。

如果未来新增前端宿主，也默认继承本文档；只有该宿主存在更高优先级的局部设计文档时，才允许局部覆盖。

## 2. Console Web 产品定位

Console Web 的产品主语是 `AI Team Workbench`，不是系统对象浏览器。

用户进入 Console Web 后，页面应优先回答：

- 当前 Team 是谁、在做什么、是否健康。
- 最近一次 run、message、tool call、human input 或 workflow signal 处在哪个阶段。
- 当前能力如何构建、绑定、调用、观察。
- 出问题时该看哪个事实源、哪个 readmodel、哪条 observation。
- 需要治理时如何下钻到 service、deployment、policy、binding 与 endpoint。

默认信息架构应保持两层心智：

- `Teams` 层负责运行、协作、介入和理解。
- `Platform` 层负责服务治理、发布、绑定、流量、策略和排障。

V1 页面不得为了视觉完整性伪造后端不存在的事实源。已有稳定 API 或 readmodel 的对象，例如 member roster、team roster、run summary，必须读取真实契约；没有稳定契约的全局 team catalog、跨 team health、组织级 analytics 或运营 KPI，只能展示当前上下文、最近记录、可验证的 readmodel 或明确标注为本地临时状态。

## 3. Console Web 页面模型

Console Web 新增或重构页面时，优先落到以下页面模型之一。

### 3.1 Team Workbench

Team 页面负责 team-first 运行视角。

标准结构：

- 顶部 `Team Context Header`：team/scope、状态摘要、最近 run、待处理事项、关键操作。
- 主区域 `Activity / Topology / Intervention / Bindings`：活动流、拓扑、人工介入、连接与策略。
- 右侧或下方 `Observation Panel`：timeline、trace、summary、raw。

Team 页面不得把 workflow、script、GAgent、service 作为无上下文的一级卡片墙。它们应被解释为当前 Team 的成员实现、能力入口、运行记录或治理对象。

### 3.2 Member Workbench

Studio 的直接编辑对象是 Team 中的某个 `Member`，不是抽象工具集合。

标准结构：

- 顶部 `Context Bar`：scope、team、member、implementation kind、revision、binding、health。
- 左侧 `Member Rail`：team members、binding 状态、health、last run、revision。
- 中间四阶段工作区：`Build -> Bind -> Invoke -> Observe`。

任何保存、发布、绑定、调用、测试、观察动作都必须让用户知道当前主语是哪个 member。

### 3.3 Service Workbench

Service / Invoke 页面负责一次真实请求的运行闭环。

标准结构：

- 顶部 `Service Header`：service、endpoint、scope、revision、状态、主要动作。
- 阶段提示：`Contract / Invoke / Observe / Govern`，用于表达当前 service 的契约、调用、观察与治理位置。
- 左侧 `Playground`：prompt、actorId、headers、实际 request preview、run/stop/replay。
- 左下 `Request History`：最近请求、状态、耗时、结果摘要。
- 右侧 `AGUI Events`：timeline、trace、tabs、bubbles、raw、run summary、response。

request preview 必须等于实际发送 payload。若 endpoint 不支持当前 workbench 的 streaming invoke，不得伪装成可执行，应给出原因和跳转入口。

Service Workbench 不拥有 implementation build 语义。需要编辑 workflow、script 或 GAgent 实现时，必须跳转到 Member Workbench；service 页面只负责已发布能力的契约、调用、观察和治理。

### 3.4 Platform Governance

Platform 页面负责 service-centric 治理。

它可以更密集、更工具化，但仍要保持：

- 当前对象身份清晰。
- 版本、部署、流量、policy、binding 的关系清晰。
- 异步动作有 loading、错误反馈和防重复提交。
- 表格、筛选、详情抽屉和诊断面板之间有稳定导航关系。

### 3.5 页面职责表

| 页面模型 | 主要路由 | 主语 | 子视图 / 对象 |
|---|---|---|---|
| `Team Workbench` | `/teams`、`/teams/:scopeId`、`/teams/:scopeId/:teamId` | Team | activity、topology、intervention、members、team-scoped bindings |
| `Member Workbench` | `/studio` | Member | build、bind、invoke、observe、implementation、revision |
| `Service Workbench` | `/services`、service invoke 入口 | Service | contract、invoke、request history、AGUI events、run summary |
| `Platform Governance` | `/governance`、`/governance/*`、`/deployments` | Platform object | deployment、traffic、policy、binding、endpoint catalog |
| `Run Diagnostics` | `/runs` 或工作台内嵌 run view | Run | timeline、trace、audit、response、errors、human input |

`Run / Binding / Policy` 不是默认一级页面模型；它们是 Team、Member、Service 或 Platform 工作台中的子视图或治理对象。只有当用户任务明确是跨对象诊断或治理时，才允许作为独立入口出现。

## 4. 架构语义 UI 规则

前端必须把 Aevatar 的 CQRS / Observation / ReadModel 语义显式呈现为 UI 状态。

### 4.1 状态不得混用

以下状态必须分开建模，禁止合并成一个 `success` 或 `completed`：

| UI 状态 | 来源 | 含义 |
|---|---|---|
| `LocalDraft` | 本地 UI | 用户还在编辑，未发 command。 |
| `Accepted` | Command receipt | 后端已受理 command，并返回稳定 `commandId`。 |
| `Running` | Observation | actor 已开始推进，或 workflow/run 进入执行中。 |
| `Streaming` | Observation | AGUI / SSE / WebSocket 正在推送 token、tool call、step event。 |
| `Paused` | Observation / ReadModel | 等待 human input、approval 或 signal。 |
| `Observed` | ReadModel | 查询结果已经物化到某个 `stateVersion` 或刷新戳。 |
| `Completed` | 公开业务状态 | 公开 contract 明确表示 run/message/service action 已完成。 |
| `StillProcessing` | 本地 UI + 超时策略 | 暂无新 observation，但不能证明失败。 |
| `Failed` | 公开错误状态 | API、observation 或 readmodel 明确返回失败。 |

HTTP 200、`accepted` receipt 或 `POST` 成功只能进入 `Accepted` 或 `Running`，不能直接把业务结果标成 `Completed`。

`Streaming` 是 `Running` 的子状态或表现形式，不要求与 `Running` 互斥。LLM token streaming、tool call streaming 与普通 step 推进可以同时存在；UI 可以用主状态表达 `Running`，用子状态、badge 或 progress rail 表达 `Streaming`。

### 4.2 Query 只读 ReadModel

页面加载最近消息、run summary、team activity、service detail、binding 列表等稳定查询时，默认只能读取已物化的 readmodel 或正式 query DTO。

前端不得设计或调用以下路径作为正常查询体验：

- actor 内部 state 读取。
- event store replay 后即时返回。
- query 方法内触发 projection refresh。
- 根据 actor runtime 结构临时拼装事实。

如果 readmodel 暂时为空或落后，UI 应显示 empty、stale、still processing、refresh available 或 observed version，而不是触发 query-time priming。

### 4.3 Observation 是运行过程，不是查询替代品

AGUI、SSE、WebSocket 与 timeline 事件用于呈现运行过程。它们可以更新当前屏幕的临时体验，但稳定页面恢复、刷新、分享和历史回看仍应回到 readmodel/query。

Observation 面板默认优先展示用户可理解的信息：

1. `Run Summary`
2. `Response`
3. `Timeline`
4. `Trace / Tool Calls`
5. `Raw`

Raw event 是高级调试视图，不能成为普通用户理解运行结果的唯一入口。

### 4.4 前端稳定依赖

前端可以稳定依赖：

- 公开 DTO 字段。
- 公开业务状态枚举。
- `commandId`、`correlationId` 等追踪标识。
- `readmodel.stateVersion`、`refreshedAt` 或等价新鲜度字段。
- API 明确声明的 `messageRole`、`messageKind`、`sourceType`、`eventKind`。

前端不得稳定依赖：

- `actorId` 字符串前缀或格式。
- C# 类名、namespace、GAgent 实现类型名。
- `EventEnvelope` 内部 route、runtime、stream provider 字段。
- 后端日志文案或异常字符串。
- 临时本地缓存中的事实状态。

本节部分架构边界已有 CI 门禁覆盖，例如 actorId 字符串解析、projection lifecycle 边界等会在 `tools/ci/architecture_guards.sh` 及其子脚本中被检查。文档结构与 frontmatter 由 `tools/docs/lint.sh` 覆盖。若新增或恢复专门的前端静态边界门禁，必须同步在本节写明覆盖范围和触发命令。

### 4.5 命名与文案规范

Console Web 面向用户的文案默认使用产品语义，而不是后端实现语义。

推荐用语：

- `Team`：用户正在运营和观察的协作边界。
- `Member`：Team 内可构建、绑定、调用和观察的能力单元。
- `Service`：已发布、可治理、可调用的能力入口。
- `Run`：一次真实执行或调用。
- `Binding`：成员或服务和运行契约、凭据、endpoint 的连接关系。
- `Policy`：治理规则、审批要求、流量或访问约束。

仅在调试、高级设置、Raw、Trace 或开发者文档中暴露：

- `actorId`
- `EventEnvelope`
- `readmodel`
- `stateVersion`
- C# 类型名、namespace、GAgent 实现类型名

普通用户路径应把这些术语翻译为可理解的产品语言。例如 `readmodel.stateVersion` 可以显示为“已观察到版本 42”，`actorId` 可以显示为“运行实体地址”，并放在可复制的高级详情里。

### 4.6 状态展示标准

空态、加载态、错误态、stale 态和 paused 态必须解释当前事实源和下一步动作，不能只显示一个空容器或 spinner。

| 状态 | 必须说明 | 推荐动作 |
|---|---|---|
| `Empty` | 当前查询的对象、是否有权限、是否确实没有数据 | 创建、连接、返回 Team、刷新 |
| `Loading` | 正在读取 query/readmodel，还是正在等待 observation | 保持上下文，不清空已有稳定结果 |
| `Stale` | 当前 readmodel 的刷新戳或版本，为什么可能落后 | 继续观察、手动刷新、查看最近 run |
| `StillProcessing` | command 已受理但暂无新 observation | 继续等待、打开 run diagnostics、刷新 readmodel |
| `Paused` | 等待 human input、approval 还是 signal | 输入、批准、拒绝、发送 signal、查看上下文 |
| `Failed` | 失败来源是 API、observation、readmodel 还是本地校验 | 重试、复制错误、打开 trace、联系治理入口 |

状态展示不得删除用户已经看到的稳定事实。新的 observation 到达前，页面可以显示“仍在处理”，但不应把已有 readmodel 结果清空成未知。

## 5. 视觉设计立场

### 5.1 先定方向，再写界面

前端实现前，必须先回答三个问题：

1. 这是给谁用的？
2. 这个工作面最重要的交互是什么？
3. 这一屏最让人记住的视觉特征是什么？

允许的风格可以很大胆，也可以很克制，但必须单一、明确、连续。可选方向例如：editorial、industrial、warm technical、brutalist、refined minimal、retro-futurist。禁止把多种弱风格混合成“看起来像模板站”的中间态。

### 5.2 连续性优先于炫技

在已有产品表面上工作时，优先保持：

- 信息架构不变。
- 核心导航不变。
- 主要操作流程不变。
- 领域术语不变。

设计提升应主要体现在层次、比例、排版、状态、密度控制、质感和动效，而不是随意重排用户已经形成肌肉记忆的结构。

### 5.3 明确禁止项

以下模式在仓库内默认视为低质量实现，应避免作为默认方案：

- 以 `Inter`、`Arial`、`Roboto`、宽泛 `system-ui` 作为首选字体栈。
- 紫白渐变默认主题。
- 千篇一律的 SaaS 卡片墙和统计卡堆叠。
- 没有层次变化的浅灰边框 + 白底面板拼接。
- 为了“看起来现代”而堆砌玻璃态、发光和悬浮阴影。
- 缺少主题 token、完全依赖散落硬编码颜色和 spacing。
- 只追求截图好看，不考虑真实内容密度、滚动状态和空/错/载入态。

如确有历史兼容或局部延续需求，必须说明为什么不能收敛到更有辨识度的方案。

## 6. 设计系统要求

### 6.1 Token 优先

颜色、字体、字号、间距、圆角、阴影、边框、动效时长与 easing，优先收敛为：

- CSS variables。
- theme tokens。
- 可复用样式原语。

Console Web 的共享 token 和布局原语优先维护在：

- `apps/aevatar-console-web/src/shared/ui/aevatarWorkbench.ts`
- `apps/aevatar-console-web/src/shared/ui/proComponents.ts`
- `apps/aevatar-console-web/src/shared/ui/interactionStandards.ts`
- `apps/aevatar-console-web/src/global.less`

禁止在多个页面中长期复制相近但不相等的硬编码值。

### 6.2 字体策略

Console Web 默认使用 `AlibabaSans` 作为产品字体。新增 Console 页面不得回退到 `Inter`、`Arial`、`Roboto` 或裸 `system-ui` 作为主字体。

CLI Playground 当前若存在历史字体栈，应视为局部历史实现，不得反向扩散到 Console Web。后续 redesign 时应逐步收敛到与 Aevatar 产品气质一致的字体与字号系统。

控制台类产品要优先保证可读性，再追求风格化。display font 只能用于真正的标题、品牌信号或空态表达，不能牺牲表格、编辑器、timeline、trace 和表单的扫描效率。

### 6.3 版式与层次

- 优先通过留白、对齐、尺寸级差、色块关系建立层次，不靠额外说明文字补层次。
- 允许不对称布局、强调区、分层背景、局部高对比，但必须服务于任务流。
- 面板、侧栏、工作区和检查区要有清晰主次，避免全部元素同权重。
- 工具型页面要优先稳定尺寸、滚动区域和响应式约束，避免 hover、loading、长文本导致布局跳动。
- 不使用卡片套卡片。页面分区用全宽 band、工作台分栏或 unframed layout；卡片只用于列表项、模态、详情块和明确的 framed tool。

### 6.4 动效

- 动效必须有职责：进入、聚焦、反馈、切换、状态确认。
- 允许少量高质量的 page-load 或 panel transition。
- 禁止到处堆 hover 特效和无意义的微动效。
- 运行态动画要表达真实状态，例如 streaming、loading、paused、failed，不得制造虚假的实时感。

## 7. 组件交互标准

Console Web 交互组件默认继承 [组件交互标准](../design/2026-04-23-component-interaction-standard.md)。

所有按钮、chip、pressable card、tab、toolbar action 和异步入口默认必须满足：

- `default / hover / active / disabled / loading` 状态完整。
- hover 有清晰反馈，active 有按压反馈。
- disabled 不可点击，`aria-disabled` 与键盘行为一致。
- 异步动作 loading 期间不可重复触发。
- 异步失败必须有用户可见反馈，优先 `message.error(...)`，必要时补局部 `Alert`。
- keyboard focus 必须可见。
- 状态切换使用统一 transition。

新增组件优先复用：

- `AEVATAR_INTERACTIVE_BUTTON_CLASS`
- `AEVATAR_INTERACTIVE_CHIP_CLASS`
- `AEVATAR_PRESSABLE_CARD_CLASS`

不要回到裸 `<button>`、无 pending lock、无错误提示、只靠灰色表示 disabled 的实现方式。

## 8. 按工作面执行的规则

### 8.1 Console Web

`apps/aevatar-console-web` 运行在既有 Ant Design Pro shell 内，但页面心智应是 Aevatar workbench，而不是默认 Ant Design 管理后台。

要求：

- 默认在现有 shell 内做 refinement，除非用户明确要求大改，否则不推翻全局布局和导航组织。
- 页面优先表达 `Team / Member / Service / Platform` 的当前主语。
- `Run / Binding / Policy` 应作为上述工作台内的子视图或治理对象呈现；只有跨对象诊断、审计或治理任务明确存在时，才作为独立入口出现。
- 高频工作流优先采用 workbench 结构，而不是散装 card grid。
- AGUI、timeline、trace、request history、readmodel freshness 是一等体验，不是 debug 附件。
- 表单、表格、编辑器、图谱、运行面板必须在真实内容密度下可读。
- 任何展示的状态、payload、metric、health、version 都必须能追溯到公开 API、readmodel 或本地 UI 状态。

### 8.2 Demo Web

`demos/Aevatar.Demos.Workflow.Web/wwwroot` 主要承担演示和体验验证职责。

要求：

- 与 Console Web 的核心状态语义保持一致。
- 若存在 Demo 特有按钮或壳层差异，必须限制在 demo 边界内，不反向污染主源码结构。
- Demo 可以简化治理能力，但不能简化到误导用户理解 command、observation、readmodel 的状态边界。

## 9. 质量门槛

前端实现默认要满足以下要求：

- 桌面端与移动端都能正常加载和操作。
- 键盘导航可达。
- 真实内容密度下仍然可读。
- 空态、加载态、错误态、stale 态、paused 态至少具备基本视觉处理。
- 文本不溢出、不遮挡、不因为按钮标签、长 ID、长 service 名称破坏布局。
- 不为了风格牺牲表单、编辑器、图形工作区、timeline、trace 等核心交互的可用性。
- 不把调试视图作为普通用户理解运行结果的唯一入口。

## 10. 响应式优先级

Console Web 是密集工具，不是 landing page。桌面端是完整构建和治理的主工作面，移动端必须保证关键运行与介入动作可完成。

移动端最低要求：

- 可以查看 Team / Service / Run 的当前状态和最近活动。
- 可以读取 response、timeline 摘要、错误原因和 paused 原因。
- 可以完成 human input、approval、signal、stop、retry 等运行介入动作。
- 可以复制 endpoint、commandId、runId、错误信息和关键 trace 链接。
- 可以从 Team 跳到 Studio / Service / Run 的正确上下文。

允许桌面优先的能力：

- 复杂 workflow / graph 编辑。
- 多栏 trace 对比。
- 大型表格批量治理。
- 长 JSON / Raw payload 编辑。

这类桌面优先能力在移动端必须给出诚实降级，不得显示看似可编辑但实际不可用的半成品 UI。

## 11. PR 设计自查清单

Console Web 相关 PR 在提交前至少自查：

1. 页面主语是否明确是 `Team / Member / Service / Platform / Run` 之一。
2. 是否伪造了后端不存在的 team catalog、analytics、health、roster 或 run 事实。
3. `POST` 成功、`accepted` receipt、observation、readmodel observed、completed 是否被分开显示。
4. query 是否只读正式 readmodel / query DTO，没有 query-time replay 或 projection refresh。
5. UI 是否解析了 `actorId` 前缀、C# 类名、`EventEnvelope` 内部字段或日志文案。
6. request preview 是否等于实际发送 payload。
7. Raw / Trace 是否只是高级入口，而不是普通用户理解结果的唯一入口。
8. loading、empty、stale、paused、failed 是否都有原因和下一步动作。
9. 异步按钮是否有 loading、防重复点击、错误反馈和 keyboard focus。
10. 移动端是否至少能查看状态、复制关键标识、处理人工介入和打开诊断。

## 12. 验证要求

修改 `apps/aevatar-console-web` 后，至少执行与改动相称的前端构建校验，例如：

```bash
npm --prefix apps/aevatar-console-web run tsc
npm --prefix apps/aevatar-console-web run build
```

如果任务只涉及文档或规则更新，不需要运行前端构建；至少执行：

```bash
bash tools/docs/lint.sh
git diff --check
```

## 13. Workflow Activity vNext 的 NyxID 视觉对齐规范

本节是 `/scopes/:scopeId/workflow-activity-vnext` 的局部视觉合同，来源于
2026-08-05 对 `https://nyx.chrono-ai.fun/` 的已登录桌面与移动端实测。
它只约束视觉结构、组件密度和响应式行为，不改变该工作台既有的信息架构、
产品语义、身份边界、真实 API 数据源、路由、认证或本地化合同。

### 13.1 来源、优先级与允许偏差

- NyxID 是该命名空间在布局、排版比例、间距、控件尺寸、表格密度、容器层级、滚动和响应式行为上的视觉参考。
- Aevatar 现有颜色 token 是唯一允许保留的视觉差异；状态色继续表达 Aevatar 的运行语义，不复制 NyxID 的品牌紫色。
- 字体家族继续使用 Aevatar 的 `AlibabaSans`，但字号、字重和行高按本节度量执行；不得为了追随参考站引入未随仓库交付的字体资源。
- NyxID 实测部分大容器为 14px 圆角。Aevatar 遵守仓库不超过 8px 的组件圆角约束，工作台容器统一使用 8px，控件使用 6-8px。这是唯一的几何偏差。
- 本节不得覆盖 Workflow Activity vNext 的 17-frame baseline、backend-honest deviations 或 user-path completion evidence。

### 13.2 Shell 与页面骨架

| 区域 | 桌面 | 移动端 |
|---|---:|---:|
| 全局顶栏 | 52px 高，1px 下边框 | 52px 高，保留品牌、账户、语言与菜单入口 |
| 本地侧栏 | 200px 宽，从顶栏下方开始 | `< 768px` 隐藏，由顶栏菜单打开同一组导航 |
| 主内容水平内边距 | 40px；中等宽度 32px/24px | 16px |
| 主内容顶部内边距 | 24px | 16px |
| 主内容底部内边距 | 32px 加 safe area | 32px 加 safe area |
| 页面标题 | 28px/28px，700 | 22px/22px，700 |
| 页面说明 | 12px/17px，400 | 12px/17px，400 |

- 顶栏品牌、面包屑和全局账户动作稳定占位，不因页面标题、加载状态或操作按钮变化而跳动。
- 页面标题和说明位于主内容区，不放入卡片；页面级操作与标题或首个工具栏对齐。
- 工作区使用满宽、单层容器。禁止卡片套卡片、营销式 hero、统计卡墙和装饰性渐变。
- 桌面侧栏导航项高度 34px，字体 12px/17px、500，左右内边距 12px，图标与文字间距 9px；分组间通过留白而非额外卡片区分。

### 13.3 间距与组件尺寸

- 基础间距阶梯为 `4 / 8 / 12 / 16 / 24 / 32 / 40px`，页面不得复制近似但不相等的魔法值。
- 页面标题到页签或首个工具栏保持 24-32px；页签到主体保持 16-24px。
- 标准页签栏高度 32px，项间距 4px，单项水平内边距 12px、垂直内边距 8px；选中态使用 2px Aevatar 主色下划线，不使用填充胶囊。
- 标准输入、选择器和紧凑按钮高度 32-36px；图标按钮固定 32px 方形。主操作可以使用 36px 高度，但不得因 loading 或标签变化改变尺寸。
- 表单 label 使用 12px/17px、500；帮助文案使用 12px/17px、400。输入控件与其 label 间距 8-10px，一个字段组到下一个字段组间距 20-24px。
- 一组并列操作间距 8px；工具栏主区与次区使用 `space-between`，窄屏时按任务顺序换行或堆叠。

### 13.4 表格、列表与操作

- 表格容器有 1px Aevatar 中性边框、8px 圆角、白色工作面和极轻阴影；表格本身不再嵌套第二层边框卡片。
- 表头高度 32px，字号 10px/14px、600、大写；字距固定为 `1.5px`，这是数据表头的可扫描性规则，不扩散到普通文本。
- 正文使用 12px/17px、400；普通单行记录高度 53-60px。标题+说明的首列可以达到 60px，但不得回到 72px 以上的稀疏行。
- 单元格水平内边距 12px，垂直内边距 10px；行分隔线使用 1px 中性色，不依赖斑马纹。
- 行 hover 只改变背景或边框颜色，不改变尺寸。整行可进入详情时，应有一个明确的主要导航目标。
- 行内常规命令优先使用一个主要文本或图标+文本操作；其余操作收敛到带可访问名称和 tooltip 的省略号菜单。禁止并排堆放三个以上同权重文本按钮。
- 状态 badge 使用文本加语义提示，不只靠颜色；高度 22-24px、字号 11-12px，圆角不超过 8px。
- 桌面/平板表头可 sticky；纵向滚动由表格容器拥有，页面不得因为表格内容产生水平滚动。
- 移动端保持语义表格，并在有边界的容器中水平滚动；不得将同一张宽表默认改造成卡片列表。首个重要列应稳定在视觉起点，长值在单元格内截断或换行，不扩大页面宽度。

### 13.5 页面映射

| vNext 页面 | NyxID 视觉模式 | Aevatar 实施要求 |
|---|---|---|
| Workflows | 服务列表的标题、页签/工具栏、紧凑表格 | 搜索与筛选同行；表格至少显示 6-8 行；主要操作清晰，其余进入菜单 |
| New workflow | 详情页的单层 section 与选择控件 | 四种创建方式作为同层可选择列表/块，不做营销卡片墙；选择后复用同一表单容器 |
| Workflow editor | 详情页标题动作 + 全宽工具面 | 顶栏和页面标题保持 NyxID 密度；Canvas 继续 full-bleed，面板不套装饰卡片 |
| Activity | Sessions/服务表格 | 采用 32px 表头、53-60px 行高、单一详情目标和容器内滚动 |
| Run detail | 服务详情页的标题、页签与单层详情块 | 状态和恢复动作靠近标题；摘要、页签、步骤表按单层垂直节奏排列 |
| Settings | Account Settings 的标题、32px 页签和表单面板 | 三个 section 改为横向页签；表单使用单一边框容器；dirty save actions 位于容器底部并在移动端可见 |

### 13.6 响应式与可访问性

- 断点语义固定：桌面 `>= 1200px`，平板 `768-1199px`，移动 `< 768px`。
- 移动端隐藏固定侧栏，顶栏菜单打开同一导航；不得再增加一条永久横向导航带占用首屏高度。
- 移动端页签可横向滚动但不换成下拉框，保证当前 section 和相邻 section 可见。
- 宽表格使用局部横向滚动，滚动容器可获得键盘焦点并有清晰名称；页面 `scrollWidth` 不得大于 `clientWidth`。
- 标题、工具栏、表单按钮和恢复动作必须在 390px 宽度无重叠；最长单词、ID 和错误信息必须换行、截断或在局部滚动区域内展示。
- 所有图标按钮有可访问名称；不熟悉的图标提供 tooltip；focus-visible 使用 Aevatar 主色高对比轮廓。
- `prefers-reduced-motion` 下关闭非必要 transition；loading、hover、open/close 状态不得引发布局跳动。

### 13.7 验收视口与证据

- 桌面：`1440x900` 或更宽，检查顶栏、200px 侧栏、标题区、工具栏、至少 6 行数据和局部滚动。
- 平板：`834x1112`，检查侧栏/内容压缩、表格局部滚动、编辑器面板和 Settings 页签。
- 移动：`390x844`，检查顶栏菜单、页签、表格横向滚动、所有主要动作、safe area 和无页面级横向溢出。
- 逐页覆盖 Workflows、New workflow 四种模式、Workflow editor canvas/YAML、Activity、Run detail 各页签、Settings 三个 section，以及 loading/empty/error/disabled 状态。
- 视觉验证必须使用真实页面和真实后端状态；截图可以作为证据，但不允许为截图注入生产 mock 数据。
