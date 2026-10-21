# Workflow 上游输出联想

## 当前状态与范围

状态：**输入框原位联想原型已完成本轮交互验证，待用户体验；生产开发仍等体验反馈。**

独立分支为 `feat/2026-10-19_workflow-output-picker`。本轮仅完善独立原型及
设计说明，不接入产品路由、真实保存或执行链路，不改变后端、依赖和认证。
原型使用明确标注的本地演示数据，不连接生产 API。

[交互原型](../../prototypes/2026-10-19-workflow-output-picker/index.html)：
`apps/aevatar-console-web/docs/prototypes/2026-10-19-workflow-output-picker/index.html`。

本文是当前唯一交互方案。先前“插入上游输出”按钮、带搜索框的选择浮层、
红色错误横幅及其验证记录已被本轮取代；旧结果不能证明原位联想通过验证。

## 唯一交互：在输入位置引用前面节点

适用输入框旁保留简短 tips：“输入 `${steps`，引用前面节点的输出。”
单行、多行和嵌套 JSON 字符串共用以下规则：

1. 光标所在引用输入到 `${steps` 时，自动在当前输入框下方展示上游节点；
   也支持从 `${` 起始。继续输入按当前表达式过滤节点 ID、输出路径或字段名；不另开搜索框。
2. 选择节点后，仅把当前表达式补成 `${steps.<ID>.`，联想原位切换为该节点的
   输出选项。每项展示实际输出路径；完整输出和可证明存在的顶层字段明确区分。
3. 选择完整输出，完成为 `${steps.<ID>.output}`；选择字段，完成为
   `${steps.<ID>.json.<field>}`。选择后收起列表，焦点仍在原输入框，光标位于
   表达式末尾，可继续输入文字。
4. ↑/↓ 移动候选，Enter 或 Tab 选择当前候选，Esc 收起；鼠标也可选择。
   仅列表打开且有可选候选时拦截选择按键；否则保留 Enter 换行、Tab 移焦。
   中文输入法组合输入期间不拦截 Enter、方向键或 Tab，不误选候选。
5. Esc 或点击外部只收起列表，不撤回已输入文字；重新编辑当前引用可再次
   唤起联想。没有独立插入按钮、搜索弹框或第二套选择入口。

列表紧邻输入框下方，受可见区域约束，窄屏可局部滚动。输入框保持键盘焦点，
候选有明确的活动状态、可访问名称与列表关系；不要把焦点困在候选中。

## 编辑范围与撤销

每次补全只替换光标所在的当前 `${...}` 表达式范围；节点阶段和输出阶段
共用同一稳定目标及范围。能安全识别的已闭合引用替换至其对应 `}`；右侧旧
路径包含空白且范围不明确时暂停补全，先由用户修正旧引用，避免将旧尾部
误降为普通文字。未完成引用只替换
已识别的表达式片段，不吞掉周围固定文字、后缀、其他引用或换行。例如：

```text
文档：${steps.prepare_document.json.document_id}，请处理
```

在这段文字中修改节点或字段，必须保留“文档：”和“，请处理”。多行与同一
字符串的多个引用遵守相同规则。支持撤销最近一次补全，并立即重新校验；
不能把撤销恢复的无效引用当作有效内容。

切换节点、移除目标字段或切换编辑模式后，立即关闭联想并作废旧目标。
嵌套 JSON 用稳定编辑节点 ID 定位，不能只保存会随重命名、排序变化的路径。

| 位置 | 原型覆盖与正式接入目标 |
| --- | --- |
| 普通单行值，例如 Assign Value | 原位联想与手写校验 |
| 多行文字，例如 AI Instruction | 原位联想，保留换行 |
| Arguments Fields 模式的 string 值 | 原型仅覆盖固定 document_id；任意层级的同一联想与递归校验是正式接入目标 |
| JSON 属性名、number、boolean、null、容器、声明名称 | 不提供联想 |
| Raw JSON、Advanced JSON、整份 YAML | 原型不提供这些编辑器；正式接入不新增联想，解析后的字符串引用须巡检 |

本轮生成的直接引用只有 `steps.<id>.output` 与 `steps.<id>.json.<field>`。
独立原型对其他 `${...}` 保守阻止应用；正式接入须补全已有命名变量、作用域
和表达式语法的目录与检查规则，不能用原型限制破坏原有合法 DSL。

## 演示流程与可信字段

默认流程为 `prepare_document → configure_page_title → read_docx_content →
summarize_content → send_summary`。`read_docx_content` 是工具节点，
`summarize_content` 是 AI 节点；所有值均明确标为本地示例。

`prepare_document` 是静态 Assign，当前 literal Value 为：

```json
{
  "document_id": "doc_demo_123",
  "page_title": "团队知识库"
}
```

它的候选来自当前 Value，不维护另一份字段清单：完整输出为
`${steps.prepare_document.output}`，两个顶层字段分别为
`${steps.prepare_document.json.document_id}` 和
`${steps.prepare_document.json.page_title}`。节点输出与 Assign 的
`Target variable` 是不同地址，不混在一起。

只有 Value 无 `${...}`、不以表示转发输入的 `$` 开头且可解析为 JSON object
时，才从顶层 key 推导字段。变成动态表达式、转发输入、非 JSON 或非 object
后，立即撤下字段候选，仅保留完整输出。字段名保守限制为
`[A-Za-z_][A-Za-z0-9_]*`；不为带点、空格或括号的 key 伪造转义语法。
object/array 顶层字段可整体引用，不展开内部字段或数组下标。

AI/tool 没有可信输出 schema 时，仅提供完整 `output`，中性说明“输出字段
未定义”。prompt、入参、模型名称或一次运行样例均不能证明字段存在。

## 目录与存在性检查同源

只考虑当前 Workflow 最外层 `steps`，不展开 foreach/while、并行子步骤或
子 Workflow 内部。上游依据真实连线，不能按画布位置或 YAML 数组顺序猜测。
正式接入复用 `materializeImplicitSequentialTransitions` 和
`findStudioControlFlowCycle`，按稳定拓扑顺序列祖先，同层保持文档顺序；排除
当前、下游、孤立及从入口不可达的节点。有环时停止提供候选。

候选目录、过滤与引用检查必须使用同一份当前文档、上游计算和字段推导。
来源或字段删除、改名、连线变化后，原型收起旧列表并重建表单；再次输入时
从当前文档生成候选，当前节点的应用检查立即重算。正式接入的全工作流巡检
也必须重算；
确认选择前再检查候选仍存在，不沿用缓存结果。每处引用须同时满足：

1. 属于支持的引用语法，来源 ID 唯一且真实存在。
2. 来源是引用所在节点的有效上游。
3. 完整输出使用 `output`；`json.<field>` 必须能从当前支持的确定性定义推导。

输入、补全、结构变化、Apply、撤销、还原节点及恢复示例均走同一检查。
原型递归检查当前步骤参数中的对象、数组与 string 值；属性名不作为表达式值。
当前节点存在未完成或失效引用时不能 Apply，编辑文字仍保留。正式接入的
Save 必须遍历所有步骤，包含未打开节点，不能只检查当前表单。

连线可达不保证分支执行、成功或有值。相关候选保留简短说明：“分支节点可能
没有执行，引用不保证运行时有值。”定义存在性与运行结果为空是不同问题。

## 中性状态与修复

不使用红色边框、红色错误横幅或弹窗打断输入。未完成、失效与应用阻断统一
在底部中性状态区表达。原型一次编辑一个字段；正式接入多个字段时，需能
定位到对应节点、字段和表达式。把光标放回该引用即可继续原位补全，或直接编辑、
删除这处引用；修复与移除只影响该表达式，随后立即重新检查。

| 情况 | 中性反馈与行为 |
| --- | --- |
| 没有上游 | “当前节点前面还没有可引用的节点。” |
| 没有匹配项 | “没有找到匹配的上游节点或输出。”；不创建假字段 |
| 引用未完成 | “引用尚未完成，完成后可应用。”；保留草稿，暂不能 Apply |
| 来源删除、非上游、字段删除或未定义 | 底部提示先选择可用引用，暂不能 Apply；输入时只给可用候选 |
| 来源 ID 重复或连线有环 | 说明需调整来源名称或连线，停止列候选 |
| 联想目标已消失 | 收起列表，不向其他字段写入 |
| 检查通过 | 允许“应用到本地示例”；不宣称已保存或已执行 |

正式接入时，结构变化须同步更新其他节点的中性受影响标记和底部定位入口。
Save workflow 检查整个工作流，不能只复用当前节点 Apply 的结果。Raw JSON、
Advanced JSON、YAML 先经现有解析链；解析失败时不宣称引用有效。前端检查
不代表后端已有同等校验，也不增加真实保存能力。

## 原型需体验的变化

- 真正从静态 Assign Value 移除字段，立即更新候选并重新检查当前节点引用。
- 真正删除来源并修复演示连线；或改有效 `next` 使来源不再是当前节点上游。
  不能只删显示线，因为隐式顺序可能补回。
- 合法且唯一地改名来源，只精确改连线及已识别引用的来源 ID，保留普通文字、
  相似 ID、字段后缀与换行；同步当前编辑值和 applied baseline。
- 手写缺失节点、字段、自引用、下游引用、未闭合引用和不支持路径，均与
  选择结果同样检查。多处引用分别修复，周围文字不变。
- “重新体验”恢复整份示例；“还原此节点”恢复 applied baseline 并重检。
  结构变化清除旧文本撤销记录；改名不让还原带回旧 ID，已删除字段不因还原
  而有效。不另做结构撤销。
- 原型覆盖单行 document_id、多行 AI 指令和 Assign Value，以及嵌套参数
  path_params.document_id。任意层级的可增删 JSON 字符串编辑仍是正式接入目标。
- 两个起始前缀、节点到输出的两步补全、键盘、中文输入法、无匹配、无上游、
  分支、有环和窄屏均需体验；真实中文输入法候选确认尚待人工体验。

这些变化必须修改演示文档本身，不能只切错误模式或隐藏固定选项。

## 现有契约与后续生产接入

当前 Studio 的 `StudioWorkflowDocument/StudioWorkflowStepDocument` 与后端
`StepModel` 没有输出 schema；primitive capabilities 主要描述参数。
`NyxIdOperationSchema` 的请求 schema、response policy 不能推导返回体字段。
虽然 runtime 有 `response_projection.fields`，当前 Studio 的兼容字段表、
StepModel、YAML 链路尚未贯通，不能把它当作现成字段来源，也不新增假 YAML。

`WorkflowExpressionEvaluator.ResolveVariable` 按完整 key 查找，缺 key 返回
空串；`WorkflowExecutionValueStore.ResolveJsonPropertyAliases` 只建立成功
JSON object 输出的顶层别名。深层路径、数组下标不是已有通用路径语法，
`json()` 也不是 JSONPath。静态 Assign 是本轮可证明字段的依据，检查不承诺
运行时非空。以上是既有设计依据，本轮不声称重新验证后端。

用户体验反馈后再决定生产开发，接入计划如下（前端相对路径）：

| 位置 | 正式接入职责 |
| --- | --- |
| `src/pages/workflow-activity-vnext/workflows/WorkflowEditorPage.tsx` | 使用当前真实文档与结构变化，不建第二份权威状态 |
| `src/pages/workflow-activity-vnext/hooks/useWorkflowEditor.ts` | Apply、结构操作、全工作流 Save 复用检查结果 |
| `src/pages/workflow-activity-vnext/workflows/WorkflowNodeInspector.tsx` | 输入框联想、tips、底部定位，沿用 `updateFieldValue` |
| `src/pages/workflow-activity-vnext/workflows/StructuredJsonArgumentsEditor.tsx` | 复用稳定编辑身份、`commitRoot/updateNode`，支持嵌套 string |
| `src/shared/studio/document.ts` | 复用隐式顺序和环检测，精确处理来源改名 |
| 共享辅助逻辑与中英文消息文件 | 统一目录、检查、范围替换及组合输入行为；复用 `t` 与 Umi locale |

接入前补齐现有合法表达式兼容规则、确认前后端校验边界。独立原型不是 API、
持久化或执行事实来源。生产验证遵守 worktree 环境同步与 OAuth origin 规则，
不读取或展示 dotenv 值。

## 本地启动与体验

本原型是独立静态 HTML，不依赖 pnpm、后端、登录或真实工作流数据。
在此分支的仓库根目录运行；关联 worktree 先按本机规则完成环境同步：

```bash
python3 -m http.server 58147 --bind 127.0.0.1 \
  --directory apps/aevatar-console-web/docs/prototypes/2026-10-19-workflow-output-picker
```

打开 <http://127.0.0.1:58147/>。若该端口已经由此 worktree 的原型服务使用，
直接复用，不重复启动。若由其他服务占用，替换为一个空闲端口，并用同一个
端口打开页面；这个静态原型不涉及 OAuth 配置。

建议按下面顺序体验：

1. 在右侧 `document_id` 输入 `${steps`，确认仅显示上游节点；选择
   `prepare_document`，再选 `document_id`，完整引用应自动补齐并可应用。
2. 点击 AI 节点，把引用放到多行指令中间；用方向键及 Enter/Tab 选择，用
   Esc 收起，用“撤销补全”恢复。前后文字与换行应保留。
3. 重新体验后，先引用字段，再用上方按钮移除字段、删除来源或改变连线；
   已失效的输出不能再选或应用。来源改名应同步更新已有完整引用。
4. 切换普通文本、分支汇合和循环连线场景，确认未知工具只有完整输出，
   分支有提示，有环时没有可选候选。中文输入法确认请做人工体验。

“应用到示例”仅修改本页内存，刷新即重置。它不保存或执行真实工作流，
正式 workflow 编辑器也不会因这个 PR 获得联想功能。

## 本轮验证状态

本轮在独立本地原型上，通过实际浏览器输入、选择、键盘和示例结构操作检查了：

- 输入前缀自动列出上游节点；选节点后切换输出字段；选字段后生成完整表达式，
  可以应用到本地示例。未完成引用不显示红色错误。
- 单行/多行的鼠标、Enter、Tab、方向键、Esc；全选后 Tab 正常移焦、多行
  Enter 正常换行。补全后焦点和光标留在输入框。
- 多行文字中间修复引用并撤销，保留前后文字和换行；未闭合前缀不吞右侧原文；
  支持路径中已闭合的错误尾部整体替换；旧路径含空白且边界不明确时不提供
  补全。右侧 literal JSON 不会被误当成表达式尾部覆盖。
- 移除字段后候选撤下；来源改名同步更新完整引用；删除来源后移除候选并阻止
  失效引用应用。未知工具只列完整输出；无匹配、无上游、分支、有环状态可见。
- 转义文字 `$${steps` 不触发联想。真实中文输入法组合确认没有自动化验证，
  目前仅有组合事件期间不拦截按键的实现和代码检查，仍需人工体验。
- 桌面、平板和手机布局检查，包括窄屏长字段换行，修正了候选切换导致整页
  意外滚动、手机提示语被拆列的问题。截图保存在仓库外的本地体验产物目录。

静态验证：提取 HTML 内脚本执行 `node --check`；`bash tools/docs/lint.sh`；
`git diff --check`；对两个新增文件额外检查 UTF-8、末尾换行及行尾空白。
上述检查均通过。另复用主 checkout 已安装的 Biome，对这两个路径运行
`biome lint --no-errors-on-unmatched --reporter=summary`：0 errors、5 条
`noImportantStyles` warnings、31 条 `useTemplate` infos。浏览器未捕获
warn/error；已检查 1440×900、1024×768、
834×1112、390×844、360×844，页面宽度均未超过视口。

本轮没有修改产品 TypeScript、构建或依赖，不新增产品单元测试；没有运行
完整前端测试、集成测试、E2E 或 smoke 套件，也没有运行产品 TypeScript
检查及生产构建。这些原型体验检查不构成真实 API、保存或执行链路验证。
正式开发时仍须按前端测试策略选择受影响的单元测试与相关静态检查。

## 设计基线声明

沿用主画板的节点工作台布局及现有编辑、身份、保存和语言行为，不改原有
17 个主 frame。Schedule 不在本轮范围内。

```text
Design baseline:
  apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/
Primary design:
  aevatar-workflow-activity-vnext.excalidraw
Design SHA-256:
  30e74d7b410ae72c4c91432355436679033679c54c10b1702908435b001577de
Primary frame inventory:
  17 frames; this feature does not change the baseline artwork.
Contract specification:
  apps/aevatar-console-web/docs/superpowers/specs/
  2026-08-04-workflow-activity-vnext-design.md
User paths:
  apps/aevatar-console-web/docs/superpowers/specs/
  2026-08-04-workflow-activity-vnext-user-paths.md
Authentication and localization:
  Existing Aevatar login, callback, session, returnTo, and Umi locale logic;
  presentation may change, behavior may not.
Production data source:
  Real APIs and API-acknowledged user actions only; no mock fallback.
  The isolated prototype contains labeled demonstration data only.
Baseline integrity:
  python3 apps/aevatar-console-web/docs/design-baselines/
  workflow-activity-vnext/verify-baseline.py
```
