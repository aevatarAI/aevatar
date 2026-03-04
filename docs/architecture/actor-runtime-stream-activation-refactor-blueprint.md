# GAgent 配置与激活索引彻底重构蓝图（Zero-Manifest，不兼容）

## 1. 文档元信息
- 状态：`Final`
- 版本：`v7`
- 日期：`2026-03-05`
- 决策级别：`Architecture Breaking Change`
- 适用范围：
  - `Aevatar.Foundation.Abstractions`
  - `Aevatar.Foundation.Core`
  - `Aevatar.Foundation.Runtime.Implementations.Local`
  - `Aevatar.Foundation.Runtime.Implementations.Orleans`
  - `Aevatar.Workflow.*`
  - `Aevatar.Bootstrap* / Aevatar.Configuration`

## 2. 问题定义
现有模型把多类语义混在 `Manifest` 中：
1. 运行时激活索引（`actorId -> agentType`）。
2. 模块恢复（`ModuleNames`）。
3. 配置快照（`ConfigJson`）。
4. 业务绑定（`Metadata`，如 workflowName）。

问题：
1. 分层污染：Local 需求上浮到框架契约。
2. 语义混杂：类型、配置、业务绑定耦合在同一存储对象。
3. 可重放风险：配置读取与业务状态更新路径不一致。
4. 基类职责不清：`GAgentBase<TState,TConfig>.Config` 的运行态语义、持久化边界、合并时机没有被统一定义。

## 3. 最终架构决策（强制）
1. **框架层零 Manifest**：`Abstractions/Core` 不再定义任何 Manifest 模型与存储契约。
2. **Class 默认配置来源**：仅来自 `Host/Infrastructure` 的 `.NET Configuration + Options` 装配结果。
3. **Instance 覆盖配置与业务绑定**：统一进入 `State/Event`，不走旁路存储。
4. **Local 惰性激活索引**：`actorId -> agentType` 仅作为 Local 实现内部细节，不上升框架。
5. **不存在 Class Manifest**：一个 `GAgent class` 不对应任何框架级 Manifest，也不引入“Class Manifest Actor”。
6. **默认模块不进框架**：`DefaultModules` 不作为 Foundation 通用语义；workflow 需要的模块由 workflow 自身定义与持久化。
7. **激活语义遵循 Actor 模型**：由 stream/grain 激活；“恢复”是实现层加载 state，不是框架 Manifest 恢复。
8. **Class 默认配置必须集群化管理**：存在统一权威配置源，跨节点共享同一 class 配置版本。
9. **Class 默认配置必须支持无重启更新**：配置发布后，运行中节点通过订阅/热加载生效，不依赖进程重启。
10. **`GAgentBase<TState,TConfig>` 不做配置独立持久化**：`TConfig` 仅为运行态 `effective config`，事实源来自 `class defaults + state/event overrides`。
11. **State 事实源仍强依赖 EventSourcing**：配置覆盖属于 state 语义，必须通过 `Command -> Event -> Apply -> Replay` 恢复。
12. **基类统一配置合并职责**：`GAgentBase<TState,TConfig>` 负责配置重算主流程，子类只提供 override 提取与字段级 merge 策略。
13. **protobuf 限制下采用 hook/adapter**：`TState : IMessage` 不要求继承配置基类；禁止通过“让 protobuf state 继承框架基类”实现配置合并。
14. **配置热更新遵循 Actor 主线程**：回调线程仅发信号，配置重算必须在 Actor 事件入口或等价主线程上下文执行。
15. **Class 默认配置的集群控制面属于 Infrastructure**：可用 Actor 实现，但不进入 Foundation 框架契约。
16. 不做兼容适配，不保留双写，不保留旧字段。

## 4. 目标模型

### 4.1 Class 默认配置（Host 语义）
Class 默认配置不属于框架契约，属于宿主装配输入。

来源：
1. `appsettings.*`
2. environment variables
3. distributed config center（权威源，集群共享）

输出：
1. 强类型 `Options`（按 agent class 分组）。
2. 注入到运行时创建/激活路径的默认配置对象。
3. `ClassDefaultsVersion`（递增版本或不可变 hash）。

约束：
1. `Core/Abstractions` 不引用 `IConfiguration/IOptions*`。
2. 事件处理热路径不得动态访问远程配置中心。
3. 集群共享 class 配置由基础设施配置中心保证，不通过框架内 Manifest/Actor 分发。
4. 运行中配置变更必须通过热加载链路生效，不要求重启 runtime/host。
5. 配置回调线程不得直接改 Actor 运行态；只能触发内部事件或在消息入口重算。

### 4.2 Instance 覆盖配置（Domain 语义）
实例级配置与绑定统一 state-event 化：
1. `InstanceConfigOverrides`
2. `WorkflowBinding`
3. 业务运行态字段

约束：
1. `Command -> Event -> Apply` 唯一路径。
2. 不允许直接修改配置中心来覆盖单实例行为。
3. 配置覆盖变更必须可由 EventSourcing 回放恢复。

### 4.3 Local 激活索引（实现语义）
Local 为支持按 ID 惰性物化，内部维护索引：
1. 数据：`actorId -> agentType`
2. 位置：`Aevatar.Foundation.Runtime.Implementations.Local` 内部
3. 生命周期：`Create` 写入，`Destroy` 删除，`Get/Exists` 读取

约束：
1. 非事实源不得上浮到框架层。
2. Orleans 路径不依赖该索引。

### 4.4 模块装配语义（Workflow 优先）
1. Foundation 只保留通用扩展点：`IEventModule`、`IEventModuleFactory`、`SetModules(...)`。
2. Workflow 负责解析“需要哪些模块”（来自 workflow steps/roles）。
3. 模块选择结果进入 Workflow state/event，不再走 Foundation 级持久化。
4. 框架不维护 `DefaultModules` 概念；如需默认值，在 workflow 定义或 workflow 自身配置中表达。

### 4.5 Class 默认配置集群化与热更新模型
控制面（Infrastructure）：
1. 统一配置源按 `AgentClass + Version` 持久化 class defaults。
2. 变更发布采用版本化发布（禁止覆盖式无版本写入）。
3. 发布后向各 runtime 节点发送配置版本变更通知（事件总线/流/控制面 Actor 均可）。

数据面（Runtime）：
1. 节点通过 `.NET Configuration Provider + IOptionsMonitor` 接收更新并刷新本地快照。
2. 节点维护 `agentClass -> latestDefaults(versioned)` 的只读缓存。
3. 实例在消息处理入口比较 `AppliedClassDefaultsVersion` 与 `latest version`，不一致则在 Actor 主线程重算 `effective config` 并更新已应用版本。

一致性语义：
1. 集群最终一致：新版本在发布后逐节点收敛。
2. 单实例单调一致：同一实例只前进到更高版本，不回退。
3. 无重启生效：已激活实例与新激活实例都可在运行中使用新 class defaults。

### 4.6 `GAgentBase<TState, TConfig>` 统一合并模型
定位：
1. `TConfig` 是运行态 `effective config` 缓存，不是持久化事实源。
2. 持久化事实只在 `TState`（经 EventSourcing）与 class defaults（Host/Infrastructure）两端。
3. `GAgentBase<TState,TConfig>.Config` 仅表示“当前已生效配置快照”，可被重算覆盖，不可作为恢复输入。

合并公式：
1. `effective_config = Merge(class_defaults, state_overrides)`
2. `state_overrides` 从 `TState` 提取，不从旁路存储读取。

触发时机：
1. Activate：`Replay state` 完成后执行一次合并。
2. State 变化：`OnStateChanged` 路径执行合并。
3. Class defaults 热更新：版本前进时执行合并。
4. 基类显式重算入口触发：如 `RecomputeEffectiveConfigAsync(...)`。

基类职责边界（强制）：
1. 基类负责“读 class defaults -> 提取 state overrides -> merge -> 更新 Config -> 回调 OnConfigChanged”完整链路。
2. 子类只负责两个可变点：`ExtractStateOverrides(TState)` 与 `MergeConfig(classDefaults, overrides)`。
3. 基类不得直接持久化 `TConfig`，不得写 `ConfigJson` 或等价快照字段。
4. `ConfigureAsync(config)` 若保留，仅作为“命令入口封装”；真实持久化仍必须走 `PersistDomainEvent -> Apply(State) -> Recompute`。

抽象策略（应对 protobuf 无继承）：
1. `TState : IMessage` 不要求继承配置基类。
2. 基类提供统一流程；子类通过 hook/adapter 提供“从 `TState` 提取 overrides”的实现。
3. 如需复用，可在 Core 定义 `IStateConfigAdapter<TState,TConfig>`，由基类调用；不把该抽象上推到 Abstractions。
4. 禁止在子类散落重复 merge 主流程。

### 4.7 配置持久化与 EventSourcing 对齐模型
规则：
1. `class defaults`：外部权威配置源（Infrastructure）持久化。
2. `instance overrides`：仅在 `TState`（由 EventSourcing 回放恢复）持久化。
3. `effective config`：运行态计算结果，不落独立存储，不进 event log。

状态建议字段：
1. `InstanceConfigOverrides`
2. `AppliedClassDefaultsVersion`

恢复流程：
1. EventSourcing 回放得到 `TState`。
2. 读取当前 class defaults（按 agent class + latest version）。
3. 基类 merge 生成 `Config`。
4. 若 `AppliedClassDefaultsVersion` 落后，进入一次 state/event 化对账更新。

### 4.8 配置更新运行时语义（无重启）
1. 配置中心发布新版本后，节点通过 provider reload 获得最新 class defaults。
2. 回调线程只记录“版本变更信号”，不直接改 `Config/State`。
3. 实例在下一次消息入口检测到版本前进，在 Actor 主线程执行重算。
4. 重算后更新 `Config`，并通过事件更新 `AppliedClassDefaultsVersion`（如需要持久化对账）。
5. 不要求 run 级独立配置文件，不引入 run 级配置存储。

## 5. 分层职责重划

### 5.1 Aevatar.Foundation.Abstractions
删除：
1. `AgentManifest`
2. `IAgentManifestStore`

保留：
1. Actor/Agent/Runtime 基础契约
2. 事件与模块抽象（无 workflow 专有语义）

禁止：
1. 引入任何 Manifest 等价抽象
2. 引入 Local 激活索引接口

### 5.2 Aevatar.Foundation.Core
删除：
1. `GAgentBase.ManifestStore` 注入点
2. `GAgentBase` 模块恢复/持久化到 Manifest 逻辑
3. `GAgentBase<TState, TConfig>` 的 `ConfigJson` 持久化逻辑
4. `GAgentBase` 上承载 workflow 语义的 `DefaultModules`（若存在）

新增：
1. 激活时接收“宿主注入的 class 默认配置”
2. 在 `GAgentBase<TState, TConfig>` 内统一 `class defaults + state_overrides -> effective config`
3. 子类 hook/adapter：从 `TState` 提取 overrides
4. `GAgentBase<TState, TConfig>.Config` 定义为运行态快照，不参与独立持久化
5. 将 `AppliedClassDefaultsVersion` 纳入 state/event 对账语义（按需要落地）

禁止：
1. 直接引用 `IConfiguration` / `IOptions*`
2. 为 `TConfig` 引入独立持久化通道

### 5.3 Aevatar.Foundation.Runtime.Implementations.Local
新增（internal）：
1. `ILocalActivationIndexStore`
2. `LocalActivationIndexRecord { ActorId, AgentTypeName }`

行为：
1. `Create` 写索引
2. `Get/Exists` 惰性解析并物化
3. `Destroy` 删索引

### 5.4 Aevatar.Foundation.Runtime.Implementations.Orleans
删除：
1. 对旧 Manifest 存储依赖

行为：
1. 保持 grain 激活语义
2. 类型事实优先来自运行时 grain 状态/初始化信息

### 5.5 Aevatar.Workflow.*
删除：
1. workflow 绑定写入通用 Manifest 路径

改为：
1. workflow 绑定与模块需求进入 workflow state/event
2. 查询走 read model/state

### 5.6 Host / Infrastructure
职责：
1. 聚合 `.NET Configuration` 多源
2. 绑定强类型 class defaults
3. 在 runtime 装配点注入 class defaults
4. 接入集群配置中心并处理版本化、发布、缓存失效
5. 将配置更新推送到各节点（provider reload + cluster notification）

说明：
1. 推荐：通过基础设施配置控制面 Actor（或等价分布式服务）承载发布审计、灰度与回滚
2. 该 Actor 不是框架契约，不进入 `Abstractions/Core`
3. 不在框架层定义“配置分发协议”或“Class Manifest 协议”
4. class 级配置可以使用集中配置服务或 Actor 持久态实现，但都属于 Infrastructure 选型

## 6. 生命周期语义

### 6.1 Create
1. runtime 接收 `agentType`
2. 获取宿主装配的 class defaults
3. 初始化实例 state（含 overrides 容器）
4. Local 额外写 activation index

### 6.2 Activate
1. 由 stream/grain 激活实例（Local 用索引定位类型，Orleans 用 grain identity）
2. EventSourcing 回放实例 state（实现层行为，不依赖框架 Manifest）
3. 基类统一合并 `class defaults + state_overrides`
4. 生成 `effective config`
5. 如检测到 `AppliedClassDefaultsVersion` 落后，进入对账更新
6. 进入事件处理主循环

### 6.3 Reconfigure
1. 仅通过命令事件更新 instance overrides
2. `TConfig` 变更先入 event，再由 `OnStateChanged` 触发基类重算
3. 不写外部旁路配置存储

### 6.4 Class Defaults Hot Reload（无重启）
1. 配置中心发布新 `ClassDefaultsVersion`
2. 各节点收到变更通知并刷新本地 `Options` 快照
3. 实例在 Actor 主线程检测到 class defaults 版本前进（不是回调线程直接改态）
4. 重新合并 `class defaults + instance overrides`，更新 `effective config`
5. 后续事件按新配置处理（不中断进程、不重启节点）

### 6.5 Destroy
1. 删除实例状态/事件流（按 provider 语义）
2. Local 删除 activation index

## 7. 不兼容变更清单
1. 移除 `IAgentManifestStore` 与 `AgentManifest`
2. 移除 `ConfigJson/ModuleNames/Metadata` 旧路径
3. 移除所有 Foundation/Core/Workflow 对 Manifest 的读写
4. 移除基于 Manifest 的类型回退与绑定回退逻辑
5. 测试按新语义重写，不保留旧断言

## 8. 实施工作包（WBS）

### WP1：抽象层清理
1. 删除 Manifest 相关契约与模型
2. 修复编译与调用链

### WP2：Core 去 Manifest 化
1. 删除 GAgentBase Manifest 读写路径
2. 建立 `GAgentBase<TState, TConfig>` 统一合并点（输入为 `class defaults + state_overrides`）
3. 提供 `TState` 覆盖提取 hook/adapter（protobuf 组合模型）
4. 删除 `TConfig` 独立持久化路径
5. 约束 `Config` 字段为运行态快照，不能参与恢复输入
6. `Configure` 路径改造为事件化写 state，而非直接写持久化配置

### WP3：Local 内部索引化
1. 实现 internal activation index store
2. 改造 `Create/Get/Exists/Destroy` 语义
3. 并发幂等测试覆盖

### WP4：Workflow 状态化
1. workflow 绑定迁移到 state/event
2. 模块需求选择迁移到 workflow state/event

### WP5：Host 配置装配
1. 完成 `IConfiguration -> Options -> class defaults` 装配
2. 接入集群配置中心并实现版本化发布（`AgentClass + Version`）
3. 实现节点热加载链路（provider reload + cluster notification）
4. 运行中实例无重启生效（版本检测与主线程重算）
5. 明确配置控制面归属 Infrastructure，禁止回流为框架级 Class Manifest

### WP6：门禁与文档
1. 守卫：禁止 Core/Workflow 残留 Manifest 调用
2. 守卫：禁止 Core/Abstractions 依赖 `Microsoft.Extensions.Configuration*`
3. 守卫：禁止 Actor 事件处理路径直接读取远程配置
4. 守卫：禁止配置回调线程直接写 Actor 运行态（必须事件化/入口重算）
5. 守卫：禁止新增 `ConfigJson`/`IAgentManifestStore` 等旁路配置持久化
6. 更新相关架构文档

## 9. 验证矩阵
1. `dotnet build aevatar.slnx --nologo`
2. `dotnet test aevatar.slnx --nologo`
3. `bash tools/ci/architecture_guards.sh`
4. `bash tools/ci/solution_split_guards.sh`
5. `bash tools/ci/solution_split_test_guards.sh`
6. `bash tools/ci/test_stability_guards.sh`

通过标准：
1. 框架层无 Manifest 概念残留
2. Local 惰性激活仅依赖 Local 内部索引
3. Workflow 绑定与模块选择均 state-event 化
4. Core/Abstractions 不出现 `IConfiguration` 直接引用
5. 多节点配置生效路径一致（同输入下 effective config 一致）
6. Class 默认配置发布后可在不重启节点的情况下完成生效
7. 已激活实例可在运行中收敛到最新 `ClassDefaultsVersion`
8. `GAgentBase<TState, TConfig>` 不存在独立配置持久化逻辑
9. `TConfig` 可由 `Replay(TState) + class defaults` 确定性重建
10. protobuf state 不要求继承配置基类，依然可完成统一 merge 生命周期
11. Class 级配置热更新后，已激活实例可在 Actor 主线程收敛并完成版本对账

## 10. 非目标
1. 不提供旧 Manifest 数据迁移脚本
2. 不保留运行期兼容开关
3. 不引入新的“框架级 Class Manifest”替代物
4. 不在框架层引入 `DefaultModules` 语义
5. 不在框架层引入“Class 配置分发 Actor”标准实现
6. 不引入 run 级独立配置文件/配置存储模型

## 11. DoD
1. Framework（Abstractions/Core）实现 Zero-Manifest
2. 配置分层清晰：class defaults 在 Host，instance overrides 在 state/event
3. Local 激活索引彻底内聚在 Local 实现
4. Workflow 语义不再泄漏到 Foundation 通用层
5. `GAgentBase<TState,TConfig>` 完成统一 merge 主流程，子类只实现 hook/adapter
6. 文档、测试、门禁一致通过
