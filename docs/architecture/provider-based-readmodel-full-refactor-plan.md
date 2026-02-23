# Provider-Based ReadModel 全量重构计划（彻底版）

## 1. 文档元信息
- 状态：In Progress
- 目标：覆盖 `docs/architecture/generic-event-sourcing-elasticsearch-readmodel-requirements.md` 的全部要求（含 v1 + vNext）
- 适用仓库：`aevatar`
- 编写日期：2026-02-23
- 最近更新：2026-02-23
- 备注：本计划按“可破坏式重构”制定，不以兼容历史实现为约束

## 1.1 当前进展快照（2026-02-23）
- 已完成：W0（Provider 纠偏清理，Workflow 不再持有 Provider Store 实现）。
- 部分完成：W1（能力模型/选择器/校验器已落地，统一 Registry 与结构化观测仍待补齐）。
- 部分完成：W2（通用 Elasticsearch Provider 项目与 DI 注册已落地，异常分级与 schema/mapping 策略仍待增强）。
- 部分完成：W6（Workflow 已接入通用 Provider 注册与选择链路，其它业务域未迁移）。
- 未开始：W3/W4/W5。

## 2. 执行原则（硬约束）
- 严格分层：`Domain / Application / Infrastructure / Host`。
- Provider 必须在通用 CQRS 基建层，不得绑定具体业务域（例如 Workflow）。
- CQRS 与 AGUI 继续走单一 Projection Pipeline，禁止平行链路。
- 中间层禁止进程内 ID->事实态映射（遵守现有 guard）。
- 能力不匹配默认启动期 fail-fast。
- 变更必须满足现有 CI 门禁与新增测试门槛。

## 3. 当前关键问题（必须先修）
1. `Elasticsearch` 适配器被放在 `Aevatar.Workflow.Projection`，违反“通用能力下沉”原则。（已修复）
2. Provider 能力模型虽已引入，但尚未形成“跨业务域可复用”的统一 Registry/路由/校验治理框架。（进行中）
3. Graph Provider 尚未落地，StateMirror/StateOnly 通用能力未完成。
4. Event Sourcing 自动 Persisted Event 仍未进入统一管道。

## 4. 目标架构（完成态）

### 4.1 项目结构（目标）
- `src/Aevatar.CQRS.Projection.Abstractions`
  - 保留并扩展通用契约：`IProjectionReadModelStore<,>`、Provider 能力模型、ReadModel 需求模型。
- `src/Aevatar.CQRS.Projection.Providers`
  - 新建通用 Provider 运行时：注册、选择、能力校验、错误模型、可观测性。
- `src/Aevatar.CQRS.Projection.Providers.Elasticsearch`
  - Document Index Provider，通用实现，不含 Workflow 业务对象。
- `src/Aevatar.CQRS.Projection.Providers.Neo4j`
  - Graph Index Provider，通用实现，不含 Workflow 业务对象。
- `src/Aevatar.CQRS.Projection.StateMirror`
  - 通用 `State -> DefaultReadModel` 镜像能力（可选启用）。
- `src/workflow/Aevatar.Workflow.Projection`
  - 仅保留 Workflow 读模型、reducer/projector、port，不含后端 SDK/Provider 实现。

### 4.2 运行路径（目标）
1. 业务模块声明 ReadModel 与需求（IndexKind/Schema/Alias）。
2. Host 装配通用 Provider Runtime。
3. Provider Runtime 在启动期执行：
   - ReadModel 绑定解析
   - Provider 选择
   - 能力校验
   - 失败即 fail-fast（默认）
4. Projection 写入通过统一 Store 契约执行，业务层无后端耦合。

## 5. 全量重构范围（对应需求文档）

### 5.1 v1 范围（必须完成）
- Provider 能力模型、启动期校验、Document Provider 落地。
- Workflow 切换 Provider 且 Query 语义不变。
- 可观测性字段落地（provider/readModelType/key/elapsedMs/result/errorType）。
- 配置模型落地与环境变量覆盖。

### 5.2 vNext 范围（本轮也纳入计划并执行）
- Graph Provider（Neo4j-like）完整落地。
- 通用 `StateOnly / DefaultReadModel / CustomReadModel` 模式。
- 通用 StateMirror 能力。
- Event Sourcing 自动 Persisted Event 管道（开关化 + 默认策略定义）。

## 6. 详细实施计划（Workstreams）

### W0 纠偏清理（先决）
目标：消除“Provider 绑定业务域”问题。
状态：Completed（2026-02-23）

任务：
1. 从 `Aevatar.Workflow.Projection` 移除 `Elasticsearch` 实现与配置细节。
2. 在 Workflow 层保留 `IProjectionReadModelStore<WorkflowExecutionReport, string>` 注入点，不感知具体后端。
3. 删除/迁移 Workflow 内 Provider 专属类到通用 Provider 项目。

交付：
- Workflow 项目不再引用 Elasticsearch 相关 SDK/实现。
- 相关 Workflow 测试已切换为通用 Provider Store 类型；Provider 专项测试集待补齐。

### W1 通用 Provider Runtime 基建
目标：建立跨业务可复用的 Provider 选择与能力校验内核。
状态：In Progress

任务：
1. 设计并实现：
   - `IProjectionReadModelProviderRegistry`
   - `IProjectionReadModelProviderSelector`
   - `IProjectionReadModelCapabilityValidator`（现有静态工具升级为可注入策略）
   - `ProjectionReadModelBindingResolver`
2. 引入标准错误模型：
   - `readModel`
   - `provider`
   - `requiredCapabilities`
   - `actualCapabilities`
   - `violations`
3. 统一日志接口与事件 ID，支持结构化查询。

交付：
- 任意业务模块可通过统一 API 注册 ReadModel 需求并由运行时自动选 Provider。

### W2 Document Provider（Elasticsearch）
目标：通用 Document Index Provider 完成生产可用版本。
状态：In Progress

任务：
1. 新建 `Aevatar.CQRS.Projection.Providers.Elasticsearch`。
2. 提供通用 Store 适配机制：
   - 支持 `Upsert/Mutate/Get/List`
   - `ListAsync` 强上限
   - 索引前缀隔离
   - 自动建索引策略
3. 引入 mapping/settings/alias 处理策略（与能力模型一致）。
4. 完善异常分类（连接失败、索引不存在、版本冲突、认证失败）。

交付：
- 不依赖 Workflow 类型的通用 ES Provider。

### W3 Graph Provider（Neo4j）
目标：完成 Graph Index Provider 能力闭环。
状态：Not Started

任务：
1. 新建 `Aevatar.CQRS.Projection.Providers.Neo4j`。
2. 支持节点/关系写入与基础查询。
3. 支持唯一约束与索引初始化策略。
4. 将 Graph 能力纳入同一 Provider runtime 校验。

交付：
- `IndexKind.Graph` 可被真实 Provider 承接。

### W4 StateMirror / ReadModel 可选模式
目标：完成 `StateOnly / DefaultReadModel / CustomReadModel` 三模式。
状态：Not Started

任务：
1. 新建 `Aevatar.CQRS.Projection.StateMirror`：
   - 默认字段映射
   - 可配置忽略/重命名
   - 可插拔 projector 覆盖
2. `StateOnly` 模式定义：
   - 不创建 ReadModel
   - 查询端点返回统一能力不可用错误模型
3. `CustomReadModel` 与默认镜像并存规则、优先级规则落地。

交付：
- 框架层提供无业务耦合的默认读模型能力。

### W5 Event Sourcing 自动 Persisted Event 管道
目标：把“手动 Raise/Confirm”为主的模式升级为可配置自动化。
状态：Not Started

任务：
1. 设计统一写侧提交管道：
   - 变更检测
   - 事件生成（Snapshot/Delta）
   - 版本推进与幂等控制
2. 提供开关：
   - 全局开关
   - 模块级覆盖
3. 默认策略：
   - 默认保持兼容行为或由本次重构统一切换（按最终决策）

交付：
- 业务方无需手动拼装 persisted event 基础流程。

### W6 Workflow 与其他模块接入
目标：业务域从“自带 Provider”转为“消费通用 Provider Runtime”。
状态：In Progress

任务：
1. Workflow 完整迁移到通用 runtime。
2. 逐步接入 AI/Foundation/其他读侧模块（如存在）。
3. 清理重复抽象、空转发层、历史兼容分支。

交付：
- 业务域仅保留领域语义与投影逻辑，不含后端实现细节。

## 7. 配置模型重构计划

### 7.1 目标配置（通用）
- `Projection:ReadModel:Provider`
- `Projection:ReadModel:FailOnUnsupportedCapabilities`
- `Projection:ReadModel:Bindings`
- `Projection:ReadModel:Providers:Elasticsearch:*`
- `Projection:ReadModel:Providers:Neo4j:*`
- `Projection:ReadModel:Mode`（`StateOnly/DefaultReadModel/CustomReadModel`）

### 7.2 迁移策略（本次不保兼容）
- 删除业务域私有 Provider 配置键（如 `WorkflowExecutionProjection:Providers:*`）。
- 全量切换到统一 `Projection:ReadModel:*`。

## 8. 测试与门禁计划

### 8.1 单元测试
- Provider 能力匹配：成功/失败/冲突/歧义。
- Provider 选择器：单候选自动路由、多候选强制显式绑定。
- ReadModel 三模式行为测试。
- 自动 Persisted Event 管道（变更检测、版本、幂等）。

### 8.2 集成测试
- Elasticsearch 端到端写读（Docker）。
- Neo4j 端到端写读（Docker）。
- Workflow Provider 切换后 Query 合同一致性。

### 8.3 分布式测试
- 保留并扩展 3 节点一致性链路。
- 新增 Provider 后验证跨节点一致收敛。

### 8.4 强制门禁
- `dotnet build aevatar.slnx --nologo`
- `dotnet test aevatar.slnx --nologo`
- `bash tools/ci/architecture_guards.sh`
- `bash tools/ci/projection_route_mapping_guard.sh`
- `bash tools/ci/test_stability_guards.sh`
- `bash tools/ci/solution_split_test_guards.sh`

## 9. 里程碑与交付

### M1（纠偏 + 基建）
- 完成 W0 + W1。
- 验收：Workflow 不再持有任何 Provider 实现。
- 当前状态：W0 完成；W1 部分完成。

### M2（Document Provider）
- 完成 W2。
- 验收：ES Provider 在通用层可被 Workflow/其他模块消费。
- 当前状态：已可被 Workflow 消费，增强项进行中。

### M3（Graph Provider）
- 完成 W3。
- 验收：Graph ReadModel 可真实写读，能力校验全链路可用。
- 当前状态：未开始。

### M4（StateMirror + 模式化 + ES 自动化）
- 完成 W4 + W5。
- 验收：ReadModel 三模式与自动 Persisted Event 能力全量可测。
- 当前状态：未开始。

### M5（全域收口）
- 完成 W6 + 全量文档/测试/门禁收口。
- 验收：需求文档条目全部闭环，无历史空壳实现。
- 当前状态：Workflow 已接入，其它域待迁移。

## 10. 风险与应对
- 风险：Provider Runtime 泛化过度导致复杂度爆炸。  
  应对：先冻结最小能力集合，按里程碑增量扩展。
- 风险：Graph 与 Document 语义差异大，统一抽象失真。  
  应对：抽象仅覆盖公共最小集，复杂能力走 provider-specific extension。
- 风险：重构期间回归面大。  
  应对：分阶段门禁 + 契约测试 + 分片测试强制执行。
- 风险：自动 Persisted Event 改写写侧行为。  
  应对：先实验开关，明确默认策略后再全量切换。

## 11. 完成定义（Final DoD）
- 需求文档中 FR/NFR 全量有代码落地。
- Provider 不再出现在业务域实现层。
- Document + Graph Provider 均可被统一 runtime 装配并通过能力校验。
- StateOnly/Default/Custom 三模式完整可用。
- 自动 Persisted Event 管道可用并有完整测试。
- 全量 CI 门禁通过，文档同步完成。
