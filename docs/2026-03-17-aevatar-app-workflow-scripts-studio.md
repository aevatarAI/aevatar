# Aevatar App Workflow / Scripts Studio 重构

## 目标

`tools/Aevatar.Tools.Cli` 的前端不再按通用 playground 组织，而是收敛为两个一等能力：

1. `Workflow Studio`
2. `Scripts Studio`

整体思路参考 `~/Code/aevatar-workflow` 的 studio 形态，但保留 `aevatar app` 现有的本地宿主、内嵌 capability 与运行时接线方式。

## 统一约束

- `scope_id` 统一固定为 `aevatar`。
- workflow 与 scripts 都通过 CLI 宿主暴露的 capability API 进入后端，不在前端拼业务协议。
- `aevatar app` 只有一套后端 API：`Aevatar.Mainnet.Host.Api`。不再把 workflow 相关能力拆成独立 workflow host。
- LLM 只负责生成 `workflow yaml` 或 `C# source`；发布、运行、观察都走正式 capability。
- workflow 继续走 `GAgentServices` 已经接入的 scope workflow 能力。
- scripts 不直接把“生成代码”视为上线；必须区分草稿运行与正式 promotion。

## Workflow Studio

### 前端布局

- 左侧保留资源导航：
  - Published Workflows
  - Bundled Library
- 主区拆成三块：
  - `Workflow Graph`
  - `Workflow YAML`
  - `Run Activity`
- `Ask AI` 固定接在 workflow 画布右下角边缘，作为当前画布上下文的一部分，而不是单独弹窗。

### AI 生成路径

- 前端将用户请求与当前 YAML 一并送入 `/api/app/chat`。
- metadata 固定包含：
  - `scope_id = aevatar`
  - `workflow.authoring.enabled = true`
  - `workflow.intent = workflow_authoring`
- 返回约束为单个 fenced `yaml` block。
- 前端提取 YAML 后立即本地调用 `/api/playground/parse` 校验，并更新画布。

### 发布与运行

- 发布走 `MapUserWorkflowCapabilityEndpoints()` 暴露的 user workflow API。
- 查询 published workflow 明细时，统一走 mainnet 的 `GET /api/scopes/{scopeId}/workflows/{workflowId}`；后端先通过 `IUserWorkflowQueryPort` 读取发布态，再尝试从 `IServiceRevisionArtifactStore` 拿 active revision source。
- draft run 继续走 `app chat` 的 workflow yaml inline 执行。
- published run 走 `POST /api/scopes/{scopeId}/workflows/{workflowId}/runs:stream`。
- published run 请求 `eventFormat = agui` 时，mainnet 直接把 AGUI 事件 SSE 推给前端，前端据此展示 workflow execution 过程与 human-input 等待态。

## Scripts Studio

### 为什么单独建模

scripts 的问题不只是“多一个文本编辑器”，而是存在两条不同语义：

1. `draft runtime`
2. `catalog promotion`

前者用于快速试错，后者才是正式进入 script catalog 的业务动作。两者不能共用一个按钮语义，也不能让前端通过一次 LLM 生成就默认等价于“已发布脚本”。

### 建议的三段式模型

#### 1. Draft Authoring

- 前端维护本地 draft：
  - `scriptId`
  - `revision`
  - `aiPrompt`
  - `source`
  - `runInput`
- LLM 仅生成完整 C# 文件，不直接触发 promotion。

#### 2. Draft Run

- 新增 `/api/app/scripts/draft-run` 作为 app 专用草稿运行入口。
- 宿主内流程：
  1. `IScriptDefinitionCommandPort.UpsertDefinitionWithSnapshotAsync`
  2. `IScriptRuntimeProvisioningPort.EnsureRuntimeAsync`
  3. `IScriptRuntimeCommandPort.RunRuntimeAsync`
- 这样前端不需要理解 definition actor、runtime actor、snapshot 细节，只消费 app 级协议。

#### 3. Promotion

- 正式 promotion 仍走 `/api/scripts/evolutions/proposals`。
- 这条链路保留现有 scripting domain 的演进语义，不被 app 草稿能力绕开。
- 结论：
  - `draft-run` 是 app 内部体验能力
  - `promotion` 是正式 catalog 变更能力

### 运行时契约

为了先让 studio 可用，草稿脚本运行时先收敛到一个最小稳定契约：

- command input: `google.protobuf.StringValue`
- read model: `google.protobuf.Struct`

`Struct` 约定字段：

- `input`
- `output`
- `status`
- `last_command_id`
- `notes`

CLI 宿主通过 `AppScriptProtocol` 统一生成和读取该结构。这样做的目的不是把 scripting 内核降级成 bag，而是给 app studio 的草稿体验提供一条窄而稳定的 authoring contract。正式脚本能力后续若有明确业务语义，再上提为强类型 proto。

## 宿主改动

`AppToolHost` 在 embedded mode 下新增：

- `AddGAgentServiceCapability`
- `MapUserWorkflowCapabilityEndpoints`
- `MapScriptCapabilityEndpoints`
- `AppStudioEndpoints`

其中 `AppStudioEndpoints` 负责 app 前端专属协议：

- `/api/app/context`
- `/api/app/scripts/draft-run`

这些 endpoint 只做宿主组合，不承载 scripting 或 workflow 的核心业务编排。

在 proxy mode 下，CLI bridge 会把 `/api/scopes/...` 直接代理到 mainnet，因此 published workflow 的查询、发布与运行都不再依赖本地 app 私有 workflow endpoint。

## 当前前端行为

- 首屏以 workflow 为默认工作区。
- scripts 作为第二工作区切换。
- workflow 首屏支持：
  - bundled workflow 装载
  - published workflow 查看
  - YAML 校验
  - publish
  - draft / published run
  - 右下角 Ask AI 生成 YAML
- scripts 首屏支持：
  - 本地 draft 管理
  - Ask AI 生成 C# source
  - draft run
  - promotion proposal
  - runtime snapshot 查看

## 后续建议

- 为 scripts 草稿运行补一套更强类型的 app-level proto contract，替代当前 `Struct` 草稿协议。
- 在 workflow graph 上增加 step inspector，把选中节点的参数、branch、children 展开为单独面板。
- 若 scripts studio 继续扩展，再把 draft session 与 promotion history 物化成独立 read model，而不是继续只靠列表查询拼前端状态。
