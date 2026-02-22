# Repository Guidelines

## 顶级架构要求（最高优先级）
- 严格分层：`Domain / Application / Infrastructure / Host`，`API` 仅做宿主与组合，不承载核心业务编排。
- 统一投影链路：CQRS 与 AGUI 走同一套 Projection Pipeline，统一入口、一对多分发，避免双轨实现。
- 投影编排 Actor 化：Projection 的会话、订阅、关联关系等运行态必须由 Actor 或分布式状态承载；禁止在中间层通过进程内注册表/字典持有事实状态。
- 明确读写分离：`Command -> Event`，`Query -> ReadModel`；异步完成通过事件通知与推送，不在会话内临时拼装流程。
- 严格依赖反转：上层依赖抽象，禁止跨层反向依赖和对具体实现的直接耦合。
- 命名语义优先：项目名、命名空间、目录一致；缩写全大写（如 `LLM/CQRS/AGUI`）；集合语义使用复数。
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
- 渐进演进：开发期可用本地/内存实现提升反馈速度，但生产语义必须能无缝迁移到分布式与持久化实现。
- 删除优于兼容：重构以清晰正确为第一目标；无业务价值或重复层应直接删除，不为历史包袱保留空壳。
- 治理前置：架构规则必须可自动化验证（门禁、测试、文档一致性），避免依赖口头约定。

## 中间层状态约束（强制）
- 禁止在中间层维护 `entity/actor/workflow-run/session` 等 ID 到上下文或事实状态的进程内映射（`Dictionary<>`、`ConcurrentDictionary<>`、`HashSet<>`、`Queue<>`）。
- 允许 Actor 内部运行态集合保留在内存或 Actor `State`（例如 `module_runtime`）；前提是该状态不作为跨节点事实源，并且按生命周期及时清理。
- 需要跨 Actor/跨节点一致性的显式状态时：优先写入 Actor 持久态；无法放入 Actor 时，使用抽象化分布式状态服务，不允许在中间层落进程内缓存作为事实源。
- 明确例外：`InMemory` 持久化/基础设施实现仅用于开发与测试，可保留，但不得外溢到中间层业务语义。
- 方法内局部临时集合可用，但不得提升为服务级/单例级事实状态字段。
- 投影端口规范：禁止通过 `actorId -> context` 反查方式管理生命周期，改为显式 `lease/session` 句柄传递。

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
- CI 守卫（full-scan）：禁止 `GetAwaiter().GetResult()`；禁止 `TypeUrl.Contains(...)` 字符串路由；禁止 `Aevatar.Workflow.Core` 依赖 `Aevatar.AI.Core`；禁止中间层 `actor/entity/run/session` ID 映射 Dic 事实态字段（仅扫描 Projection/Application/Orchestration 中间层）；禁止投影端口回退到 `actorId` 反查上下文模型；要求新增非抽象 `Reducer` 类必须被测试引用；要求事件类型到 reducer 的路由采用 `TypeUrl` 派生 + 精确键路由（由 `tools/ci/projection_route_mapping_guard.sh` 专项校验，含 `EventTypeUrl` 分组与 `TryGetValue` 命中）。

## 提交与 PR 规范
- 提交信息使用祈使句并聚焦单一目的（如：`Refactor projection pipeline`）。
- PR 必须包含：问题与方案、影响路径、验证命令与结果、相关文档更新。
- 若涉及架构调整，需同时更新 `docs/` 架构文档与示意图。

## 文档

- mermaid 默认指令（所有图统一加在代码块首行）：
  `%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%`
- mermaid 的标签用引号包起来, 如 A2[“RoleGAgent”], 不要 A2[RoleGAgent]. 
- 默认将打分/审计文档生成到 `docs/audit-scorecard/` 目录。
- 工作文档不需要添加到解决方案（`aevatar.slnx`）。
