# Repository Guidelines

## 顶级架构要求（最高优先级）
- 严格分层：`Domain / Application / Infrastructure / Host`，`API` 仅做宿主与组合，不承载核心业务编排。
- 统一投影链路：CQRS 与 AGUI 走同一套 Projection Pipeline，统一入口、一对多分发，避免双轨实现。
- 投影编排 Actor 化：Projection 的会话、订阅、关联关系等运行态必须由 Actor 或分布式状态承载；禁止在中间层通过进程内注册表/字典持有事实状态。
- 明确读写分离：`Command -> Event`，`Query -> ReadModel`；异步完成通过事件通知与推送，不在会话内临时拼装流程。
- 严格依赖反转：上层依赖抽象，禁止跨层反向依赖和对具体实现的直接耦合。
- 命名语义优先：项目名、命名空间、目录一致；缩写全大写（如 `LLM/CQRS/AGUI`）；集合语义使用复数。
- API 字段单一语义：一个字段只能表达一个含义，禁止同字段承载“名称查找 + inline 内容”等双重语义。
- 核心语义强类型：凡是影响业务语义、控制流、稳定读取且仓库内生产方/消费方可控的数据，必须建模为 `proto field / typed option / typed sub-message`，禁止塞入通用 bag。
- `Metadata` 命名受限：禁止内部无语义、泛化的 `Metadata` 命名；只有真正的元模型、明确的开放扩展边界、外部协议/第三方 SDK/基础设施标准术语才允许保留 `Metadata`，其余内部场景必须按职责命名，如 `Headers / Annotations / Items`。
- 不保留无效层：空转发、重复抽象、无业务价值代码直接删除。
- 变更必须可验证：架构调整需同步文档，且 `build/test` 通过。

## 架构设计哲学（抽象）
- 单一主干，插件扩展：系统只保留一条权威业务主链路；新增能力以插件/模块方式挂载，避免平行“第二系统”。
- 内核最小化：核心层只承载稳定业务不变量与通用机制；波动能力下沉到扩展层，减少核心侵蚀。
- 扩展对称性：内建能力与扩展能力遵循同一抽象模型与生命周期协议，不为扩展单独再造体系。
- 抽象优先：依赖行为契约与语义接口，而非具体类型与实现细节；组合面向能力，非面向实现。
- 边界清晰：协议适配、业务编排、状态管理分别归属不同层；每层只做本层职责，禁止跨层偷渡语义。
- 事实源唯一：跨请求/跨节点的一致性事实必须有唯一权威来源（Actor 持久态或分布式状态），不依赖进程内偶然状态。
- 数据语义分层：传输元数据用于追踪与上下文；业务语义以领域事件与读模型为准，不混用语义层次。
- 强类型内核，窄扩展点：内核稳定语义默认强类型；只有在确有插件/第三方/跨边界透传需求时才保留 bag。边界 bag 可以保留 `Metadata`，但内部 bag 不得退回到泛化 `Metadata`。
- 渐进演进：开发期可用本地/内存实现提升反馈速度，但生产语义必须能无缝迁移到分布式与持久化实现。
- 删除优于兼容：重构以清晰正确为第一目标；无业务价值或重复层应直接删除，不为历史包袱保留空壳。
- 治理前置：架构规则必须可自动化验证（门禁、测试、文档一致性），避免依赖口头约定。

## 字段命名与扩展决策（强制）
- 先判定是否属于核心语义：凡是影响业务语义、控制流、稳定查询或稳定决策的数据，直接建模为强类型字段；不要因为“未来可能还会扩展”就先放 bag。
- bag 只用于明确的开放扩展边界：生产方和消费方不完全同源、允许第三方追加字段、且缺失字段不应破坏主流程时，才允许保留 bag。
- bag 必须服从边界职责：command 头用 `Headers`，业务完成注解用 `Annotations`，链内临时状态用 `Items`；真正的边界扩展袋可以保留 `Metadata`，或使用更精确但不失真的边界名称。
- `Metadata` 看的是对象语义边界，不是“是否跨层”：如果它是 `request/response/event/command` 自身的正式扩展信息，可以叫 `Metadata`；如果它只是 middleware / hook / pipeline 执行过程里的进程内临时共享上下文，即使会跨多个处理层，也应叫 `Items`。
- 外部新增信息不适合现有 bag 时，新增一个按职责命名的字段或子消息；不要把它硬塞进不匹配的现有 bag，也不要把内部明确语义重新降级成通用 `Metadata`。
- 不要为了消灭单词而窄化语义：如果一个边界扩展袋天然就是开放式 metadata，就保留 `Metadata`，不要硬改成会缩窄含义的名字。
- protobuf 字段演进是默认路径：仓库内可控的稳定语义优先通过新增 `proto field / typed sub-message / typed option` 演进，而不是先用字符串 key 兜底。
- 外部名称尊重边界：第三方 SDK、外部协议或基础设施标准术语如果原生使用 `Metadata`，允许在 adapter/boundary 保留该命名；但进入仓库内部主模型后，必须映射回 typed 字段或按职责命名的内部结构，禁止把外部 `Metadata` 语义原样扩散到内核。

## Command / Envelope / Dispatch 抽象（强制）
- 统一包络不等于统一语义：`Envelope` 只是 Actor System 的统一消息包络，可承载 `command/reply/internal signal/domain event/query`；是否可持久化、可投影、可对外观察必须由消息契约显式定义，禁止因“都走 Envelope”而混淆语义。
- 已提交领域事件必须可观察：write-side 一旦完成 committed domain event，必须把该事实送入统一 observation/projection 主链；禁止只落 event store / actor state 而不进入可观察流，再由上层用 query fallback 猜测完成态。
- 禁止 generic actor query/reply：内部模块不得为“读取另一个 actor 当前状态”定义通用 `Query*Requested -> *Responded` 协议，也不得保留通用 `request-reply client` 作为兜底读取手段；查询默认只能落到 read model，跨 actor 交互默认只能是 command/event 驱动的业务协议。
- 禁止用 stream request-reply 冒充 RPC：`stream` 用于事件分发与观察，不用于在 actor turn 内实现同步 query/reply；凡是“先发消息、再等另一条 reply 消息回来”的链路，都必须改成正式 read model 查询，或改成 continuation 化的事件协议。
- 命令骨架必须内聚：标准命令生命周期应收敛为 `Normalize -> Resolve Target -> Build Context -> Build Envelope -> Dispatch -> Receipt -> Observe`；业务模块只负责目标解析、载荷映射和结果映射，禁止各能力入口各自拼装一套流程。
- 传输载体必须可替换：直接远程调用、`IActorDispatchPort`、stream/broker 都只是消息传输机制；上层依赖投递契约，不依赖具体载体，确保链路可从直投切换为异步传输而不污染应用语义。
- 投递语义必须 runtime-neutral：`publish/send` 统一表示“进入目标 actor inbox 等待处理”，不得因目标是 `self` 或底层 runtime 差异而退化为 inline dispatch；需要立即执行时必须走独立 `dispatch` 契约，禁止在基类、业务层或中间适配层绕过标准 publisher 直接操作 `stream/provider/grain` 等底层传输对象。
- Runtime 与 Dispatch 必须分责：`Runtime` 负责 actor 的 lifecycle / topology / lookup，`Dispatch Port` 负责消息投递；禁止把创建、查询、投递、观察等职责揉进一个全能接口。
- ACK 语义必须诚实：同步返回只能承诺已经真实达到的阶段，默认应是 `accepted for dispatch + stable command id`；`committed`、`read-model observed` 等更强保证必须通过独立契约或异步观察获取，禁止在弱语义 ACK 中暗示强保证。
- 追踪标识与目标身份必须分离：`commandId/correlationId` 用于追踪一次请求，`actorId` 用于标识处理实体；禁止把追踪 ID 与目标身份混成同一语义，也不得假设二者天然一一对应。
- 命名必须跟随职责语义：接口、类型、目录命名应描述职责与边界，而不是绑定暂时实现路径；一旦底层实现可替换，命名不得泄露 `runtime/stream/protocol` 偶然细节。

## 复盘抽象（强制）
- 运行时形态不是业务事实：不得把本地实例类型、代理类型、对象可见结构当成业务绑定依据；业务事实必须来自 actor-owned contract 或 read model。
- 身份与事实必须分离：稳定 ID 只负责寻址与复用键，不承载可变业务事实；可变绑定必须显式建模、显式读取。
- 读写边界不能混合：写侧端口只负责 lifecycle / command；读取必须走窄 query contract 或 projection，禁止在 Application / Infrastructure 直接读取 write-model 内部状态。
- 禁止用侧读冒充 query：当系统缺少正式 query/reply 语义时，禁止通过直读其他 actor 的 event store、持久态快照或任意“事实重建器”在中间层拼装查询结果；这类跨 actor 读取必须回到 actor-owned contract、projection，或显式的事件化 continuation。
- 禁止 query-time replay：`QueryPort / QueryService / ApplicationService / Infrastructure read adapter` 不得在请求路径中直接读取 `IEventStore`、重放 committed events、临时重建 snapshot/document 后立刻返回；事实回放只能属于正式 projection/materialization 流程，不属于 query 执行流程本身。
- read model 物化必须脱离 query 调用栈：如果查询需要“先刷新 read model”，刷新动作也必须通过正式 projection 会话、后台 materializer、写侧预挂接 projection，或显式的 read-model 更新管线完成；禁止在 query 方法里同步补跑一遍 ES/materialization 逻辑。
- projection 只消费 committed 事实：projection/read model 必须基于 committed domain event 或其同源 durable feed 构建；禁止订阅入站 command、self continuation 或 actor 运行时偶然结构去推测业务完成态。
- 默认路径必须先定义资源语义：任何“缺失即创建”的默认策略，都必须同时定义稳定归属、复用规则和清理责任；禁止生成不可达、不可复用、不可回收的隐式资源。
- 本地可用不等于分布式正确：凡是依赖本地 runtime 偶然细节才能成立的实现，都视为未完成设计，必须收敛到 runtime-neutral 协议。
- 抽象一旦能被滥用，就等于设计未完成：若某个通用接口允许绕过读写分离、绕过 actor 边界或绕过权威事实源，应继续收窄，而不是靠约定克制。

## Actor 生命周期判定（强制）
- 默认优先 `run/session/task-scoped actor`：凡是一次执行、一次会话、一次临时编排即可完成职责的能力，默认建模为短生命周期 actor；静态 `GAgent`、workflow、scripting 只要协议一致，都可作为这类 actor 的实现来源。
- 长期 actor 只保留给事实拥有者：只有 `definition/catalog/manager/index/checkpoint` 这类需要长期持有权威状态、串行推进事实的对象，才允许设计为长期 actor。
- 单线程 actor 不承担热点共享服务：禁止把 actor 当成高并发无状态公共服务容器；单线程 actor 用于维护状态边界和顺序语义，不用于承接无限扩张的共享吞吐。
- 升级默认前滚，不热替换存量 run：脚本、workflow、静态实现升级时，默认语义是“旧 run 留在旧 actor/旧定义，新的创建请求走新实现”；除非明确设计了状态迁移契约，否则禁止对正在运行的 actor 做原地实现替换。
- `actorId` 对调用方是不透明地址：允许更换其背后实现，但调用方不得解析前缀、类型名或实现来源，也不得把 `actorId` 字面模式当成业务判断条件。

## Actor 化执行哲学（强制）
- 单线程事实源：Actor/模块运行态只能在事件处理主线程修改；禁止在模块内使用 `lock/Monitor/ConcurrentDictionary` 作为并发补丁来维护事实状态。
- 回调只发信号：`Task.Run`、`Timer`、线程池回调不得直接读写运行态，也不得直接推进业务分支；只能发布“内部触发事件”（如 timeout/retry fired）。
- 业务推进内聚：工作流推进（成功/失败/分支/重试）必须在 Actor 事件处理流程内完成，保证顺序性与可重放性。
- self continuation 必须事件化：Actor 需要“下一拍继续”时，必须通过标准 self-message 进入自身 inbox，再由 Actor 事件处理流程消费；禁止新增绕过消息抽象的临时 helper，或依赖特定 runtime 的 self-dispatch 偶然行为来推进业务。
- 延迟与超时事件化：所有 `delay/timeout/retry backoff` 统一采用“异步等待 -> 发布内部事件 -> Actor 内消费并对账”的模式，禁止回调线程直接改状态。
- 跨 actor 等待必须 continuation 化：Actor 向其他 actor 请求事实或动作时，必须采用“发送请求事件 -> 结束当前 turn -> 由 reply event 或 timeout event 唤醒自身继续处理”的模型；禁止在当前 turn 内同步等待 reply，也禁止用本地快照读取、event store 侧读或伪 RPC 绕过这一约束。
- query 与 command 的 actor 边界必须分清：actor 若只是想读取另一侧已提交事实，就不应给对方 actor 发 query 消息，而应读取该事实对应的 read model；只有确实需要对方参与一次新的业务交互时，才允许发送事件/命令，并由 reply/timeout continuation 继续推进。
- 显式对账：内部触发事件必须携带最小充分相关键（如 `run_id + step_id`），由 Actor 内做活跃态校验，拒绝陈旧事件。
- 无锁优先：若设计需要加锁才能正确，优先判定为“破坏 Actor 边界”，应先重构为事件化串行模型，再实现功能。

## 中间层状态约束（强制）
- 禁止在中间层维护 `entity/actor/workflow-run/session` 等 ID 到上下文或事实状态的进程内映射（`Dictionary<>`、`ConcurrentDictionary<>`、`HashSet<>`、`Queue<>`）。
- 允许 Actor 内部运行态集合保留在内存或 Actor `State`（例如 `module_runtime`）；前提是该状态不作为跨节点事实源，并且按生命周期及时清理。
- 需要跨 Actor/跨节点一致性的显式状态时：优先写入 Actor 持久态；无法放入 Actor 时，使用抽象化分布式状态服务，不允许在中间层落进程内缓存作为事实源。
- 明确例外：`InMemory` 持久化/基础设施实现仅用于开发与测试，可保留，但不得外溢到中间层业务语义。
- 方法内局部临时集合可用，但不得提升为服务级/单例级事实状态字段。
- 投影端口规范：禁止通过 `actorId -> context` 反查方式管理生命周期，改为显式 `lease/session` 句柄传递。

## 序列化约束（强制）
- 所有序列化与反序列化操作统一使用 `Protobuf`，尤其是 `State`、领域事件、命令、回调载荷、快照、缓存载荷、跨 Actor/跨节点内部传输对象。
- 禁止在 `Actor State`、`WorkflowRun State`、模块持久态、投影检查点或其他事实存储中使用 `JSON/XML/自定义字符串格式` 作为内部序列化方案。
- 若外部协议或第三方接口必须使用 `JSON`，仅允许在 `Host/Adapter` 边界做临时协议转换；进入应用层、领域层、运行时层后必须恢复为 `Protobuf` 对象，且内部落盘、持久化、发布、重放仍统一使用 `Protobuf`。
- 新增状态对象、事件对象、持久化载荷时，先定义 `.proto` 契约并生成类型，再接入实现；禁止先写临时序列化结构、后补 `Protobuf`。

## 项目结构与模块组织
- `src/`：生产代码，按能力与分层组织（`Aevatar.Foundation.*`、`Aevatar.Workflow.Core`、`Aevatar.AI.*`、`Aevatar.CQRS.Projection.Abstractions/Core/WorkflowExecution`、`Aevatar.Host.*`）。
- `test/`：与 `src/` 对应的测试项目（单元、集成、API）。
- `docs/`：架构与设计文档；`workflows/`：YAML 工作流定义。
- `tools/`：开发工具；`demos/`：示例与演示程序。

## 构建、测试与本地运行
- `dotnet restore aevatar.slnx --nologo`：还原依赖。
- `dotnet build aevatar.slnx --nologo`：编译全部项目。
- `dotnet test aevatar.slnx --nologo`：运行全量测试。
- `bash tools/ci/architecture_guards.sh`：本地执行 CI 架构门禁（与 CI 同步）。
- `bash tools/ci/workflow_binding_boundary_guard.sh`：单独执行 workflow binding 边界门禁。
- `bash tools/ci/projection_route_mapping_guard.sh`：单独执行“事件类型 -> reducer 路由映射正确性”静态门禁。
- `bash tools/ci/solution_split_guards.sh`：执行分片构建门禁（Foundation/AI/CQRS/Workflow/Hosting）。
- `bash tools/ci/solution_split_test_guards.sh`：执行分片测试门禁（Foundation/CQRS/Workflow）。
- `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --collect:"XPlat Code Coverage"`：单项目覆盖率。
- `dotnet run --project src/workflow/Aevatar.Workflow.Host.Api`：启动 Workflow API（`/api/chat`、`/api/ws/chat`）。

## 编码风格与命名规范
- 遵循 `.editorconfig`：UTF-8、LF、4 空格缩进、去除行尾空白。
- 保持 `项目名 = 命名空间 = 目录语义`，推荐模式：`Aevatar.<Layer>.<Feature>`。
- 先抽象后实现；优先接口注入；避免跨层直接调用。
- 公开 API 与领域对象命名要表达业务意图，避免含糊词。
- 把不需要的直接删除, 无需考虑兼容性

## 测试与质量门禁
- 测试栈：xUnit、FluentAssertions、`coverlet.collector`。
- 测试文件命名：`*Tests.cs`，单文件聚焦一个行为域。
- 行为变更必须补测试；重构不得降低关键路径覆盖率。
- 自动生成代码（脚手架生成）不纳入代码覆盖率考核，不将覆盖率作为其合并门禁。
- 轮询等待门禁（`tools/ci/test_stability_guards.sh`）为强制项：测试中禁止随意引入 `Task.Delay(...)`/`WaitUntilAsync(...)`。
- 若确属跨进程/跨节点最终一致性探测且无法改为确定性同步（如 `TaskCompletionSource`/`Channel`），必须将测试文件路径显式加入 `tools/ci/test_polling_allowlist.txt`，并在变更说明里写明原因。
- 涉及测试新增/修改时，提交前必须执行：`bash tools/ci/test_stability_guards.sh`。
- 涉及 workflow actor binding、definition identity、resume/signal 路径的变更时，提交前必须执行：`bash tools/ci/workflow_binding_boundary_guard.sh`。
- CI 守卫（full-scan）：禁止 `GetAwaiter().GetResult()`；禁止 `TypeUrl.Contains(...)` 字符串路由；禁止 `Aevatar.Workflow.Core` 依赖 `Aevatar.AI.Core`；禁止中间层 `actor/entity/run/session` ID 映射 Dic 事实态字段（仅扫描 Projection/Application/Orchestration 中间层）；禁止投影端口回退到 `actorId` 反查上下文模型；要求新增非抽象 `Reducer` 类必须被测试引用；要求事件类型到 reducer 的路由采用 `TypeUrl` 派生 + 精确键路由（由 `tools/ci/projection_route_mapping_guard.sh` 专项校验，含 `EventTypeUrl` 分组与 `TryGetValue` 命中）。

## 提交与 PR 规范
- `GIT` 分支命名使用固定格式，不允许任何变体：`<type>/YYYY-MM-DD_<purpose>`。
- `<type>` 必须从固定集合中选择：`feat`、`fix`、`refactor`、`docs`、`test`、`chore`。
- 日期必须使用前置定长格式 `YYYY-MM-DD`；禁止使用 `2026-3-12`、`20260312`、`03-12-2026`、后置日期或日期时间混合格式。
- `<purpose>` 只允许使用小写字母、数字和连字符 `-`，应简短表达单一目标；禁止空泛名称如 `test`、`tmp`、`misc`。
- 标准示例：`feat/2026-03-12_gagent-protocol-first-plan`。
- 提交信息使用祈使句并聚焦单一目的（如：`Refactor projection pipeline`）。
- PR 必须包含：问题与方案、影响路径、验证命令与结果、相关文档更新。
- 若涉及架构调整，需同时更新 `docs/` 架构文档与示意图。

## 文档

- mermaid 默认指令（所有图统一加在代码块首行）：
  `%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%`
- mermaid 的标签用引号包起来, 如 A2[“RoleGAgent”], 不要 A2[RoleGAgent]. 
- `sequenceDiagram` 默认使用紧凑布局（优先收紧 `actorMargin/messageMargin/diagramMarginX/diagramMarginY` 与文案长度），避免图整体过大。
- 禁止通过固定大宽度样式撑大时序图（如 `min-width: 2200px`、`width: max-content` 强制放大）；优先按容器宽度渲染。
- 需要查看完整细节时，使用外层容器横向滚动（`overflow-x: auto`），不要放大图本体。
- 文件名中带时间/日期的文档，时间戳必须放在文件名开头，禁止后置时间戳。
- 时间戳必须使用固定字长格式：仅日期用 `YYYY-MM-DD-`（10 位日期 + 1 位连接符），日期时间用 `YYYY-MM-DD-HH-mm-ss-`（19 位日期时间 + 1 位连接符）。
- 禁止在同类文档中混用 `2026-3-9`、`20260309`、`03-09-2026`、尾部 `-2026-03-09` 等变长或后置格式；统一使用前置定长格式，例如 `2026-03-09-workflow-architecture.md`。
- 默认将打分/审计文档生成到 `docs/audit-scorecard/` 目录。
- 工作文档不需要添加到解决方案（`aevatar.slnx`）。
