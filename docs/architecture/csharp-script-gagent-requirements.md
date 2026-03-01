# C# Script GAgent Requirements

## 1. 文档元信息
- 状态: Draft
- 版本: v0.2
- 日期: 2026-03-01
- 适用范围: `src/Aevatar.Foundation.*`、`src/Aevatar.CQRS.Projection.*`、`src/workflow/*`、Host 装配层
- 文档定位: 定义“可由 C# 脚本构建的 GAgent”需求基线，确保与静态 GAgent/YAML Workflow GAgent 同主链语义
- 最近一次验证结果: 已完成首批实现与验证，`build`/脚本专项测试/架构门禁通过

## 2. 背景与关键决策（统一认知）
现有体系已具备两类能力：
1. 静态编译期 GAgent（`GAgentBase<TState>`）。
2. YAML 配置驱动的 Workflow GAgent（`WorkflowGAgent` + module pack）。

新增目标是第三类能力：通过 C# 脚本定义 GAgent，并满足与静态类同等级能力边界。

关键决策：
1. 采用“单主干 + 脚本能力包”模型，不引入第二套运行时主链路。
2. 脚本不直接成为任意运行时代码类型，必须由固定 `ScriptHostGAgent` 承载执行。
3. 脚本写侧必须遵循 `Application Command -> Requested Event(EventEnvelope) -> Domain Event -> Apply -> State`，不得旁路 Event Sourcing。
4. 脚本读侧必须接入统一 Projection Pipeline，CQRS 与 AGUI 不允许双轨。
5. `ScriptHostGAgent` 主干继承必须是 `GAgentBase<ScriptHostState>`。
6. `RoleGAgent/AIGAgentBase` 能力复用必须采用组合与子 Actor 委托，不得作为脚本主干继承链。

## 3. 重构目标
1. 支持用 C# 脚本定义 GAgent 行为，覆盖静态类核心能力面（请求事件处理、状态迁移、读模型投影）。
2. 保持 Event Sourcing 语义严格一致，可回放、可审计、可重建。
3. 支持脚本自定义 State 与 ReadModel 结构，同时不破坏现有分层和依赖反转约束。
4. 保持 Actor 化运行态治理，禁止中间层进程内事实状态映射。
5. 全量需求可门禁验证（build/test/architecture guards/route mapping guard）。

## 4. 范围与非范围
范围：
1. C# 脚本 GAgent 的定义契约、加载编译、执行宿主、状态与投影规范。
2. 与现有 Event Sourcing、Projection、Workflow/Host 装配的集成约束。
3. 安全沙箱、可观测性、版本治理、迁移策略需求。

非范围：
1. 外部业务 DSL 设计（仅定义 C# 脚本契约，不定义新 DSL）。
2. 任意 CLR API 全开放执行（脚本必须受限）。
3. 替换现有静态 GAgent 或 YAML Workflow 的存量能力。

## 5. 架构硬约束（必须满足）
1. 必须保留单一业务主链路，禁止并行第二系统。
2. 必须严格读写分离：写侧仅事件事实源，读侧仅 ReadModel 查询源。
3. 必须强制 Event Sourcing：脚本状态恢复仅允许 replay。
4. 必须复用统一 Projection Pipeline，AGUI 与 CQRS 共用输入流。
5. 必须 Actor 化运行态：会话/订阅/关联/超时在 Actor 或分布式状态中承载。
6. 禁止中间层维护 `actor/entity/run/session` ID 到上下文事实映射字典。
7. 禁止 `TypeUrl.Contains(...)` 路由；必须使用精确 TypeUrl 键路由。
8. 禁止脚本回调线程直接改运行态；定时/重试必须事件化回到 Actor 主线程推进。
9. 禁止脚本绕过框架直接写 EventStore/ReadModelStore。
10. Host 层仅做装配与协议，禁止承载脚本业务编排。
11. `ScriptHostGAgent` 必须直接继承 `GAgentBase<ScriptHostState>`，不允许改为 `RoleGAgent` 或 `AIGAgentBase<TState>` 继承链。
12. AI 能力复用必须通过 `IRoleAgent` 子 Actor 或 `IAICapability` 端口组合，不允许以继承方式耦合脚本主干。

## 6. 当前基线（代码事实）
1. 有状态 GAgent 基线由 `GAgentBase<TState>` 提供，已强制 Event Sourcing 生命周期。
证据: `src/Aevatar.Foundation.Core/GAgentBase.TState.cs`
2. Event Sourcing 行为与工厂已具备统一实现。
证据: `src/Aevatar.Foundation.Core/EventSourcing/EventSourcingBehavior.cs`、`src/Aevatar.Foundation.Core/EventSourcing/DefaultEventSourcingBehaviorFactory.cs`
3. Workflow GAgent 已实现“配置驱动 + 事件化状态迁移 + 模块装配”。
证据: `src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs`
4. 投影已实现统一入口与一对多 projector 分发。
证据: `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionCoordinator.cs`
5. reducer 路由精确匹配已有守卫脚本。
证据: `tools/ci/projection_route_mapping_guard.sh`
6. 架构门禁已约束 ES/路由/中间层状态反模式。
证据: `tools/ci/architecture_guards.sh`
7. `WorkflowGAgent` 继承 `GAgentBase<WorkflowState>`，并通过运行时创建 `RoleGAgent` 子 Actor 复用 AI 能力，不走继承复用。
证据: `src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs`
8. `RoleGAgent` 继承 `AIGAgentBase<RoleGAgentState>`，属于 AI 专用角色 agent。
证据: `src/Aevatar.AI.Core/RoleGAgent.cs`

## 7. 需求分解与状态矩阵
| ID | 需求 | 验收标准 | 当前状态 | 证据 | 差距 |
|---|---|---|---|---|---|
| R-SG-01 | 脚本定义模型 | 提供 `IScriptAgentDefinition` 契约，覆盖请求事件决策/事件处理/状态迁移/投影 reducer 声明 | Done | `src/Aevatar.Scripting.Abstractions/Definitions/*`、`src/Aevatar.Scripting.Core/Compilation/*` | 动态脚本 reducer 声明机制待扩展 |
| R-SG-02 | 执行宿主 | 固定 `ScriptHostGAgent` 承载脚本执行，不直接创建任意脚本 agent type | Done | `src/Aevatar.Scripting.Core/ScriptHostGAgent.cs` | 无 |
| R-SG-03 | 严格 ES | 脚本写侧仅输出领域事件，由宿主调用 `PersistDomainEvent(s)` 提交并 apply | Done | `ScriptHostGAgent`、`ScriptHostGAgentReplayContractTests`、`ScriptGAgentEndToEndTests` | 无 |
| R-SG-04 | 自定义 State | 支持脚本声明状态 schema 与默认值，宿主状态包含 `ScriptStatePayload + SchemaHash + Revision` | In Progress | `script_host_messages.proto` 的 `ScriptHostState` | schema 迁移策略未实现 |
| R-SG-05 | 自定义 ReadModel | 支持脚本 reducer 定义 read model 结构并接入统一 projector 路由 | Done | `Aevatar.Scripting.Projection/*` + `ScriptExecutionReadModelProjectorTests` | 跨存储 provider 装配待补 |
| R-SG-06 | 路由确定性 | reducer 路由必须 `TypeUrl` 精确键 + Ordinal 命中 | Done | `ScriptEventReducerBase` + `ScriptExecutionReadModelProjector` + `architecture_guards.sh` | 无 |
| R-SG-07 | Actor 运行态一致性 | 超时/重试/延迟仅能发布内部事件，不得回调改状态 | In Progress | `ScriptHostGAgent` 遵守事件处理主线程模型 | timeout/retry 内部事件模型未落地 |
| R-SG-08 | 安全沙箱 | 禁止 `Task.Run/Timer/Thread/lock/IO 直连/反射逃逸` 等危险能力 | In Progress | `ScriptSandboxPolicy` + `RoslynScriptAgentCompilerTests` | IO/反射白名单治理待补齐 |
| R-SG-09 | 可观测性 | 每次脚本执行包含 `script_id/revision/event_id/correlation_id` 日志与指标维度 | Planned | 本文档 | 观测埋点未实现 |
| R-SG-10 | 版本治理 | 支持脚本 revision 固化回放；升级默认“新实例生效”，在位升级需显式迁移 | In Progress | `RunScriptRequestedEvent.script_revision`、`ScriptDomainEventCommitted.script_revision` | revision 到编译句柄绑定未实现 |
| R-SG-11 | Host 装配 | Host 仅注册脚本 capability，不承载脚本业务逻辑 | Done | `Aevatar.Scripting.Hosting/*` + `ScriptCapabilityHostExtensionsTests` | 主程序默认接入策略待确认 |
| R-SG-12 | 验证门禁 | 新增测试与守卫后，`build/test/guards` 全部通过 | In Progress | `dotnet build aevatar.slnx`、脚本专项测试、`architecture_guards.sh` | 全量 `dotnet test aevatar.slnx` 尚未执行 |
| R-SG-13 | 继承边界 | `ScriptHostGAgent` 继承 `GAgentBase<ScriptHostState>`；不得继承 `RoleGAgent/AIGAgentBase` | Done | `script_inheritance_guard.sh` + `ScriptInheritanceGuardTests` | 无 |
| R-SG-14 | AI 复用方式 | AI 复用仅通过子 Actor/能力端口组合，不通过继承复用 | In Progress | `IAICapability`、`RoleAgentDelegateAICapability` | `IRoleAgentPort` 生产实现未完成 |

## 8. 差距详解
1. 缺脚本契约层: 当前缺“定义-编译-执行”的统一抽象，容易出现脚本直接耦合运行时实现。
2. 缺宿主状态壳: `GAgentBase<TState>` 需要静态 protobuf state 类型，尚无脚本可变 state 的标准承载方案。
3. 缺脚本投影链路: 现有 reducer 以编译期类型装配，尚无脚本 reducer 的可验证接入机制。
4. 缺脚本安全策略: 当前无系统化能力白名单，存在脚本逃逸风险。
5. 缺版本回放治理: 当前无 `script revision` 固化语义，回放一致性不可保证。
6. 缺 AI 复用边界实现: 当前尚无“脚本主干不继承 AI 基类”的自动化守卫与端口实现。

## 9. 目标架构
目标组件：
1. `ScriptAgentDefinitionRegistry`（Application/Infrastructure）:
负责脚本定义的注册、版本解析、校验与查找。
2. `ScriptAgentCompiler`（Infrastructure）:
将脚本编译为受限能力包（委托/表达式树/中间表示），输出可执行句柄。
3. `ScriptHostGAgent : GAgentBase<ScriptHostState>`（Domain）:
唯一脚本执行宿主，统一事件提交、状态 apply、内部事件对账。
4. `ScriptProjectionProjector`（Projection）:
将脚本声明的 reducer 按 TypeUrl 精确路由接入既有 ProjectionCoordinator。
5. `ScriptCapabilityHostExtensions`（Host）:
仅做 DI 装配与能力开关，不承载业务编排。
6. `ScriptAICapabilityAdapter`（Application/Infrastructure）:
以组合方式复用 `RoleGAgent` 或 AI runtime 能力，向脚本暴露稳定 AI 能力端口。

关键链路：
1. Application Command -> Adapter -> EventEnvelope(RunScriptRequestedEvent) -> ScriptHostGAgent -> Script Decide -> Domain Events -> PersistDomainEvents -> Apply -> State
2. EventEnvelope -> ProjectionCoordinator -> ScriptProjectionProjector + 其他 Projector -> ReadModel Store + AGUI Sink
3. ScriptHostGAgent -> RoleGAgent(or IAICapability) -> AI Events -> Unified Projection Pipeline

## 10. 重构工作包（WBS）
1. WP-01 契约与边界
目标: 定义脚本 agent 契约、状态壳、错误模型、版本模型。
产物: Abstractions 项目接口与 proto 状态壳。
DoD: 契约评审通过，覆盖正反约束测试。

2. WP-02 编译与安全沙箱
目标: 实现脚本编译和能力白名单。
产物: 编译器、校验器、禁用 API 规则集。
DoD: 禁用场景测试通过，允许场景可执行。

3. WP-03 宿主 Actor 执行链路
目标: 落地 `ScriptHostGAgent` 写侧主链。
产物: 宿主 actor、内部事件模型、回放一致性测试。
DoD: replay contract 测试通过。

4. WP-04 读侧投影集成
目标: 脚本 reducer 接入统一 pipeline。
产物: projector、路由构建器、读模型映射器。
DoD: route mapping 守卫与投影集成测试通过。

5. WP-05 Host 装配与文档门禁
目标: 提供 host extension 与完整验证矩阵。
产物: DI 扩展、运行手册、CI 守卫补充。
DoD: build/test/guards 全绿。

6. WP-06 AI 能力组合复用
目标: 通过组合端口复用 RoleGAgent/AIGAgent 能力，不引入继承耦合。
产物: `IAICapability` 接口、`RoleAgentDelegate` 适配器、继承边界守卫测试。
DoD: 继承边界测试通过，AI 组合复用集成测试通过。

## 11. 里程碑与依赖
1. M1（契约冻结）: 完成 WP-01，冻结脚本能力面和状态模型。
2. M2（写侧可用）: 完成 WP-02 + WP-03，脚本写侧可回放可审计。
3. M3（读侧闭环）: 完成 WP-04，读模型与 AGUI 同链路可用。
4. M4（能力复用边界闭环）: 完成 WP-06，确认 AI 复用走组合而非继承。
5. M5（生产可治理）: 完成 WP-05，门禁与文档闭环。

依赖关系: M1 -> M2 -> M3 -> M4 -> M5。

## 12. 验证矩阵（需求 -> 命令 -> 通过标准）
| 需求ID | 验证命令 | 通过标准 |
|---|---|---|
| R-SG-01~R-SG-03 | `dotnet test test/Aevatar.Foundation.Core.Tests/Aevatar.Foundation.Core.Tests.csproj --nologo` | 契约/ES/回放相关测试通过 |
| R-SG-04~R-SG-07 | `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --nologo` | 脚本状态、内部事件对账、Actor 约束测试通过 |
| R-SG-05~R-SG-06 | `bash tools/ci/projection_route_mapping_guard.sh` | 脚本投影路由满足 TypeUrl 精确映射 |
| R-SG-03~R-SG-12 | `bash tools/ci/architecture_guards.sh` | 无 ES 回退、无中间层事实态映射、无字符串路由 |
| R-SG-13 | `rg -n "class\\s+ScriptHostGAgent\\s*:\\s*GAgentBase<ScriptHostState>" src` | 精确命中 `ScriptHostGAgent` 主继承链 |
| R-SG-13 | `rg -n "class\\s+ScriptHostGAgent\\s*:\\s*(RoleGAgent|AIGAgentBase<)" src` | 命中结果为空 |
| R-SG-14 | `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --filter "*Script*AI*Delegate*" --nologo` | 组合复用路径测试通过 |
| R-SG-12 | `dotnet build aevatar.slnx --nologo` | 全量编译通过 |
| R-SG-12 | `dotnet test aevatar.slnx --nologo` | 全量测试通过 |

## 13. 完成定义（Final DoD）
1. 脚本 GAgent 能力与静态能力在定义层面等价，且不破坏现有主链。
2. 所有写侧状态变更均可追溯到领域事件并可 replay 重建。
3. 所有读侧输出均来自统一 Projection Pipeline。
4. Actor 运行态约束得到代码与测试双重保证。
5. AI 能力复用路径仅为组合/委托，不存在脚本主干继承 AI 基类。
6. 文档、门禁、测试、代码一致且可复核。

## 14. 风险与应对
1. 风险: 脚本能力过宽导致沙箱逃逸。
应对: 编译期能力白名单 + 运行期双重校验 + 违规即 fail-fast。
2. 风险: revision 漂移导致回放不一致。
应对: 事件持久化写入 revision，回放时按 revision 绑定脚本快照。
3. 风险: 动态 read model 影响查询稳定性。
应对: schema 版本化与兼容检查，发布前启动期 fail-fast 验证。
4. 风险: 新增分支导致双轨实现。
应对: 代码审查 + 守卫脚本明确禁止第二投影链路与旁路写入。
5. 风险: 通过继承复用 AI 造成脚本主干语义漂移与依赖反转破坏。
应对: 继承边界守卫 + 组合端口规范 + 集成测试强约束。

## 15. 执行清单（可勾选）
- [ ] 完成脚本契约定义与评审（WP-01）
- [ ] 完成脚本编译器与沙箱策略（WP-02）
- [ ] 完成 `ScriptHostGAgent` 写侧链路（WP-03）
- [ ] 完成脚本 projection 集成（WP-04）
- [ ] 补齐测试、门禁、文档与 Host 装配（WP-05）
- [ ] 完成 AI 组合复用与继承边界守卫（WP-06）

## 16. 当前执行快照（2026-03-01）
1. 已完成:
脚本项目骨架、`ScriptHostGAgent` 写侧、沙箱编译器、投影链路、Host 装配、继承守卫与端到端测试已落地。
2. 部分完成:
状态 schema 迁移、AI 组合复用生产实现、观测埋点与版本治理策略仍在进行中。
3. 阻塞项:
无硬阻塞。

## 17. 变更纪律
1. 任一需求状态变化必须同步更新本文件第 7、15、16 节。
2. 任何架构规则新增必须同步补门禁脚本与测试。
3. 若实现与本文档冲突，以“单主干、严格 ES、统一投影、Actor 事实源”原则优先，文档需同日更新。
4. 删除优于兼容；无业务价值的过渡层不得保留空壳。
5. 涉及 AI 复用变更时，必须先证明“组合可行”，不得以继承捷径破坏主干边界。
