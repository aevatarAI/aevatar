---
title: "Studio Invoke Chat-like Run Diagnostics"
status: draft
owner: tbd
last_updated: 2026-04-30
references:
  - "./2026-04-20-studio-member-workbench-prd.md"
  - "./2026-04-21-studio-workflow-member-lifecycle-prd.md"
  - "../2026-04-27-member-first-studio-apis.md"
---

# Studio Invoke Chat-like Run Diagnostics

## 1. 文档定位

本文定义 `Studio / Member lifecycle / Invoke` 页的下一版交互与技术边界。

核心问题不是 `Invoke` 和 `Run` 哪个词更好，而是：

1. 用户第一次测试一个刚 bind 好的 member 时，应该在哪里完成第一轮调用
2. Invoke 页是否应该像 Chat 一样容易开始
3. Invoke 页是否应该保留 run 诊断能力
4. Invoke 页与 Runs 页面如何分工，避免双入口和重复实现

本文只定义产品与技术边界，不包含具体代码实现。

## 2. 核心结论

Invoke 页应该保留 `Invoke` 作为页面主语，但默认体验应调整为类 Chat 的执行入口。

一句话：

`Invoke Console is a chat-like execution surface for starting and observing the current run. Advanced diagnostics expose the underlying run, transport, trace, and raw events without making them the default mental model.`

中文表述：

`Invoke 调试台是一个类 Chat 的执行入口，用来发起并观察当前 run；高级诊断区暴露 run、transport、trace、raw events，但不把这些作为默认心智负担。`

## 3. 用户心智

用户进入 Invoke 页时，最常见目标是：

1. 我已经完成 Build / Bind
2. 我想给当前 member 发一句话或一次输入
3. 我想知道它是否能工作
4. 如果失败，我想知道下一步应该修 provider、route、payload、headers 还是 actor

用户此时不是优先想做：

1. 管理所有历史 runs
2. 深入阅读 raw event JSON
3. 比较多次 run
4. 查看完整 audit report

因此 Invoke 页默认不应该像完整 Runs 管理页。它应该像 Chat 一样开始，像 Run debugger 一样展开。

## 4. 页面职责边界

### 4.1 Invoke 页职责

Invoke 页负责：

1. 发起当前 member 的一次 invoke
2. 在当前页面内闭环第一次 run
3. 展示当前 run 的 streaming response / result / error
4. 展示当前 run 的轻量摘要
5. 提供高级诊断入口
6. 提供跳转 Runs 页面入口

### 4.2 Runs 页面职责

Runs 页面负责：

1. 全量 run list
2. run detail
3. trace / raw / audit 深查
4. 多 run 筛选和比较
5. resume / signal / stop 等完整生命周期管理
6. 长期排查与历史观察

### 4.3 边界图

```text
+-------------------------------+       +-------------------------------+
| Invoke                        |       | Runs                          |
|-------------------------------|       |-------------------------------|
| Start one invoke              |       | List all runs                 |
| Observe current run           |       | Inspect selected run          |
| Chat-like prompt              |       | Search/filter history         |
| Current result/error          |       | Audit/deep diagnostics        |
| Lightweight diagnostics       |       | Lifecycle management          |
| Open in Runs                  | ----> | Run detail                    |
+-------------------------------+       +-------------------------------+
```

## 5. 推荐信息架构

Invoke 页分三层。

```text
Layer 1: Chat-like invoke
  - Prompt/input
  - Invoke button
  - Streaming/current response
  - Retry / Stop

Layer 2: Current run
  - status
  - result/error
  - runId when available
  - duration
  - steps/tools count

Layer 3: Advanced diagnostics
  - endpoint
  - transport
  - request type URL
  - protobuf payload base64
  - JSON scratchpad
  - headers
  - actor id
  - timeline / events / raw
  - Open in Runs
```

## 6. 推荐页面结构

```text
Invoke
  |
  +-- Contract summary
  |     status
  |     member
  |     endpoint
  |     revision
  |     published context
  |     actor id
  |
  +-- Invoke Console
  |     Conversation | Timeline | Events
  |     current response / empty state / error card
  |     prompt input
  |     Invoke button
  |     Stop / Retry
  |
  +-- Current Run
  |     status
  |     run id if available
  |     command id
  |     actor id
  |     duration
  |     Open in Runs
  |
  +-- Advanced Diagnostics
        endpoint details
        typed payload editor
        headers
        raw request
        raw events
```

### 6.1 Design review 结论

当前文档的交互方向是对的，但布局规格还不够具体。

设计完整度评分：

```text
Before layout review: 7 / 10
After adding this section: 8.5 / 10
```

一个 10 / 10 的版本还需要真实 mockup、移动端视觉稿和最终 copy key。本文先把布局决策补到足够实现，不等待视觉稿。

### 6.2 Desktop 布局

桌面端推荐使用三段式工作台，不做完整 Runs 页面复制。

```text
+----------------------------------------------------------------------------+
| Lifecycle header                                                            |
| Build -> Bind -> Invoke -> Observe                                          |
+----------------------------------------------------------------------------+
| Contract strip                                                              |
| Ready status | member | endpoint | revision | actor id                      |
+--------------------------+-------------------------------------------------+
| Setup / Contract          | Invoke Console                                  |
|--------------------------|-------------------------------------------------|
| Endpoint mode             | Console header                                  |
| Input adapter             |   Conversation | Timeline | Events              |
| Typed payload entry       |   messages count | events count | live trace     |
| Advanced toggle           |-------------------------------------------------|
|                          | Conversation / timeline canvas                  |
|                          | empty / streaming / result / error              |
|                          |                                                 |
|                          |-------------------------------------------------|
|                          | Current run strip                               |
|                          | status | runId | commandId | duration | actions |
|                          |-------------------------------------------------|
|                          | Composer                                        |
|                          | prompt or payload summary              Invoke  |
+--------------------------+-------------------------------------------------+
```

布局比例：

1. `Contract strip` 使用单行或两行紧凑摘要，不占主舞台
2. `Setup / Contract` 宽度约 `280-320px`
3. `Invoke Console` 占剩余宽度，是页面视觉中心
4. `Current run strip` 放在 console 内部底部或顶部次级栏，不单独做大卡片
5. `Advanced Diagnostics` 默认折叠，从 Setup 区展开或作为右侧 drawer 出现

不要做：

1. 不要把 Contract、Console、Current Run、Runs History 四个大卡片纵向堆叠
2. 不要把 Runs 列表放在首屏主路径
3. 不要在主舞台同时展开 raw request、raw events、JSON scratchpad

### 6.3 主输入区

主输入区必须是第一眼就能操作的区域。

默认位置：

1. 放在 `Invoke Console` 底部，类似 Chat composer
2. 输入框占主要宽度，`Invoke` 按钮固定在右侧
3. `Stop` 只在 invoking 状态出现
4. `Retry` 只在已有失败或已有历史输入时出现

默认文案：

```text
Bound, no run:
  placeholder: Send a prompt to invoke this member
  button: Invoke

Invoking:
  placeholder: Invoking...
  button: Stop

Failed:
  primary: Retry Invoke
  secondary: Edit input
```

主输入区不应放在 Advanced 内，也不应被 contract 表单挤到页面中段以下。用户第一次 bind 后，5 秒内应该能看见下一步是输入并 Invoke。

### 6.4 Conversation / Timeline / Events

Tabs 是同一个 current run 的三种视图，不是三个不同功能区。

| Tab | 默认场景 | 内容 | 空状态 |
|---|---|---|---|
| Conversation | Workflow / GAgent / chat endpoint | user input、assistant response、tool summary、human input prompt | `No conversation yet. Send a prompt to start the run.` |
| Timeline | 调试执行过程 | run start/end、steps、tool calls、human input、retry/stop | `No events yet. Timeline appears after invoke starts.` |
| Events | 开发者诊断 | normalized events table、event count、filter、copy raw | `No raw events received yet.` |

默认选中规则：

1. chat / stream endpoint 默认 `Conversation`
2. command / typed endpoint 默认 `Timeline` 或 `Result`，避免假装是自然语言对话
3. 如果发生 error，自动显示 human-readable error card，但保留当前 tab
4. `Events` 不应作为默认 tab，除非 endpoint 是 unknown / raw

### 6.5 Current Run

`Current Run` 是当前 invoke 的状态摘要，不是 run 详情页。

推荐表现：

```text
Current run strip
  status pill
  run id when available
  command id when available
  duration
  steps/tools count
  Open in Runs
```

显示规则：

1. Bound 后无 run：显示 `Ready to invoke`，不显示 run id
2. Invoking 且无 runId：显示 `Dispatching...` 或 `Waiting for run identity...`
3. 有 runId：显示 `Current run`
4. 有 commandId 但无 runId：显示 commandId，但不显示 `Open in Runs`
5. `Open in Runs` 是二级动作，放在 strip 右侧，不做主按钮

`Current Run` 不应占据 conversation canvas 的主要面积。它的任务是给用户安全感：我知道这次调用有没有开始、有没有 run、能不能跳去深查。

### 6.6 Advanced Diagnostics

Advanced 是开发者诊断区，不是默认工作流。

建议分组：

```text
Advanced Diagnostics
  Contract
    endpoint
    request type URL
    response type URL
    stream frame format
  Target
    actor id
    revision
    published context
  Payload
    protobuf payload base64
    JSON scratchpad
    headers
  Raw
    normalized events
    raw error
    copy request
```

展开方式：

1. 桌面端可从左侧 Setup 区展开
2. 窄屏端用 drawer 或 accordion
3. raw 内容默认折叠
4. copy 动作必须贴近字段，而不是只有整块复制
5. 敏感字段按第 19 节脱敏

Advanced 的视觉权重必须低于主输入区和 current response。否则这个页面会重新退化成“开发者表单 + raw JSON”。

### 6.7 Script typed payload 入口

Script command endpoint 的核心设计要求：入口可见，但不抢走 chat-like 主路径。

推荐摆放：

```text
Setup / Contract
  Endpoint mode: Command
  Typed payload
    [Edit payload] primary secondary button
    request type URL preview
    payload validation status

Invoke Console composer
  payload summary chip
  optional prompt/helper text
  Invoke
```

行为规则：

1. command / typed endpoint 不默认显示纯 prompt composer
2. 首屏必须能看到 `Typed payload` 入口
3. `JSON scratchpad` 必须标注它不会自动变成 protobuf bytes，除非实现真的支持转换
4. payload invalid 时，主错误指向 payload editor
5. payload valid 且 accepted 后，优先显示 accepted receipt / commandId；只有拿到 runId 才显示 Current Run

如果一个 script endpoint 同时支持 prompt 和 typed payload，应采用 split composer：

```text
Primary input: prompt
Secondary input: typed payload summary + Edit
```

不要把 protobuf base64 textarea 放在主 conversation canvas 里。那会把第一次调用体验毁掉。

### 6.8 Responsive 与键盘规则

响应式布局：

| Viewport | 布局 |
|---|---|
| `>=1280px` | 左 Setup，右 Invoke Console，Advanced 可 drawer |
| `768-1279px` | Contract strip + Invoke Console，Setup 折叠为 top accordion |
| `<768px` | 单列：Contract summary、Console、Current run、Advanced accordion |

键盘与可访问性：

1. `Cmd/Ctrl + Enter` 触发 Invoke
2. `Esc` 在 invoking 时不直接 stop，避免误触；stop 必须确认或明确按钮
3. Tabs 使用 arrow key 切换
4. Advanced accordion 使用 button + `aria-expanded`
5. status pill 需要文本，不只靠颜色
6. 所有 icon button 必须有 tooltip 或 `aria-label`

## 7. Bind 后与 First Run 语义

Bind 后通常不存在 `runId`。

原因：

1. Bind 只确认当前 member 可以被调用
2. Bind 建立 invoke target / endpoint / revision / published context / actor binding
3. run 是一次 invoke 后产生或推进的执行事实

状态机：

```text
[Bound, no run]
  |
  | user clicks Invoke
  v
[Invoking]
  |
  | backend emits run identity
  v
[Current run available]
  |
  +-- completed
  +-- failed with runId
  +-- waiting for human input
  +-- stopped

[Invoking]
  |
  | failure before run creation
  v
[Invoke failed before run]
```

UI 规则：

1. Bind 后不显示 `Open this run`
2. Bind 后显示 `Ready to invoke`
3. 第一次 invoke 后，如果拿到 runId，显示 `Current run`
4. 如果失败但没有 runId，显示 `Invoke failed before a run was created`
5. 如果失败且有 runId，显示 `Run failed` 和 `Open in Runs`

## 8. Run Identity 与导航契约

Invoke 页必须区分发起调用时的临时 UI 状态、命令追踪标识、run 身份和 actor 身份。

| 标识 | 来源 | 何时存在 | UI 用途 | 约束 |
|---|---|---|---|---|
| `clientSessionId` | 前端本地生成 | 用户点击 Invoke 后立即存在 | 关联当前页面内的 pending invoke | 只用于 UI 关联，不得作为后端事实 |
| `commandId` | invoke receipt 或事件流 | dispatch 被接受后可能存在 | 展示命令追踪、技术诊断 | 不等同于 `runId`，不得用于 Runs 详情跳转 |
| `runId` | invoke 事件流或 readmodel | run 被创建或识别后才存在 | 展示 Current Run、启用 `Open in Runs` | Bind 后不得假设存在；缺失时不得伪造 |
| `actorId` | bind 结果或事件流 | bind 后通常存在 | 展示目标 actor、诊断路由 | 只表示目标身份，不表示本次执行 |

导航规则：

1. 只有存在真实 `runId` 时，才显示 `Open in Runs`
2. 如果只有 `commandId`，显示为技术追踪信息，不提供 Run 详情跳转
3. 如果失败发生在 run 创建前，只保留本页错误卡和 retry
4. 如果事件流后续补充 `runId`，当前页面应从 pending session 升级为 current run
5. `clientSessionId` 只用于前端合并流式事件和本地状态，不写入业务语义

## 9. API / Event Schema 对照表

本节描述当前前端可依赖的 invoke API、SSE 事件归一化结果，以及 UI 汇总字段的来源。

### 9.1 Invoke request

当前流式 invoke 入口：

```text
POST /scopes/{scopeId}/services/{serviceId}/invoke/{endpointId}:stream
```

前端请求体：

| 字段 | 来源 | 必填 | 用途 | 约束 |
|---|---|---:|---|---|
| `prompt` | Invoke 输入框 | 是 | chat / stream endpoint 的主输入 | typed command 不应只依赖该字段表达核心 payload |
| `headers` | Advanced headers editor | 否 | 传递调用上下文 | 默认折叠；展示和复制时需要脱敏 |
| `actorId` | Bind 结果或 Advanced override | 否 | 指定目标 actor | 只表示目标身份，不等同于 run |
| `inputParts` | 多模态输入 | 否 | 图片、文件、媒体等输入片段 | 需要与 endpoint capability 匹配 |

现有实现中，前端还会创建本地 `clientSessionId` 作为 `InvokeHistoryEntry.id`，用于在页面内关联 pending invoke、events 和 current result。它不属于后端 API 契约。

### 9.2 SSE frame normalization

后端 SSE frame 进入前端后先归一化为 flat runtime event。当前前端需要同时兼容两种形态：

```text
Typed + nested:
  { type: "RUN_STARTED", runStarted: { runId: "...", threadId: "..." } }

Oneof only:
  { runStarted: { runId: "...", threadId: "..." }, timestamp: 123 }

Normalized:
  { type: "RUN_STARTED", runId: "...", threadId: "...", timestamp: 123 }
```

UI 不应直接依赖后端原始 oneof 结构，而应依赖归一化后的 runtime event。

### 9.3 Event to UI mapping

| 事件类型 | 关键字段 | UI 汇总影响 | 说明 |
|---|---|---|---|
| `RUN_STARTED` | `runId`, `threadId` / `actorId` | status = `running`; 填充 `runId` / `actorId` | 第一个可靠 run identity 来源 |
| `RUN_FINISHED` | `runId`, `threadId` / `actorId` | status = `completed` | 仅在未处于 error / stopped / needs-input 时覆盖 |
| `TEXT_MESSAGE_CONTENT` | `delta` | 追加 `textOutput` | Conversation 主输出 |
| `TEXT_MESSAGE_END` | `messageId` | 可标记完成 | 可能没有 `runId` |
| `STEP_STARTED` | `stepName` | 增加 step count | Timeline / Events 使用 |
| `STEP_FINISHED` | `stepName` | Timeline step done | 不直接代表 run 完成 |
| `TOOL_CALL_START` | `toolCallId`, `toolName` | 增加 tool call count | Tool timeline 使用 |
| `TOOL_CALL_END` | `toolCallId`, `result` | Tool result 展示 | result 默认进入诊断区或 timeline |
| `HUMAN_INPUT_REQUEST` | `runId`, `stepId`, `prompt`, `options` | status = `needs-input` | 显示 Continue / human input UI |
| `HUMAN_INPUT_RESPONSE` | `runId`, `stepId`, `approved`, `userInput` | status = `submitted` | 表示用户输入已提交，不等于 run 完成 |
| `RUN_STOPPED` | `runId`, `reason` | status = `stopped` | 用户 stop 或后端停止 |
| `RUN_ERROR` / `ERROR` | `message`, `code` | status = `error` | 需要进入错误分类逻辑 |
| `CUSTOM` | `name`, `payload` | 按 custom name 补充语义 | 例如 `aevatar.human_input.request`、`aevatar.step.completed` |
| `STATE_SNAPSHOT` | `snapshot` | Advanced / raw only | 不作为主输出事实 |

### 9.4 UI summary contract

前端 current run summary 至少包含：

| UI 字段 | 来源 | 缺失行为 |
|---|---|---|
| `status` | 事件归纳 | 默认 `idle`；pending 本地 invoke 可显示 invoking |
| `actorId` | `RUN_STARTED.threadId`、`RUN_FINISHED.threadId`、事件 `actorId`、bind actor | 缺失时显示 `-`，不得推导 |
| `runId` | `RUN_STARTED.runId`、`RUN_FINISHED.runId`、human input event payload | 缺失时不显示 Run 跳转 |
| `commandId` | accepted/context event、custom payload、non-stream invoke receipt | 缺失时不显示命令追踪字段 |
| `correlationId` | accepted/context event、custom payload、response header、non-stream invoke receipt | 缺失时不显示 correlation 字段 |
| `textOutput` | `TEXT_MESSAGE_CONTENT.delta` 或 step completed custom payload | 缺失时显示 empty / no response |
| `errorMessage` | `RUN_ERROR.message` 或 catch error | 缺失时显示通用错误摘要 |
| `errorCode` | `RUN_ERROR.code`、pre-stream JSON error `code`、catch error payload | 缺失时使用 generic error 分类 |
| `humanInputPrompt` | `HUMAN_INPUT_REQUEST.prompt` 或 custom payload | 缺失时不显示 Continue |
| `eventCount` | events length | 可为 0 |
| `stepCount` | distinct `STEP_STARTED.stepName` | 可为 0 |
| `toolCallCount` | distinct `TOOL_CALL_START.toolCallId/toolName` | 可为 0 |
| `lastEventType` | 最后一个 event type | 可为空 |

### 9.5 Contract gaps

当前文档目标比现有实现多出几个契约要求。落地时需要确认后端或 service catalog 是否已经提供：

1. `endpoint.kind` 的强类型来源
2. endpoint 是否支持 streaming、typed payload、multi-modal input 的 capability
3. run identity 是否保证在 `RUN_STARTED` 或等价事件中出现
4. invoke accepted receipt 是否返回 `commandId` / `correlationId`
5. raw error 是否能提供稳定 `code` 以支持错误分类

如果这些字段暂时不可用，前端必须走明确 fallback：

1. 没有 endpoint kind：进入 contract-first request composer
2. 没有 runId：不显示 `Open in Runs`
3. 没有 error code：显示 generic error，并把 raw 放入 Advanced
4. 没有 commandId：不显示命令追踪字段

### 9.6 Contract gap 核对结论

基于当前代码核对后，缺口状态如下：

| 项 | 当前状态 | 结论 |
|---|---|---|
| `endpoint.kind` | 后端 `ServiceEndpointKind` 已有 `Command / Chat`，service catalog snapshot 也会暴露 endpoint `kind` | 已满足；前端可以依赖该字段作为基础分类 |
| endpoint capability | endpoint contract response 已有 `SupportsSse`、`SupportsAguiFrames`、`StreamFrameFormat`、`DefaultSmokeInputMode`、`SampleRequestJson` | 后端基本满足；前端 Invoke 需要消费这些 contract，而不是只看 `kind` |
| `runId` stream 语义 | Workflow / GAgent / Script stream 都会注册 service run，并发送与 registry 对齐的 `RunStarted.runId` | 基本满足；UI 可以把 `RUN_STARTED.runId` 作为 `Open in Runs` 的前提 |
| `commandId` / `correlationId` | 非 stream invoke receipt 有字段；stream 场景通过 context/custom/header 暴露不完全统一 | 部分满足；前端 summary 需要独立提取，且不得把 `commandId` 当 `runId` |
| error `code` | pre-stream JSON error 有稳定 `code`；stream 内 `RunError` 有时只有 message | 部分满足；错误分类必须允许 generic fallback |

因此，Phase 0 的实现重点不是补一套新后端 API，而是让前端 Invoke 消费已有 endpoint contract，并补齐 current run summary 的身份字段。

### 9.7 前端实现策略

Phase 0 建议作为 Invoke v2 之前的地基补丁：

1. API client 增加 `getEndpointContract(scopeId, serviceId, endpointId)`
2. Invoke 页在选择 service / endpoint 后加载 endpoint contract
3. `getStreamableInvokeEndpoints` 优先使用 `contract.supportsSse`
4. `getInvokeSurfaceSupport` 优先使用 endpoint contract 判断当前 Invoke surface 是否支持
5. command / typed endpoint 使用 `DefaultSmokeInputMode` 和 `SampleRequestJson` 引导 contract-first composer
6. `InvokeRunSummary` 增加 `commandId`、`correlationId`、`errorCode`
7. summary 从 `aevatar.run.context`、`RUN_ERROR.code`、pre-stream error payload、non-stream receipt 中提取诊断字段
8. UI 只在真实 `runId` 存在时展示 `Open in Runs`

这一步不改变后端 invoke API，不改变 Runs / Observe 页面，也不做大布局重构。

### 9.8 已验证的 runId 语义

当前 stream run identity 的实现语义：

| 实现类型 | `runId` 来源 | `commandId` 来源 | `Open in Runs` 基础 |
|---|---|---|---|
| Workflow | workflow run actor id，也就是 accepted receipt 的 `ActorId` | accepted receipt `CommandId` | service-run registry 使用同一个 run actor id 注册 |
| GAgent | accepted receipt `CommandId` | accepted receipt `CommandId` | service-run registry 使用 command id 作为 run id 注册 |
| Script | 服务端生成的 guid | 当前实现中通常与 generated run id 对齐 | service-run registry 使用同一个 generated run id 注册 |

UI 约束：

1. `runId` 是 Runs 详情跳转的唯一依据
2. `commandId` 是追踪字段，不是 Runs 跳转字段
3. GAgent 当前实现中 `runId == commandId` 是实现事实，不应推广成通用规则
4. Script 当前 stream chat 会生成 run id；typed command invoke 是否产生同样 run identity，仍应以对应 endpoint contract / receipt / readmodel 为准

### 9.9 Error code 限制与 fallback

错误分类不能假设所有失败都有稳定 `code`。

当前事实：

1. pre-stream 失败通常返回 JSON `{ code, message }`
2. stream 已开始后，`RunError` 可能携带 `code`
3. 部分 stream error 只有 `message`
4. 浏览器 abort / network error 可能只有本地异常名

UI fallback：

1. 有 `code`：优先进入明确错误分类
2. 无 `code` 但有 HTTP status：按 status 粗分
3. 无 `code`、无 status：显示 generic invoke error
4. raw error 始终放入 Advanced
5. 用户首屏只展示可行动摘要，不用 raw JSON 替代错误解释

## 10. 不同 Member 类型适配

Invoke 页面应统一框架，不应统一输入方式。

### 10.1 Workflow member

Workflow member 默认适合 prompt-first。

推荐：

1. 默认显示类 Chat 输入
2. Conversation / Timeline / Events 作为主 tabs
3. Advanced 中放 route、headers、raw events

### 10.2 GAgent member

GAgent member 默认适合 chat-like 或 agent action。

推荐：

1. 默认显示 prompt 输入
2. 主结果强调 response 和 tool calls
3. Advanced 中放 actor id、headers、raw events

### 10.3 Script member

Script member 需要区分 chat-like script 与 typed command。

如果 endpoint 是 command / typed payload：

1. 不应强行只显示自然语言 prompt
2. typed payload 入口必须可见
3. protobuf payload 和 JSON scratchpad 可放入 Advanced，但不能藏到用户找不到

推荐规则：

```text
chat / stream endpoint
  default input: prompt

command / typed endpoint
  default input: payload editor or prompt + typed payload side-by-side

unknown / raw endpoint
  default input: contract-first request composer
```

### 10.4 Endpoint kind 的权威来源

前端不应通过 endpoint 名称、type URL 字符串片段或 member 名称猜测输入方式。

推荐的判定来源按优先级为：

1. bind / contract 返回的强类型 `endpoint.kind`
2. request / response type 的强类型声明
3. stream capability 或 chat capability 声明
4. 明确的 member type adapter
5. unknown fallback：contract-first request composer

禁止规则：

1. 禁止用 `TypeUrl.Contains("Chat")` 或类似字符串规则决定主交互
2. 禁止因为 UI 想做 Chat，而把 typed command 的核心 payload 降级为自由文本
3. 禁止在没有权威 kind 时默认假设为 chat endpoint

## 11. 错误展示规则

Invoke 失败时，默认不直接展示 raw JSON。

推荐三层：

```text
Layer 1: Human-readable error
  title
  explanation
  next action

Layer 2: Technical details
  HTTP status
  route
  model
  endpoint
  commandId
  actorId
  runId

Layer 3: Raw error
  full JSON
  upstream payload
```

示例：

```text
Provider not connected
This invoke used the openai provider, but it is not connected.

[Open Providers] [Change route] [Retry Invoke]

Technical details
  HTTP 400
  route: nyxid
  model: gpt-5.4
```

### 11.1 错误分类

错误卡需要基于错误类型给出不同的行动建议。

| 错误类型 | 是否有 `runId` | 默认展示 | 主操作 |
|---|---:|---|---|
| bind not ready | 否 | Member is not ready to invoke | Return to Bind |
| route/provider not configured | 否 | Provider or route is not connected | Open Providers / Change route |
| payload validation failed | 否 | Request payload is invalid | Edit payload |
| dispatch rejected | 否 | Invoke was rejected before a run was created | Retry / View details |
| stream interrupted | 不确定 | Invoke stream was interrupted | Retry / View technical details |
| run failed | 是 | Run failed | Open in Runs / Retry |
| waiting for human input | 是 | Run is waiting for input | Continue |
| stopped or aborted | 可能 | Invoke was stopped | Retry |

约束：

1. 错误发生在 run 创建前，不显示 `Open in Runs`
2. 错误发生在 run 创建后，保留当前 run 摘要和 `Open in Runs`
3. raw error 默认折叠，但必须可复制
4. 用户动作优先指向下一步修复对象，而不是只展示异常名

## 12. Run History 处理

Invoke 页不承载完整 Run History。

推荐：

1. 默认只展示当前 run
2. 可展示最近一次或少量最近 runs 的轻量摘要
3. 完整历史进入 Runs 页面
4. `Open in Runs` 是二级动作，不是主流程

不推荐：

1. 在 Invoke 页复制完整 Runs 页面
2. 在 Invoke 页做多 run 筛选
3. 在 Invoke 页做完整 compare / audit

## 13. Observe / Runs 边界

Invoke、Observe、Runs 三者都可能展示运行信息，但职责不同。

| 页面 | 主问题 | 允许展示 | 不承担 |
|---|---|---|---|
| Invoke | 我现在能否调用这个 member，并看到当前结果 | 当前 invoke、当前 run、轻量 timeline/events | 全量历史管理、长期观察 |
| Observe | 这个 member / actor 当前是否可观察、是否在发布事件 | observation 状态、subscription、projection 可见性 | 发起 invoke、编辑 payload |
| Runs | 某个 run 或历史 runs 发生了什么 | run list、run detail、trace、audit、生命周期操作 | member lifecycle 的第一入口 |

因此：

1. Invoke 页只观察当前调用闭环
2. Observe 页不成为发起调用入口
3. Runs 页不替代 member lifecycle 的 Invoke 步骤
4. 三者可共享展示组件，但不共享页面主语

## 14. 与当前 Script Invoke 原型的关系

当前 Script Invoke 页面已经具备：

1. 调用契约
2. 调试台
3. typed request 信息
4. 当前结果
5. Runs 列表
6. run 详情

主要问题是信息层级偏开发者：

1. request type URL、protobuf payload、JSON scratchpad 过早占据主舞台
2. 当前 run 的 conversation-like 体验不够突出
3. Runs 列表在 Invoke 页中容易让职责边界变模糊

建议调整：

1. 保留调用契约摘要
2. 把 prompt / Invoke / current result 移到主舞台
3. 将 typed request composer 移入 Advanced 或侧栏 Setup
4. 将 Runs 列表弱化为 `Current run` + `Open in Runs`
5. 保留 trace / raw，但默认不压过 conversation

## 15. 与 Run Console 原型的关系

Run Console 红框区域中的交互骨架可复用：

1. Conversation / Timeline / Events tabs
2. 大面积 conversation 空间
3. 底部 prompt 输入
4. 顶部消息、事件、trace 状态

但不应复用页面主语。

Invoke 页仍应叫 `Invoke` 或 `Invoke Console`，不应改成 `Run Console`。

原因：

1. 用户仍处于 member lifecycle 的 Invoke 步骤
2. 发起动作是 invoke，不是浏览 run
3. Run Console / Runs 页面应保留历史观察职责

## 15.1 现有前端差距核对

核对对象：

1. `tools/Aevatar.Tools.Cli/Frontend/src/runtime/InvokeWorkbench.tsx`
2. `tools/Aevatar.Tools.Cli/Frontend/src/runtime/ScopePage.tsx`
3. `tools/Aevatar.Tools.Cli/Frontend/src/runtime/invokeUtils.ts`
4. `tools/Aevatar.Tools.Cli/Frontend/src/runtime/chatTypes.ts`
5. `tools/Aevatar.Tools.Cli/Frontend/src/api.ts`

结论：现有实现已经有可复用的 streaming invoke 基础，但仍是 `Playground + AGUI events + Request history` 的开发者工作台结构。要达到本文推荐布局，需要先补数据契约和类型判断，再做 UI 重排。

| 目标区域 | 现有状态 | 差距 | 建议落点 |
| --- | --- | --- | --- |
| 主输入区 | `InvokeWorkbench` 左侧 `Playground` 中已有 prompt textarea、Invoke、Stop、Load fixture、Replay last | 输入区在左侧表单里，不在主舞台底部；顶部和中部都有 invoke 动作，用户焦点分散 | Phase 1 把 composer 移到主 console 底部；保留一个主 Invoke；Stop 仅 running 时出现 |
| Conversation | 现有 `bubbles` view 可表达 assistant message，但默认 mode 是 `timeline` | 没有命名为 `Conversation` 的默认 tab；chat-like 输出不是默认心智 | Phase 1 将 mode 收敛为 `conversation / timeline / events`；stream/chat endpoint 默认 `conversation` |
| Timeline | `TimelineView`、step/tool/human input frame builder 已可复用 | 目前与 `trace/tabs/bubbles/raw` 并列，模式过多 | Phase 1 保留为二级 tab；workflow/gagent 调试默认可切到 Timeline |
| Events | `RawView` 已能展示 events；`debugEvents` 也可复用 | 现在叫 `raw`，且在主模式里直接暴露；缺少 filter/copy/脱敏分组 | Phase 1 改名为 Events；raw payload 放在 Events 内部或 Advanced 子组 |
| Advanced | 已有 `Advanced options`，包含 actor id 和 headers | 只覆盖少量调用参数；contract、target、payload、raw diagnostics 没有按组归档 | Phase 1 扩展为 `Target / Headers / Contract / Raw`，默认折叠 |
| Current Run | 右侧 `Run summary` 已展示 status、actor、run、steps、tool calls | 位置偏诊断侧栏；未表达 commandId/correlationId/errorCode；没有 `Open in Runs` 契约 | Phase 0 先扩展 summary model；Phase 1 放到主舞台顶部轻量 strip |
| Run History | 左侧有 session-local `Request history`，支持 replay/compare | 容易与 Runs 页面职责混淆；当前页面内权重偏高 | Phase 1 改成轻量 recent list；深查跳 Runs 页；compare 可后置 |
| Script typed payload | command-only service 当前被 `getInvokeSurfaceSupport` 阻止并引导去 Raw | 与本文目标冲突：typed command 应留在 Invoke 页面，而不是丢到 Raw | Phase 0 引入 endpoint contract；Phase 1 在左侧 Setup/Contract 或 composer 上方提供 typed payload editor |
| Endpoint contract | `ServiceEndpoint` 只有 `kind/requestTypeUrl/responseTypeUrl/description`；API 没有前端 `getEndpointContract` 调用 | 无法判断 `supportsSse/supportsAguiFrames/defaultSmokeInputMode/sampleRequestJson` | Phase 0 新增前端 contract type 和 API client；streamable 判断优先 contract |
| runId / commandId | `runId` 从 events summary 提取；resume response 临时记录 commandId/correlationId | `InvokeRunSummary` 没有 commandId/correlationId/errorCode；`runId` 与 commandId 的语义分离未进入 UI | Phase 0 扩展 summary、history entry 和 tests；只有 runId 启用 `Open in Runs` |
| inputParts | `api.scope.streamInvoke` 支持 `inputParts` 参数 | Invoke tab 的 `buildInvokeRequestPayload` 和 UI 没有附件/parts 入口 | Phase 2 再统一到 composer，不阻塞首轮布局 |

### 15.1.1 可复用资产

1. `buildInvokeWorkbenchFrames` 已经能把 run、step、tool、human input、assistant message 转成展示 frame。
2. `summarizeInvokeEvents` 已有状态、actorId、runId、文本输出、错误、human input 摘要。
3. `InvokeHistoryEntry` 已有本地 session history，可降级为 `Recent runs`。
4. `TimelineView`、`RawView`、`HumanInputCard` 可直接复用到新布局。
5. `api.scope.streamInvoke` 已支持 endpointId、headers、actorId、inputParts，主调用接口不用重写。

### 15.1.2 必补缺口

1. 新增 `ServiceEndpointContract` 前端类型，包含 `supportsSse / supportsAguiFrames / streamFrameFormat / defaultSmokeInputMode / sampleRequestJson`。
2. `getStreamableInvokeEndpoints` 不再只看 `endpoint.kind === chat`；优先看 endpoint contract，兼容旧 kind。
3. `InvokeRunSummary` 增加 `commandId / correlationId / errorCode`，并从 `RUN_STARTED`、`RUN_ERROR`、custom context、resume response 中提取。
4. `InvokeWorkbenchMode` 收敛为 `conversation / timeline / events`；原 `trace/tabs/bubbles/raw` 作为内部 view 或 Advanced 子项。
5. typed command endpoint 不再被阻止进入 Invoke 页面；根据 contract 默认展示 typed payload editor。
6. `Open in Runs` 只能在拿到 `runId` 后启用；没有 runId 时显示 accepted / streaming / waiting 等诚实状态。

### 15.1.3 实施顺序建议

1. Phase 0：只改类型、contract 获取、summary 提取和测试，不动大布局。
2. Phase 1：重排 `InvokeWorkbench`，把主输入区、Current Run、Conversation/Timeline/Events、Advanced 按本文布局落位。
3. Phase 1.5：补 typed payload editor，把 command endpoint 纳入 Invoke。
4. Phase 2：处理附件 `inputParts`、history compare、跨页面 Runs 深链。

## 16. 分阶段实施建议

### Phase 1: 只改 Invoke 页面

目标：

1. 默认体验改为类 Chat
2. 保留当前 run 轻量观察
3. 高级诊断折叠
4. 不改后端 API
5. 不改 Runs 页面

范围：

1. Invoke 页布局
2. 输入区位置
3. 当前结果展示
4. Conversation / Timeline / Events tabs
5. Advanced diagnostics
6. Open in Runs 入口

不做：

1. 重命名 `/invoke` API
2. 重命名 invoke state / helper
3. 抽公共 Run 组件
4. 改 Observe / Runs 页面

### Phase 2: 抽取可复用 Current Run 组件

触发条件：

1. Phase 1 体验验证成立
2. Invoke 和 Runs 出现明显重复展示逻辑

候选组件：

1. `RunConversationPanel`
2. `RunTimelinePanel`
3. `RunDiagnosticsPanel`
4. `RunErrorCard`

## 17. Feature Flag / Rollout 策略

Invoke 页改版应先小范围启用，避免一次性改变所有 member 的调试入口。

### 17.1 Rollout 原则

1. 默认不改后端 API
2. 默认不改 Runs / Observe 页面
3. 新旧 Invoke UI 在短期内可通过 feature flag 切换
4. 先覆盖 streaming chat endpoint，再覆盖 typed command endpoint
5. 如果 endpoint kind 不明确，保守回退到旧的 contract-first 或 Raw 入口

### 17.2 建议 flag

| Flag | 默认 | 作用 | 回滚方式 |
|---|---|---|---|
| `studio.invoke.console.v2` | off in production, on in local/dev | 启用新版 Invoke Console 布局 | 关闭后回到旧 Invoke workbench |
| `studio.invoke.advancedDiagnostics.v2` | on when v2 on | 启用新的 Advanced 分组和脱敏 | 关闭后保留 raw-only 诊断 |
| `studio.invoke.scriptTypedAdapter.v2` | off initially | 为 script command endpoint 启用 typed payload 主输入 | 关闭后回到 Raw / contract-first |
| `studio.invoke.runHistoryInline.v2` | off initially | 显示少量 recent runs 摘要 | 关闭后只展示 current run |

### 17.3 Rollout 阶段

| 阶段 | 范围 | 目标 | 退出条件 |
|---|---|---|---|
| Stage 0 | local/dev only | 验证布局和事件映射 | smoke test 通过 |
| Stage 1 | Workflow / GAgent chat endpoint | 验证 prompt-first 体验 | 无 runId 混用、无 Open in Runs 误跳转 |
| Stage 2 | Script chat endpoint | 验证 script 的 chat-like endpoint | request type / actor / trace 仍可诊断 |
| Stage 3 | Script command endpoint | 验证 typed payload adapter | protobuf / JSON scratchpad 不被 prompt 替代 |
| Stage 4 | 默认开启 v2 | 替换旧 Invoke UI | 关键指标稳定 |

### 17.4 回滚触发条件

任一条件满足时，应关闭对应 flag，而不是继续扩大范围：

1. `runId` 与 `commandId` 出现 UI 混用
2. typed command endpoint 被错误渲染成纯 chat prompt
3. `Open in Runs` 跳转到不存在或错误 run
4. Advanced 中出现未脱敏 secret / token
5. 用户无法从 Bind 后明确知道下一步如何发起 Invoke
6. Workflow / GAgent 的 prompt-first 主流程被 command payload 表单阻断

### 17.5 观测指标

上线后至少观察：

1. Bind 后首次 Invoke 点击率
2. 首次 Invoke 成功率
3. Invoke 到 first event 的耗时
4. Invoke 失败后的 retry 率
5. `Open in Runs` 点击率和跳转成功率
6. Advanced 打开率
7. raw error 复制率
8. typed command endpoint 的 payload validation failure 率

这些指标只用于判断交互是否有效，不改变后端 ACK 或 run 完成语义。

## 18. 技术约束

1. 后端 API 保持 `invoke` 语义
2. `runId` 不应在 Bind 后假设存在
3. run 观察必须来自 invoke 事件流或 readmodel，不做 query-time replay
4. Invoke 页不维护跨节点事实状态
5. typed command 的核心语义不能降级成无结构 prompt
6. Advanced 中的 request payload、headers、actor id 仍应可复制和复现
7. endpoint kind 必须来自权威契约或明确 fallback，不通过字符串猜测
8. `commandId`、`runId`、`actorId` 的 UI 语义必须分离

## 19. 安全与脱敏

Advanced diagnostics 会暴露底层诊断信息，必须默认保护敏感数据。

要求：

1. headers、provider config、raw payload 中的 token、secret、authorization 默认脱敏
2. 复制 raw 内容时保留明确提示，避免误贴敏感信息
3. actor id、command id、run id 可复制，但不应被描述为访问凭证
4. raw upstream payload 默认折叠
5. 错误卡默认展示可行动摘要，不把完整 provider response 暴露在首屏
6. 如果未来支持分享 run detail，分享内容必须基于脱敏后的 readmodel 或导出视图

## 20. 测试建议

### 20.1 通用测试

1. Bind 后无 runId，显示 `Ready to invoke`
2. 用户输入 prompt 后点击 Invoke，当前页面进入 invoking 状态
3. 事件流返回后显示 current run
4. 失败且无 runId 时，不显示 `Open this run`
5. 失败且有 runId 时，显示 `Open in Runs`
6. Advanced 默认折叠
7. 展开 Advanced 后可看到 endpoint、headers、actor id、raw
8. 只有 commandId 但没有 runId 时，不显示 `Open in Runs`
9. stream 后续补充 runId 时，当前 pending session 升级为 Current Run
10. headers 和 raw payload 中的敏感字段默认脱敏

### 20.2 Workflow / GAgent

1. 默认 prompt-first
2. Conversation tab 默认选中
3. Timeline / Events 可查看
4. tool call / tool result 能在 Timeline 或 Events 中查看

### 20.3 Script command

1. typed payload 入口可见
2. request type URL 可查看
3. protobuf payload base64 可输入
4. JSON scratchpad 不被误认为会自动转 protobuf
5. raw events 可查看
6. command / typed endpoint 不被默认渲染成纯 chat prompt
7. unknown endpoint 默认进入 contract-first request composer

### 20.4 错误状态

1. bind not ready 显示 Return to Bind
2. provider not configured 显示 Open Providers / Change route
3. payload validation failed 聚焦 payload editor
4. dispatch rejected 不显示 Run 跳转
5. run failed 显示 Run 摘要和 `Open in Runs`
6. stream interrupted 保留已收到的 partial events，并提供 retry

### 20.5 Rollout / flag

1. `studio.invoke.console.v2` off 时旧 Invoke UI 可用
2. `studio.invoke.console.v2` on 时新布局可用
3. 关闭 `studio.invoke.scriptTypedAdapter.v2` 后 script command endpoint 回退到 Raw / contract-first
4. feature flag 不改变 invoke API 请求路径和 body 语义
5. flag 切换不丢失当前页面内未提交输入

## 21. 成功标准

1. 首次 bind 后，用户知道下一步是输入并 Invoke
2. 第一次 run 不需要跳转 Runs 页面即可完成闭环
3. 失败时用户先看到可行动建议，而不是 raw JSON
4. 开发者仍能展开 trace/raw/headers/payload 排查
5. Workflow / GAgent 不被 script typed payload 表单拖累
6. Script command 不被过度 Chat 化
7. Runs 页面继续承担历史和深度诊断职责
8. runId 缺失、commandId 存在、actorId 存在这三种状态不会被 UI 混用
9. endpoint kind 不明确时，UI 退回 contract-first，而不是假装成 Chat
10. Advanced diagnostics 可复现问题，但默认不泄露敏感内容
11. v2 可以通过 feature flag 快速回滚
12. API / event schema 映射能解释每个主 UI 字段的来源

## 22. 最终建议

采用 Run Console 的主交互骨架，但保留 Invoke 的页面主语。

最终形态：

```text
Invoke page
  chat-like by default
  current-run aware
  diagnostics available
  runs page linked, not duplicated
```

这是当前最合理的产品边界。
