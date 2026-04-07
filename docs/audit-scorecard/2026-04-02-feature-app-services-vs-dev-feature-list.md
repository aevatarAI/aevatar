# `feature/app-services` 相对 `dev` 的 Feature List

## 说明

- 基线：`dev`
- 对比对象：当前工作树（包含 `feature/app-services` 已提交内容，以及本地未提交改动）
- 统计快照：`1556 files changed, 229843 insertions(+), 45823 deletions(-)`
- 主要变更集中在：`apps/aevatar-console-web`、`src/workflow`、`src/platform`、`tools/Aevatar.Tools.Cli`、`src/Aevatar.CQRS.Projection.Core`、`src/Aevatar.Studio.*`

## 一、Console Web / Studio / CLI 运行时工作台大幅扩展

- 新增独立 `apps/aevatar-console-web` 前端工程，覆盖 overview、runs、scopes、services、governance、actors、gagents、studio、workflows、Mission Control 等页面。
- CLI 前端 `tools/Aevatar.Tools.Cli/Frontend` 从单页 playground 演进为运行时工作台，补齐：
  - scope overview / invoke / assets
  - GAgent 页面
  - studio / scripts studio
  - config explorer
  - NyxID 登录态与鉴权 UI
- 控制台支持 scope-first 工作流：从 scope 视角查看服务、执行 draft run、触发 endpoint 调用、回放 run 会话。
- runtime / Studio 端补齐 execution details、trace pane、timeline grouping、tabs 填充等交互细节。

## 二、应用服务与绑定控制面能力成型

- 分支主线围绕 app-services 展开，新增 scope binding、service identity、service invocation、draft-run、logs 等能力。
- runtime service management UI 已落地，按页签组织 draft runs、services、invocation、logs。
- 服务调用从“手工拼请求”演进为“先发现服务，再按 endpoint schema 调用”的工具化入口。
- 增加 app-level function execution / workflow integration，形成 app -> service -> workflow 的贯通路径。
- 引入 revision governance、typed implementation、auto-start workflow runs，服务治理语义更完整。

## 三、AI Chat / Tool Calling / NyxID 能力明显增强

- Chat 主链进一步流式化，围绕 `ChatRuntime` / `ToolCallLoop` 增强：
  - tool round limit
  - length truncation recovery
  - context compression / token budget tracking
  - 中途 tool call 执行与 follow-up round
- 新增和增强的 tool provider 包括：
  - NyxID 管理工具
  - ServiceInvoke 工具
  - Web / Scripting / Workflow 工具
  - 本地 definition / binding 相关工具
- NyxID 集成扩展到：
  - LLM provider routing
  - 用户路由偏好
  - token / model override
  - CLI login / logout / whoami
  - NyxID chat service、conversation 管理、SSE 支持
- 新增 streaming proxy GAgent、chat history persistence，使实时会话与持久化历史结合。

## 四、Workflow 能力从执行到定义管理同时扩展

- Workflow 侧除了已有运行时与 projection 改造，还新增了 definition 管理相关能力：
  - `workflow_list_defs`
  - `workflow_read_def`
  - `workflow_create_def`
  - `workflow_update_def`
- 新增本地 workflow definition command adapter 与 YAML validator，说明 workflow 定义已不再只停留在执行期，而开始具备“编辑、校验、存取”的闭环。
- `WorkflowExecutionKernel`、`ForEachModule`、`MapReduceModule`、`ParallelFanOutModule`、`BackpressureHelper` 等改动表明：
  - 并行 fan-out / map-reduce 的背压与幂等控制被加强
  - step execution 的重复执行保护开始成体系
- `workflow_execution_messages.proto`、`workflow_state.proto` 继续演进，workflow 内核语义在向更强类型和更稳定的运行态表达收敛。

## 五、Projection / Runtime / Hosting 继续向统一主链收敛

- 分支中有大量 CQRS Projection Core、workflow projection、Studio hosting/application/infrastructure 变更，说明统一 projection 主链仍在推进。
- projection lifecycle、query path、relay/query 修正、runtime lease / activation 等能力持续收敛到统一模型。
- `tools/ci` 增加或强化了多项 guard，覆盖 projection、workflow binding、query priming、solution split、asset drift 等治理点。
- 本地开发与 host 文档继续统一到 `5100` 端口，减少环境漂移。

## 六、当前工作树额外在途改动（尚未并入已提交历史）

- `TextToolCallParser`：为文本形态 DSML/XML tool call 提供后备解析路径。
- `Aevatar.AI.ToolProviders.Binding`：新增 `binding_list / binding_status / binding_bind / binding_unbind`。
- `Aevatar.AI.ToolProviders.Workflow`：在已有查询类工具之外，补齐 workflow definition CRUD。
- `Aevatar.AI.Infrastructure.Local`：新增本地 definition command adapter，便于开发期闭环。
- `Aevatar.Foundation.*.MultiAgent`：新增 `TaskBoardGAgent`、`TeamManagerGAgent` 与对应 proto/state，开始建设 multi-agent 协作基础设施。
- `ChronoStorageChatHistoryStore`：从全量 JSONL 扫描扩展到 sidecar metadata 读写，优化 chat history index 构建成本。
- CLI runtime 前端：补了 assistant 文本清洗与 reasoning 段落分隔逻辑，改善流式渲染体验。

## 七、综合判断

- 这不是单点 feature 分支，而是把 app-services、scope runtime、NyxID chat、AI tools、workflow definition、Studio/CLI 工作台合并推进的一条大分支。
- 用户可见层面，最大的增量是“控制台/Studio 工作台化”和“AI 工具调用能力平台化”。
- 工程层面，最大的增量是“workflow + service + scope + AI chat”几条链路开始通过更统一的 tool / projection / hosting 方式连接起来。
