# Workflow + Activity + Settings vNext

## Repository Implementation Baseline

Status: **Normative for the Workflow Activity vNext frontend**.

Any implementation or review of routes below
`/scopes/:scopeId/workflow-activity-vnext` must read this directory together
with the
[`design specification`](../../superpowers/specs/2026-08-04-workflow-activity-vnext-design.md)
and
[`user paths`](../../superpowers/specs/2026-08-04-workflow-activity-vnext-user-paths.md),
plus the
[`published-workflow schedule supplement`](../../superpowers/specs/2026-08-11-workflow-schedule-design.md)
and the
[`Schedule management and History specification`](../../superpowers/specs/2026-08-21-workflow-schedule-history-design.md)
when Schedule is in scope.

Use the sources in this order:

1. `aevatar-workflow-activity-vnext.excalidraw` is the primary visual,
   information-architecture, and interaction reference for Workflow,
   Activity, and Settings.
2. The design specification is the normative product, route, identity, API,
   state, and backend-compatibility contract.
3. `aevatar-workflow-schedule-design.excalidraw`, its generator, and the
   published-workflow schedule supplement are normative whenever a Schedule
   entry, panel, or resource is changed. They define Schedule as an exact child
   of `scopeId + workflowId`, backed by the Workflow-scoped Schedule API, not a
   Team member automation, graph node, draft property, or Run property.
   Schedule History owns bounded attempts while Activity remains the owner of
   actual Runs.
4. The user-path specification is the normative end-to-end journey, decision,
   recovery, and completion-evidence contract.
5. The PNG files are viewport references for individual states. Schedule review
   uses seven standalone 1440x900 scene PNGs; there is no combined overview.
6. `prototype.html` is an interaction demonstration only;
   `prototype-schedule.html` opens its published Workflow Schedule state
   directly.
7. `aevatar-workflow-activity-vnext.gen.py` and
   `aevatar-workflow-schedule-design.gen.py` are source generators for these
   reference artifacts; they are not application runtime code.

If the Excalidraw or prototype conflicts with a real API contract, follow the
design specification's backend-honest behavior. Do not change the backend and
do not fabricate data to close the gap.

## Production Data Truth Rule

The production frontend must never make sample, fixture, generated, cached, or
hard-coded data look as though it came from an API.

- API-owned Workflows, Runs, Run details, settings, identities, receipts,
  statuses, timestamps, revisions, usage, lineage, and availability must come
  from real API responses or real user actions acknowledged by those APIs.
- While a request is pending or unavailable, render the specified loading,
  empty, delayed, unavailable, or error state. Never substitute demonstration
  rows or a successful-looking default response.
- An empty state is allowed only after a successful authoritative query returns
  no records. A request failure is an error state, not an empty state.
- Browser storage must not act as an Activity, Workflow, account, or settings
  backend. Existing session helpers may retain non-authoritative request
  recovery facts only when the design specification explicitly permits it.
- Mock and fixture data are allowed only inside clearly named automated test
  fixtures or isolated test harnesses. They must not be imported by production
  route, component, hook, query, or API-adapter code.
- Versioned, bundled Workflow templates are frontend product content, not API
  data. If used, label and model them explicitly as bundled templates; never
  present them as a server-returned template catalogue.

`prototype.html` intentionally contains hard-coded demonstration records,
`localStorage`, and timers so the standalone prototype can be exercised
without a server. Those mechanisms have no API or persistence authority and
must not be copied into the production implementation.

## Existing Authentication And Localization Contract

Workflow Activity vNext reuses Aevatar Console authentication and
internationalization behavior. It does not create a vNext-specific auth or
locale system.

- Protected-route handling continues through the current app runtime,
  `ProtectedRouteRedirectGate`, `/login`, `/auth/callback`,
  `NyxIDAuthClient`, sanitized `returnTo`, stored/restorable sessions, and
  existing sign-in, sign-out, callback recovery, and service-access review.
- The user must return to the original scoped vNext URL after successful login
  through the existing `redirect`/`returnTo` behavior. No second callback,
  token cache, session store, or auth provider is allowed.
- Language behavior continues through the Umi locale plugin,
  `ConsoleLanguageSwitch`, `setLocale`, `t`/`useIntl`, and the existing
  `en-US` and `zh-CN` catalogues. New copy must be added to both catalogues.
- If the vNext shell hides the current global chrome, it must reuse the existing
  language and account actions inside its own presentation. It must not clone
  their state or behavior.
- Login, callback, language, and account controls may receive a visual-only
  treatment matching the Operational Automation Ledger direction. Route
  names, redirects, auth protocol, return behavior, session lifecycle, locale
  keys, language persistence, error recovery, and accessibility behavior stay
  unchanged.

The existing Login page currently uses a decorative gradient and large rounded
card. A later visual implementation may replace those presentation choices
with the vNext dark rail/white work surface, neutral borders, 4-6 px radii,
compact typography, and restrained status colors. That visual change must not
alter what starts login, how callback completion works, or where the user
returns.

## Required Development And Review Declaration

Every implementation task and pull request for this vNext route should include
this declaration, updated only when the named baseline changes:

```text
Design baseline:
  apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/
Primary design:
  aevatar-workflow-activity-vnext.excalidraw
Design SHA-256:
  30e74d7b410ae72c4c91432355436679033679c54c10b1702908435b001577de
Contract specification:
  apps/aevatar-console-web/docs/superpowers/specs/
  2026-08-04-workflow-activity-vnext-design.md
Schedule design, when Schedule is in scope:
  aevatar-workflow-schedule-design.excalidraw
Schedule design SHA-256:
  7c27a027eec6a3ec9d1b118fa3b4ab80d1938fd85f3bc04451d1189553fb67d8
Schedule supplement, when Schedule is in scope:
  apps/aevatar-console-web/docs/superpowers/specs/
  2026-08-11-workflow-schedule-design.md
Schedule management and History supplement, when Schedule is in scope:
  apps/aevatar-console-web/docs/superpowers/specs/
  2026-08-21-workflow-schedule-history-design.md
User paths:
  apps/aevatar-console-web/docs/superpowers/specs/
  2026-08-04-workflow-activity-vnext-user-paths.md
Authentication and localization:
  Existing Aevatar login, callback, session, returnTo, and Umi locale logic;
  presentation may change, behavior may not.
Production data source:
  Real APIs and API-acknowledged user actions only; no mock fallback.
Baseline integrity:
  python3 apps/aevatar-console-web/docs/design-baselines/
  workflow-activity-vnext/verify-baseline.py
```

This declaration makes design drift and mock-data substitution explicit review
failures rather than undocumented implementation choices.

## Imported Source

这个目录最初根据 2026-08-03 会议结论重新整理为独立版本。用户提供的原始画板、生成器、HTML 和 PNG 是来源资产；后续经评审批准的设计演进会同时修改生成器、画板、校验器和本文档。仓库内副本是后续实现与评审的唯一可移植基准；本机来源路径不构成依赖。

## 文件

- `aevatar-workflow-activity-vnext.excalidraw`：合并后的 Excalidraw，包含 Workflows、Activity 与 Settings，共 17 个 frame。
- `aevatar-workflow-schedule-design.excalidraw`：根据用户提供的 Schedule wireframe 重绘的独立 Excalidraw，只包含 Workflows 管理、Workflow 编辑器配置、创建前复核、创建等待、Overview、History 与修改，共 7 个 frame；不包含 Activity 页面。
- `aevatar-workflow-schedule-design.gen.py`：上述 Schedule 画板的确定性生成器。
- `render-schedule-png.py`：从 Schedule Excalidraw 确定性地将每个 frame 渲染为独立 1440×900 PNG 的 Pillow 脚本。
- `aevatar-workflow-activity-vnext.gen.py`：画板生成器，内含画框边界、ID 唯一性和废弃术语检查。
- `verify-baseline.py`：仓库内校验器，验证主画板和 Schedule 画板 SHA-256、两个生成器的确定性输出、17 个主画板 frame、7 个 Schedule frame、7 张独立 PNG，并拒绝旧的拼板总览图。
- `prototype.html`：可直接在浏览器打开的交互原型，不需要安装依赖或启动服务。
- `prototype-schedule.html`：打开后直接进入已发布 Workflow 的新建 Schedule 配置，不需要先从 Draft 列表寻找入口。
- `schedule-workflows-list-modal.png`：从 Workflows 列表打开的 Schedule 管理 modal；列表行只负责选择 Schedule，不重复堆放生命周期操作。
- `schedule-workflow-editor-panel.png`：Workflow 编辑器右侧的新建 Schedule panel。
- `schedule-review.png`：确认 Workflow、名称、周期、时区、可选运行输入、创建后启用状态与五次 next-fire 预览的创建复核页。
- `schedule-creation-pending.png`：创建命令 `202 Accepted` 后回到列表并显示 Toast 的页面。
- `schedule-detail.png`：Workflow 内默认打开的 Schedule Overview，展示人类可读规则、当前状态、关键统计和分层动作；展开 Advanced details 后仍先解释运行周期，再显示精确技术格式。
- `schedule-history.png`：Workflow 内的 Schedule History，展示有界 recent attempts、诚实的 Schedule outcome、失败技术详情折叠，以及有权威 Run ID 时的整行导航。
- `schedule-edit.png`：Workflow 内修改重复规则、时间、时区和可选运行输入的页面。
- `prototype-workflows.png`：Workflows 桌面视图截图。
- `prototype-activity.png`：Activity 桌面视图截图。
- `prototype-mobile.png`：Activity 移动视图截图。
- `prototype-create-describe.png`：通过描述生成的 Workflow 草稿。
- `prototype-create-blank.png`：空白 Workflow 草稿。
- `prototype-create-import.png`：从 YAML 导入的 Workflow 草稿。
- `prototype-create-template.png`：从模板创建的 Workflow 草稿。
- `prototype-editor-canvas.png`：真实 Aevatar Workflow Studio 结构的默认节点画布。
- `prototype-editor-node.png`：点击节点后打开的 Node configuration 侧面板。
- `prototype-node-library.png`：从左侧打开并可搜索的 Node library。
- `prototype-editor-yaml.png`：Workflow YAML 侧面板。
- `prototype-create-mobile.png`：移动端 Workflow 画布，保留 Run、Add node、Edit YAML、Save 和 Publish。
- `prototype-editor-mobile-node.png`：移动端点击节点后的底部配置面板。
- `prototype-settings-llm.png`：重构后的 AI defaults 桌面视图。
- `prototype-settings-save.png`：展示来源原型中产生修改后才出现的 sticky save bar 与保存确认状态；生产实现按专项设计改为 shell-fixed dock。
- `prototype-settings-account.png`：Account 身份、会话与 service access 桌面视图。
- `prototype-settings-advanced.png`：独立的只读 Runtime 与 request values 视图。
- `prototype-settings-tablet.png`：Settings 的 768px 布局。
- `prototype-settings-mobile.png`：Settings 的 390px 移动布局。

## Workflow Studio 参照

本轮没有继续设计新的 Workflow 编辑器，而是以真实 Aevatar 项目现有的 Workflow Studio 为来源：

- `apps/aevatar-console-web/src/pages/team-member-workflow-studio/index.tsx`
- `components/WorkflowStudioCanvas.tsx`
- `components/WorkflowStudioNodeLibrary.tsx`
- `components/WorkflowStudioNodeDetailPanel.tsx`
- `components/WorkflowStudioYamlPanel.tsx`
- `src/shared/graphs/GraphCanvas.tsx`

原型因此统一采用“节点画布 + 连线 + 按需侧面板”的编辑模式。Describe、Start blank、Import YAML 和 Template 只负责产生不同的初始 Workflow 文档，不再各自发明编辑行为。

## 原型意图与强制偏差

下面的原型意图只用于理解视觉和交互方向。它不覆盖本文前面的
Production Data Truth Rule，也不覆盖设计规范中的
`Excalidraw-To-Backend Deviations`。后续实现必须应用这些强制偏差：

- 用户从 Workflows 直接创建 Workflow 草稿，没有其他资源的创建前置步骤。
- `Run` 是唯一的手动执行入口。`Schedule` 是当前 Workflow 的 recurring resource，由 `scopeId + workflowId` 精确拥有并通过 Workflow-scoped API 管理；它不是 Team member automation、Run 对话框、草稿属性或画布节点。
- Run 请求开始后先显示真实的 Accepted/Running 状态；只有 Observatory
  返回权威记录后，才能显示为 Activity 记录。
- `Current draft · revision N`、`Published · vN`、来源、耗时、用量和
  lineage 都是画板中的视觉意图；对应字段没有出现在真实 API 响应时必须省略。
- Retry 和 Run again 不修改原 Run，并请求创建新 Run；只有真实 receipt
  或后续查询提供的关联事实才能显示新旧 Run 的关系。
- Activity 是 vNext 内唯一的 Run 历史入口；按 Workflow 筛选只使用权威
  `definitionActorId`，不能按名称或其他 ID 猜测。
- Workflows、Activity 和 Settings 是隔离 vNext shell 的本地导航。第一期
  不修改现有全局菜单或旧路由。

## Excalidraw 阅读顺序

导入 `aevatar-workflow-activity-vnext.excalidraw` 后，建议先缩放到全部内容，再按 frame 名称从 01 看到 17：

1. `01 Workflows - catalogue`：日常入口、搜索、筛选、Run 和 Open。
2. `02 New workflow - direct creation`：四种入口都直接创建 Workflow 草稿。
3. `03 Describe - generated Workflow draft`：描述生成匹配的节点与连线；点击节点后从右侧配置。
4. `04 Start blank - empty Workflow draft`：真实 Studio 的空画布；添加第一个节点之前不能 Publish 或 Run。
5. `05 Import YAML - imported Workflow draft`：YAML 中的名称和节点类型进入同一套 Studio 画布。
6. `06 Template - populated Workflow draft`：模板创建独立草稿，并以连接节点显示。
7. `07 Run - unified execution dialog`：确认修订、输入、连接和外部影响；底部文案表达保留本次 Run 的产品意图，生产实现仍需等待真实 Activity 观察结果。
8. `08 Running draft - Studio canvas and Run console`：仍在同一个节点画布中显示运行状态，底部打开 Run console；画板中的 Activity 记录只表达目标状态，生产实现不得在权威查询返回前声称记录已存在。
9. `09 Activity - filtered by Workflow`：从 Workflows 按当前 Workflow 查看权威 Run 历史；它不属于 Schedule 配置流程。
10. `10 Activity - all retained Runs`：全局、最新优先的所有 Run 记录。
11. `11 Run detail - immutable record`：展示 Run 详情的信息层级；修订、来源、耗时、用量、步骤时间线与关联记录仅在真实详情响应提供对应字段时显示。
12. `12 Failed Run - recovery creates a new record`：失败解释和恢复；Retry 预览明确展示新旧 Run 的关系。
13. `13 Workflows and Activity - states`：空、加载、错误和移动端信息优先级。
14. `14 Settings - AI defaults`：正常态只保留 Preferred service 与 Default model 两个决策。
15. `15 Settings - save and recovery states`：dirty save bar、accepted observation、fallback、catalog unavailable 和保存失败恢复。
16. `16 Settings - Account`：Profile、claims、Authentication、Sign out 与 Manage service access。
17. `17 Settings - Advanced and responsive`：单份只读 effective request values 与完整移动端操作布局。

Schedule 相关变更请另外导入 `aevatar-workflow-schedule-design.excalidraw`，按下面顺序检查：

1. `01 · Workflows — schedule management modal`：在 Workflow 列表点击已发布行的 `Schedules`，打开该 Workflow 的管理弹窗；点击现有 Schedule 会在同一弹窗打开 Overview，`New schedule` 进入创建流程。
2. `02 · Workflow — schedule setup panel`：在画布旁用 `Repeat`、时间和时区构造重复规则；默认只显示人类可读摘要，`write it as cron instead` 才打开 raw cron，Review 时显示 next-fire 预览。
3. `03 · Schedule — review before creation`：确认 Workflow、Schedule name、周期、时区、可选运行输入、enabled 与五次 next-fire 预览，然后直接创建。
4. `04 · Schedule — creation pending`：创建命令返回 `202 Accepted` 后立即回到列表并显示 Toast；后台继续刷新当前 Workflow 的 Schedule list/detail，不提前声称 Active 或 next fire 已存在。
5. `05 · Workflow — schedule overview`：默认页签，在 Workflow canvas 旁查看人类可读周期、enabled、next/last attempt 与计数；Run now 和 Edit 直接显示，Pause/Enable 与 Delete 收进 More。
6. `06 · Workflow — schedule history`：紧凑展示 `recentFires` 对应的 recent attempts；原始错误只在 Technical details 中出现，相关真实 Runs 通过 Workflow + Schedule 筛选交给 Activity。
7. `07 · Workflow — change schedule`：在 Workflow panel 修改周期和运行输入，并在 `PUT` 中保留已读取的 enabled 状态；Cancel 返回 Overview。
上述 7 个 frame 分别渲染为独立 1440×900 PNG；不再生成九宫格或总览拼图。

重点不是逐个查看控件，而是沿着这条主路径检查语义是否连贯：

```text
Workflows
  -> New workflow
  -> direct draft creation
  -> edit connected nodes in Workflow Studio
  -> Run
  -> Accepted / Running from a real stream or receipt
  -> Activity record observed through the real API
  -> Run detail
  -> Retry / Run again requests a new Run without mutating the source
```

## 原型操作路径

以下步骤只用于操作独立原型。步骤中的 `localStorage`、计时器、示例用户和
示例记录不是 API 行为，也不是生产实现要求。

1. 打开 `prototype.html`，默认进入 Workflows；点击已发布 Workflow 行的 `Schedules` 会打开该 Workflow 的 Schedule 管理弹窗。列表内的 `New schedule` 进入配置流程，已有 Schedule 可进入编辑和生命周期操作。只评审编辑器 Schedule panel 时可直接打开 `prototype-schedule.html`。
2. 点击 `New workflow`，分别检查四种方式：描述会生成匹配节点；空白会进入画布空状态；导入会先校验 YAML；模板会先要求选择模板。
3. 在画布点击节点，右侧打开 Node configuration；`Add node` 从左侧打开 Node library；`Edit YAML` 从右侧打开 YAML 面板。
4. 打开一个已发布 Workflow，点击编辑器里的 `Schedules` 检查右侧 Workflow schedules 面板，再点击 `New schedule` 检查创建流程仍在右侧 panel；配置态必须以 `Repeat + time + timezone` 为主，只有点击 `write it as cron instead` 才显示 raw cron editor。未发布 Workflow 保持禁用并显示 publish 原因。Workflows 管理 modal 与编辑器 panel 共享 `list -> Overview <-> History -> Edit` 管理模型和 `list -> configure -> previewing -> review -> create -> toast -> list` 创建模型。Overview 不平铺最近 attempts；History 不冒充 Run 历史；点击 `View related runs in Activity` 后，Activity 明确展示 Workflow + Schedule 筛选。有后端 Run 身份的 attempt 可以直接打开对应 Activity Run；成功启动但缺少 Run 身份的旧 attempt 进入同一筛选后的 Activity，不猜测 Run 身份。
5. 在列表或编辑器点击 `Run`，确认修订、输入、连接和外部影响。
6. 勾选外部影响确认后点击 `Run`。记录会立即写入浏览器 `localStorage`，编辑器显示运行状态。
7. 打开 Activity，点击任意一行查看详情。
8. 对成功记录点击 `Run again`，或对失败记录点击 `Retry`，然后在确认框里创建新的关联记录。
9. 点击 Settings，在 `AI defaults` 中切换 exact service 和 model。修改后页面底部才会出现 Discard / Save changes；再分别检查 `Account` 和只读的 `Advanced`。

原型数据会保留在当前浏览器中；重新打开页面后，新建的 Run 历史仍然存在。

四种创建方式共享同一个 Workflow Studio。画布节点、连线、节点库、节点配置、YAML 和 Run 入口保持一致；变化的只是各自创建出来的 Workflow 文档。

## Settings 参照

Settings 的功能来源仍然是当前 Aevatar Console，但信息架构已经重新设计：

- `apps/aevatar-console-web/src/pages/settings/index.tsx`
- `apps/aevatar-console-web/src/pages/settings/accountContent.tsx`
- `apps/aevatar-console-web/src/pages/settings/shared.tsx`
- `apps/aevatar-console-web/src/pages/settings/userLlmSelection.ts`
- `apps/aevatar-console-web/src/pages/settings/userLlmSaveObservation.ts`

重构后分成三个任务分区：

- `AI defaults`：正常态只保留 Preferred service 与 Default model。适用范围已由页面说明表达，不再重复展示 provider inventory、route note 或保存成功状态。
- `Account`：当前浏览器身份、User ID、roles、groups、会话到期时间、NyxID provider、scope、Sign in / Sign out 和 Manage service access；字段本身足够明确，不再重复配小节说明。
- `Advanced`：只保留一份 effective request values。Runtime mode 与 base URL 不再同时出现在只读输入和 raw values 两处。

独立原型会把 AI 默认值保存在浏览器 `localStorage`，仅用于演示重开页面后的交互连续性。生产实现必须通过真实 Settings API 读取、保存并观察 AI 默认值，不能读取这份原型存储。选择 exact connected service 时，模型列表会跟着服务切换。默认状态保持安静；只有产生修改后，来源原型中的 sticky save bar 才出现；生产实现必须按 `2026-08-06-settings-save-action-dock-design.md` 使用 shell-fixed dock。保存动作先进入 accepted / confirming saved values，再根据权威查询更新已保存 service。fallback、catalog unavailable 与 provider unavailable 只在异常状态出现。Account 的 Sign in、Sign out 与 service access 继续复用现有真实逻辑。

## 基线完整性校验

从仓库根目录运行：

```bash
python3 apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/verify-baseline.py
```

校验器会在临时目录运行导入的生成器，不修改仓库文件，并验证生成结果与
主画板逐字节一致、SHA-256 与声明一致、17 个主画板 frame 的名称和顺序
完整，以及独立 Schedule 画板的 SHA-256、frame 与关键语义文案完整。任何失败都表示设计基线或声明已经漂移，必须在实现或评审前处理。
