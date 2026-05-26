# CLAUDE.md

<!--
Refactor (iter33/cluster-claude-md-slim):
Old pattern: CLAUDE.md mixed top-level architecture rules with duplicated operational runbooks and skill-owned details.
New principle: CLAUDE.md keeps the cross-process architecture and engineering boundary; operational procedures live in their owning skills.
-->

## 顶级架构约束（最高优先级）
- 严格分层：`Domain / Application / Infrastructure / Host`；`API` 仅做宿主与组合，不承载业务编排。
- 统一投影链路：CQRS 与 AGUI 走同一套 Projection Pipeline，统一入口、一对多分发，禁止双轨实现。
- 投影编排 Actor 化：Projection 运行态（会话、订阅、关联）必须由 Actor 或分布式状态承载；禁止中间层进程内注册表/字典持有事实状态。
- 读写分离：`Command -> Event`，`Query -> ReadModel`；异步完成通过事件通知，不在会话内拼装流程。
- 依赖反转：上层依赖抽象，禁止跨层反向依赖和对具体实现的直接耦合。
- 命名语义优先：`项目名 = 命名空间 = 目录语义`；缩写全大写（`LLM/CQRS/AGUI`）；集合用复数。
- 核心语义强类型：影响业务语义、控制流、稳定读取且仓库内可控的数据，必须建模为 `proto field / typed option / typed sub-message`，禁止塞入通用 bag。
- API 字段单一语义：一个字段只表达一个含义，禁止双重语义（如"名称查找 + inline 内容"）。
- Actor 即业务实体：一个 actor = 一个业务实体（数据与方法同住）；禁止按技术功能（读/写/投影）拆分同一业务实体为多个 actor。
- 删除优先：空转发、重复抽象、无业务价值代码直接删除，不保留兼容空壳。
- 变更必须可验证：架构调整需同步文档，且 `build/test` 通过。
- 外部仓库无改动权：本仓库需求禁止依赖 NyxID / chrono-storage / chrono-ornn 等外部仓库新增或修改；现有 surface 不足时，在本仓库内绕开或不做。只有发现外部仓库行为违反其已发布契约时，才可提 issue。

## 架构哲学
- 单一主干，插件扩展：只保留一条权威业务主链路；新能力以插件/模块挂载，禁止平行"第二系统"。
- 内核最小化：核心层只承载稳定不变量与通用机制；波动能力下沉到扩展层。
- 扩展对称性：内建与扩展能力遵循同一抽象模型与生命周期协议。
- 抽象优先：依赖行为契约与语义接口，而非具体类型与实现细节；组合面向能力，非面向实现。
- 边界清晰：协议适配、业务编排、状态管理分属不同层；禁止跨层偷渡语义。
- 事实源唯一：跨请求/跨节点一致性事实必须有唯一权威来源（Actor 持久态或分布式状态），不依赖进程内偶然状态。
- 强类型内核，窄扩展点：稳定语义默认强类型；只有插件/第三方/跨边界透传需求明确时才保留 bag。
- 渐进演进：开发期可用本地/内存实现，但生产语义必须能无缝迁移到分布式与持久化。
- 正确架构优先：正确架构在增长时自然解决下游问题；如果架构无法在增长时解决问题，则架构本身不正确。
- 治理前置：架构规则必须可自动化验证（门禁、测试、文档一致性）。

## 字段命名与 Metadata 决策树（强制）
1. 核心语义？影响业务语义/控制流/稳定查询 → 强类型 `proto field / typed sub-message / typed option`，不因"未来可能扩展"先放 bag。
2. 开放扩展边界？生产方/消费方不完全同源、允许第三方追加、缺失不破坏主流程 → 允许 bag。
3. bag 职责命名：command 头 → `Headers`；业务完成注解 → `Annotations`；pipeline 临时共享上下文 → `Items`。
4. `Metadata` 判定：看对象语义边界，不看"是否跨层"。`request/response/event/command` 自身正式开放扩展信息可叫 `Metadata`；middleware/hook/pipeline 执行过程上下文叫 `Items`。
5. 保留原则：边界扩展袋天然就是开放式 metadata 时，保留 `Metadata`，不硬改成缩窄含义的名字。
6. 外部协议：第三方 SDK/外部协议原生 `Metadata` 允许在 adapter/boundary 保留；进入内部主模型后必须映射回 typed 字段或按职责命名结构。
7. 演进路径：仓库内可控的稳定语义优先 `proto field` 演进，不先用字符串 key 兜底。
8. 不匹配时：新增按职责命名的字段/子消息，不硬塞现有 bag，不把明确语义降级回通用 `Metadata`。

## Command / Envelope / Dispatch（强制）
- `Envelope` 是统一消息包络（`command/reply/signal/event/query`），但是否可持久化、可投影、可观察必须由消息契约显式定义。
- committed domain event 必须可观察：write-side 完成 committed event 后必须送入 projection 主链；禁止只落 event store 而不进入可观察流。
- 业务消息与查询语义分离：actor 间 event 链路是业务协议；readmodel 查询只读已物化事实；二者契约、一致性、完成判定不得混用。
- 禁止 generic actor query/reply：不得定义通用 `Query*Requested -> *Responded` 协议或通用 `request-reply client` 兜底读取；查询走 readmodel，跨 actor 交互走 command/event。
- 禁止 stream request-reply 冒充 RPC：stream 用于事件分发与观察；"先发消息再等 reply"必须改 readmodel 查询或 continuation 事件协议。
- 命令骨架内聚：标准生命周期 `Normalize -> Resolve Target -> Build Context -> Build Envelope -> Dispatch -> Receipt -> Observe`；业务模块只负责目标解析与载荷/结果映射。
- 传输载体可替换：上层依赖投递契约（`IActorDispatchPort`），不依赖具体载体；链路可从直投切换为异步传输而不污染应用语义。
- 投递语义 runtime-neutral：`publish/send` 统一表示"进入目标 inbox"；不因目标 `self` 或底层差异退化为 inline dispatch；需立即执行走独立 `dispatch` 契约，禁止绕过 publisher 直操底层传输对象。
- Runtime 与 Dispatch 分责：`Runtime` 负责 lifecycle/topology/lookup，`Dispatch Port` 负责投递；禁止揉成全能接口。
- ACK 诚实：同步返回只承诺已达到阶段（默认 `accepted + stable command id`）；`committed`/`read-model observed` 等强保证须通过独立契约或异步观察获取。
- 追踪标识与目标身份分离：`commandId/correlationId` 追踪请求，`actorId` 标识实体；禁止混用或假设一一对应。
- 命名跟随职责：接口/类型/目录命名描述职责边界，不泄露 `runtime/stream/protocol` 偶然细节。

## 权威状态 / ReadModel / Projection（强制）
- 单一权威拥有者：每个稳定业务事实有唯一 actor 拥有；`committed event store + actor state` 是唯一真相，readmodel 只是查询副本。
- 运行时形态不是业务事实：不得把本地实例类型、代理类型、对象可见结构当成业务绑定依据。
- 身份与事实分离：稳定 ID 只负责寻址与复用键；可变绑定必须显式建模、显式读取。
- 查询始终走 readmodel：对外查询只读 readmodel；不暴露 actor 内部状态、state mirror payload 或 event replay 为查询主路径。
- 写侧端口只负责 lifecycle/command；读取走窄 query contract 或 projection，禁止 Application/Infrastructure 直读 write-model 内部状态。
- 禁止侧读冒充 query：禁止直读其他 actor 的 event store、持久态快照或"事实重建器"拼装查询结果；跨 actor 读取走 readmodel 或 projection。
- 禁止 query-time replay/priming：`QueryPort/QueryService/ApplicationService` 不得在请求路径读 `IEventStore`、重放 events、临时重建 state mirror，或在 query 方法内同步补投影/补跑 ES/materialization；刷新须通过正式 projection 会话、后台 materializer 或写侧预挂接 projection 完成。
- `EventEnvelope` 是唯一投影传输壳：业务消息与投影消息都用 `EventEnvelope`；区别由强类型 payload 表达，禁止引入第二层包络。
- 业务一致性与查询一致性分层：actor 间链路对"消息已接收/事件已提交/协议已推进"负责；readmodel 对"某 `StateVersion` 已物化可见"负责；禁止混用。
- 一权威状态 → 多 readmodel：不同 readmodel 表达同一 actor 当前态的不同查询形态，不得各自重算业务状态机。
- readmodel 按需创建：只有存在稳定消费场景（明确消费方、查询入口、返回 DTO）时才新增 readmodel。
- readmodel 根契约：仓库内 `readmodel` 默认表示 `actor-scoped current-state replica`；不符合的改名降级为 `artifact/export/log`，或由 aggregate actor 拥有。
- 聚合必须 actor 化：跨 actor 聚合/汇总/关联若有稳定业务语义，建模为 aggregate actor；禁止长期放在 query-time 拼装层。
- projection 只消费 committed 事实：基于 committed domain event 或其同源 durable feed 构建；禁止订阅入站 command、self continuation 或 actor 运行时偶然结构。
- projection 负责物化，不负责推导：消费 `EventEnvelope<CommittedStateEventPublished>` 的 `state_event + state_root` 物化到 document/index/search/graph store；actor 内已确定的当前态语义前移到 actor。
- actor 不直接拥有存储实现：actor 发布 `state_root` 作为 readmodel 统一 committed 输入，但物化职责属于 projection/runtime/provider 边界。
- 正常路径禁止 replay：query path 和 projection path 不依赖 `event replay/rebuild/backfill`；replay 只属于后台修复/迁移/灾难恢复。
- 版本对齐权威源：readmodel 版本必须来自权威 actor 的 committed version 或等价水位；禁止本地 projection counter 或 `StateVersion++` 冒充权威版本。
- 覆盖复制优先：readmodel 写入语义是"基于权威源版本的单调覆盖"；旧不覆盖新，重复幂等，冲突报错。
- 不默认保留历史视图：`timeline/audit/report/analytics` 不是默认 readmodel 形态；如有业务价值，降级为 artifact/export 或由专门 actor 拥有。
- 查询诚实：readmodel 可最终一致，但必须暴露权威源版本或刷新戳；禁止在弱读结果上暗示强一致。
- 状态镜像契约面向查询：state mirror payload 作为 readmodel 输入时须是面向读侧的稳定强类型契约，非 actor 内部 state 的原样 dump。
- 默认路径须定义资源语义：任何"缺失即创建"策略须同时定义归属、复用规则和清理责任。
- 本地可用不等于分布式正确：依赖本地 runtime 偶然细节才成立的实现视为未完成设计。
- 抽象一旦能被滥用即设计未完成：允许绕过读写分离/actor 边界/权威源的通用接口须继续收窄。

## Actor 设计 / 生命周期 / 执行模型（强制）
- Actor 以业务命名：actor 类型和 ID 描述业务实体，禁止 `WriteActor`、`ReadModelActor`、`StoreActor` 等技术功能命名。
- 读写分离在 Projection Pipeline 层实现，不在 actor 层实现：actor 拥有完整业务状态并处理命令；committed event 流入 Projection Pipeline 物化查询视图。
- 应用层契约以业务命名：读端口用 `IXxxQueryPort`，写命令通过 `IActorDispatchPort` 或等价命令分发机制；禁止 `IXxxStore` 等存储导向命名出现在应用层，endpoint 不直接依赖 `IActorRuntime`/`IProjectionDocumentReader` 等基础设施抽象；应用层契约必须承载业务语义，禁止纯转发空壳。
- 面向对象内聚：同一业务实体的状态、命令处理、事件发布在同一个 actor 内完成；禁止将数据和方法拆分到不同 actor 再拼装。
- 默认短生命周期：一次执行/会话/编排即完成的能力，建模为 `run/session/task-scoped actor`；GAgent、workflow、scripting 只要协议一致均可作为实现来源。
- 长期 actor 限定事实拥有者：`definition/catalog/manager/index/checkpoint` 等需长期持有权威状态、串行推进事实的对象。
- 单线程 actor 不做热点共享服务：actor 用于维护状态边界和顺序语义，不用于承接无限扩张的共享吞吐。
- 升级前滚：默认"旧 run 留旧实现，新请求走新实现"；无状态迁移契约时禁止原地热替换。
- `actorId` 对调用方不透明：不得解析前缀/类型名/实现来源，不得把字面模式当业务判断条件。
- 单线程事实源：运行态只在事件处理主线程修改；禁止 `lock/Monitor/ConcurrentDictionary` 作为并发补丁维护事实状态。需加锁时先重构为事件化串行模型。
- 回调只发信号：`Task.Run`/`Timer`/线程池回调不直接读写运行态或推进业务；只发布内部触发事件。
- 业务推进内聚：工作流推进（成功/失败/分支/重试）在 Actor 事件处理流程内完成，保证顺序性与可重放性。
- AI 对话主链必须流式化：实时会话入口必须使用 `ChatStreamAsync`；`ChatAsync` 仅可用于明确的非交互式离线场景。
- self continuation 事件化：Actor 需"下一拍继续"时通过标准 self-message 进入自身 inbox 再消费；禁止绕过消息抽象的临时 helper。
- 延迟/超时事件化：`delay/timeout/retry backoff` 统一"异步等待 → 发布内部事件 → Actor 内消费并对账"；禁止回调线程直接改状态。
- 跨 actor 等待 continuation 化："发送请求 → 结束当前 turn → reply/timeout event 唤醒继续"；禁止当前 turn 同步等待或通过侧读/伪 RPC 绕过。
- query 与 command 边界分清：读已提交事实 → 读 readmodel；需对方参与新业务交互 → 发 command/event + reply/timeout continuation。
- 显式对账：内部触发事件携带最小充分相关键（如 `run_id + step_id`），Actor 内做活跃态校验，拒绝陈旧事件。

## 中间层状态约束（强制）
- 禁止中间层维护 `entity/actor/workflow-run/session` 等 ID → 上下文/事实状态的进程内映射（`Dictionary<>`/`ConcurrentDictionary<>`/`HashSet<>`/`Queue<>`）。
- Actor 内部运行态集合可保留在内存或 Actor `State`；前提是不作为跨节点事实源，并按生命周期及时清理。
- 跨 Actor/跨节点一致性状态优先 Actor 持久态；无法放入时用抽象化分布式状态服务，禁止中间层进程内缓存作为事实源。
- `InMemory` 实现仅限开发/测试，不外溢到中间层业务语义。
- 方法内局部临时集合可用，不得提升为服务级/单例级事实状态字段。
- 投影端口禁止 `actorId -> context` 反查管理生命周期，改为显式 `lease/session` 句柄传递。

## 序列化（强制）
- 统一 Protobuf：`State`、领域事件、命令、回调载荷、快照、缓存载荷、跨 Actor/跨节点内部传输对象全部使用 Protobuf。
- 禁止 JSON/XML/自定义字符串格式用于 Actor State、WorkflowRun State、模块持久态、投影检查点等事实存储。
- 外部协议必须 JSON 时，仅在 Host/Adapter 边界做协议转换；进入应用/领域/运行时层后恢复为 Protobuf。
- 新增状态/事件/持久化载荷：先定义 `.proto` 并生成类型，再接入实现；禁止先写临时结构后补 Protobuf。

## 工程约定（精简）
- 文档：`docs/canon/` 是权威参考，`docs/adr/` 是不可变 ADR，`docs/history/` 是非权威归档；架构词汇见 `docs/canon/architecture-vocabulary.md`。
- `docs/canon/` 一个 topic 一个文件，不重复建权威文档；新增或调整架构口径时优先更新既有 canon。
- `docs/adr/` 只追加新决策，不改写历史决策；被替代的 ADR 通过新 ADR supersede。
- `docs/history/` 仅放归档快照，正文必须明确非权威，不得被实现或测试当作规范来源。
- AI 生成的设计文档默认不保留到 `docs/`；需要保留时必须有 `title/status/owner` frontmatter 并放入对应目录。
- `docs/canon/` 和 `docs/adr/` 文件必须有 YAML frontmatter（`title/status/owner`）；文档 lint 使用 `tools/docs/lint.sh`，已纳入 CI 门禁。
- 根目录 `.md` 只保留 `CLAUDE.md`、`README.md`、`CHANGELOG.md`、`LICENSE`、`AGENTS.md`；`docs/README.md` 由工具生成，不手动编辑。
- 项目结构：`src/` 放生产代码，`test/` 放对应测试，`tools/Aevatar.Tools.Cli` 是 CLI 项目，`workflows/` 放 YAML 工作流。
- `src/` 按能力与分层组织；保持项目名、命名空间、目录语义一致。
- `test/` 与 `src/` 对应；测试文件命名 `*Tests.cs`，单文件聚焦一个行为域。
- `tools/` 放开发工具，`demos/` 放示例程序；工作文档不加入 `aevatar.slnx`。
- 构建：使用 `dotnet restore/build/test aevatar.slnx --nologo`；仓库内禁止新增 `5000` 端口示例或默认值，Web API 同时禁用 `5000` 与 `5050`。
- 本地运行 Workflow API 使用 `dotnet run --project src/workflow/Aevatar.Workflow.Host.Api`。
- dotnet 命令统一带 `--nologo`；新增脚本、README、CLI 示例、测试样例必须与端口约束一致。
- 全量测试使用 `dotnet test aevatar.slnx --nologo`；单项目覆盖率按对应测试项目显式运行。
- 编码风格：遵循 `.editorconfig`；公开 API 与领域对象命名表达业务意图，避免含糊词。
- 先抽象后实现，优先接口注入，避免跨层直接调用；不需要的代码直接删除。
- 公开命名避免含糊词；接口、DTO、事件、ReadModel 名称必须表达职责与业务语义。
- 前端：前端请求默认遵循 `aevatar-frontend-design` skill；结果必须响应式、键盘可达、真实内容密度下仍可读。
- 前端改动优先抽取 design tokens / CSS variables，不接受大面积零散硬编码。
- 测试：行为变更必须补测试；禁止用 `[Skip]` 或 disable 测试换绿；禁止随意 `Task.Delay(...)`/`WaitUntilAsync(...)`，确需最终一致性探测时必须加入 allowlist 并说明原因。
- 测试栈为 xUnit、FluentAssertions、`coverlet.collector`；重构不得降低关键路径覆盖率。
- 自动生成代码不纳入覆盖率考核；不得把覆盖率作为脚手架生成代码的合并门禁。
- CI full-scan 禁止 `GetAwaiter().GetResult()`、`TypeUrl.Contains(...)` 字符串路由、投影端口 `actorId` 反查上下文。
- 新增非抽象 `Reducer` 类必须有测试引用；事件类型到 reducer 路由必须使用精确键路由。
- 守卫：提交前按变更范围运行对应 `tools/ci/*_guard*.sh`；架构相关默认跑 `bash tools/ci/architecture_guards.sh`，测试相关默认跑 `bash tools/ci/test_stability_guards.sh`。
- 涉及 query/read、projection lifecycle、state version、workflow binding、CLI playground 静态资源时，运行对应专项 guard。
- 若新增或修改测试，提交前必须运行 `bash tools/ci/test_stability_guards.sh`。
- Git：分支命名 `<type>/YYYY-MM-DD_<purpose>`；提交信息用祈使句并聚焦单一目的；PR 写明问题与方案、影响路径、验证命令与结果。
- 分支 `type` 仅限 `feat/fix/refactor/docs/test/chore`；日期固定 `YYYY-MM-DD`；purpose 只用小写字母、数字、连字符。
- 架构调整 PR 必须同步相关 `docs/`，并在验证结果中列出 build/test/guard。
- 不保留历史副本：废弃文件直接删除，不创建 `.bak/.old/.deprecated` 等长期遗留；历史由 git 保存。
- Mermaid：仓库 docs 图首行使用统一 `%%{init: ...}%%` 指令，标签加引号；GitHub issue/PR comment 优先 ASCII/表格，复杂 mermaid 只放仓库 docs。
- 文档文件名如带时间戳，必须前置定长：`YYYY-MM-DD-` 或 `YYYY-MM-DD-HH-mm-ss-`。
- Mermaid `sequenceDiagram` 默认紧凑布局，避免固定大宽度；需要细节时让外层容器横向滚动。
- gstack：网页浏览与 QA 使用 gstack `/browse` 等 skill；不要直接调用底层 Chrome MCP 工具。
- Skill routing：请求明确匹配仓库内 skill 时优先使用对应 skill；skill 已自包含的操作细则不复制回本文件。
- Codex loop 细则由 `.claude/skills/codex-refactor-loop/` 与 `.claude/skills/codex-implement-loop/` 自维护；`CLAUDE.md` 只保留跨流程架构与工程边界。
