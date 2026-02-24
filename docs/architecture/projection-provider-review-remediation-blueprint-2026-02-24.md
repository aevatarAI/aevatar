# Projection Provider 评审问题重构蓝图（2026-02-24）

- 状态：Implemented
- 变更类型：Breaking Refactor（不考虑兼容性）
- 输入来源：本轮 code review 7 项问题（3 Blocking / 2 Major / 2 Minor）

## 1. 执行目标

1. 让 Provider 能力声明与真实实现严格一致，避免误导选择器与启动校验。
2. 为 Elasticsearch ReadModel 写入引入可验证的并发安全（OCC），消除 stale write 覆盖。
3. 清理 Host 组合层多 Provider 默认并存导致的选择歧义。
4. 对索引缺失、绑定冲突、排序稳定性、能力匹配语义做 fail-fast 与确定性收敛。
5. 通过测试与 CI 门禁固化上述规则，避免回归。

## 2. 问题清单（Review 输入映射）

| ID | Severity | 证据位置 | 问题摘要 |
|---|---|---|---|
| B1 | Blocking | `src/Aevatar.CQRS.Projection.Providers.Elasticsearch/DependencyInjection/ServiceCollectionExtensions.cs:26` + `src/Aevatar.CQRS.Projection.Providers.Elasticsearch/Stores/ElasticsearchProjectionReadModelStore.cs:353` | Elasticsearch 声明 `supportsAliases=true`、`supportsSchemaValidation=true`，但无对应实现。 |
| B2 | Blocking | `src/Aevatar.CQRS.Projection.Providers.Elasticsearch/Stores/ElasticsearchProjectionReadModelStore.cs:76` + `src/Aevatar.CQRS.Projection.Providers.Elasticsearch/Stores/ElasticsearchProjectionReadModelStore.cs:174` | `MutateAsync` 读改写 + 普通 PUT，缺少 OCC，重放/重试/并发存在覆盖风险。 |
| B3 | Blocking | `src/workflow/extensions/Aevatar.Workflow.Extensions.Hosting/WorkflowProjectionProviderServiceCollectionExtensions.cs:26` + `src/Aevatar.CQRS.Projection.Abstractions/Abstractions/ProjectionReadModelStoreSelector.cs:49` | 同一 ReadModel 同时注册多个 Provider，未显式指定时选择歧义。 |
| M1 | Major | `src/Aevatar.CQRS.Projection.Providers.Elasticsearch/Stores/ElasticsearchProjectionReadModelStore.cs:105` + `src/Aevatar.CQRS.Projection.Providers.Elasticsearch/Stores/ElasticsearchProjectionReadModelStore.cs:127` | `AutoCreateIndex=false` 且索引缺失时被当成无数据，掩盖配置错误。 |
| M2 | Major | `src/Aevatar.CQRS.Projection.Runtime/Runtime/ProjectionReadModelBindingResolver.cs:43` | 绑定解析 `Type.Name` 优先于 `FullName`，同名类型误绑定风险。 |
| N1 | Minor | `src/Aevatar.CQRS.Projection.Providers.Elasticsearch/Stores/ElasticsearchProjectionReadModelStore.cs:241` | `ListSortField` 为空时不排序，返回顺序不稳定。 |
| N2 | Minor | `src/Aevatar.CQRS.Projection.Abstractions/Abstractions/ProjectionReadModelCapabilityValidator.cs:24` | `RequiredIndexKinds` 使用 `Overlaps`（任一匹配），语义偏宽松。 |

## 3. 目标架构决策（To-Be）

### 3.1 B1 能力声明真实性（Capability Truthfulness）

1. 立即收敛为“声明即实现”：Elasticsearch ReadModel/Relation provider 的 `supportsAliases` 与 `supportsSchemaValidation` 统一改为 `false`。
2. 不在本轮引入“空能力声明 + 未来补实现”的过渡状态。
3. README 与运行日志输出同步能力矩阵，避免二义性。

设计原则：

- 能力字段仅表达已落地、可被测试验证的行为。
- Provider 选择与启动校验不允许依赖“计划能力”。

### 3.2 B2 写入并发安全（OCC）

1. Elasticsearch Provider 引入 `seq_no + primary_term` 的乐观并发控制。
2. `MutateAsync` 改为：`GET(_seq_no/_primary_term/_source) -> mutate -> PUT(if_seq_no/if_primary_term)`，冲突时有限重试。
3. 冲突超过重试上限后抛出明确并发异常（包含 `index/key/retries`），不静默覆盖。
4. `UpsertAsync` 保持直接写入，但对“更新路径”也支持可选 OCC 参数扩展点。

设计原则：

- 读改写必须具备并发冲突检测能力。
- 重放/重试场景下禁止“最后写入者覆盖”。

### 3.3 B3 Provider 组合层去歧义

1. Host 组合层不再无条件注册 InMemory/Elasticsearch/Neo4j 三套 ReadModel Provider。
2. 改为“按配置按需注册”：仅注册 `ReadModelProvider` 与 `RelationProvider` 实际需要的 provider。
3. 若配置为空或未知 provider，启动阶段直接 fail-fast 并给出配置路径提示（`Projection:ReadModel:Provider` / `Projection:ReadModel:RelationProvider`）。
4. 删除隐式默认推断，避免环境切换时行为漂移。

设计原则：

- 组合层负责消除二义性，不把歧义下放到运行时选择器。
- 配置错误要在启动期暴露，不延迟到首个请求。

### 3.4 M1 索引缺失策略显式化

1. 新增 `MissingIndexBehavior`（建议枚举）：`Throw` / `WarnAndReturnEmpty`。
2. 默认 `Throw`（breaking）：`AutoCreateIndex=false` 且索引不存在时，`Get/List` 抛错。
3. `WarnAndReturnEmpty` 仅作为开发调试模式，不作为生产默认。
4. 日志与指标输出：`provider/index/operation/behavior`。

### 3.5 M2 绑定解析确定性

1. 绑定键只接受 `Type.FullName`（breaking）。不再使用 `Type.Name` 回退。
2. 解析失败时抛出结构化异常，明确给出期望键名示例。
3. 启动校验增加“绑定键格式检查”，禁止短名键混入。

### 3.6 N1 List 顺序稳定性

1. `ListAsync` 始终带排序条件。
2. 当 `ListSortField` 为空时，默认按 `CreatedAt desc -> _id desc` 排序，优先按创建时间倒序并保证稳定输出。
3. 在 README 中明确排序语义与默认行为。

### 3.7 N2 RequiredIndexKinds 语义收紧

1. 能力校验由“任一命中（Overlaps）”改为“全部包含（AllContained）”。
2. 未来如需“任一命中”语义，必须通过显式匹配模式字段表达，不能默认放宽。

## 4. 详细改造清单（按代码层次）

### 4.1 Abstractions 层

目标文件：

- `src/Aevatar.CQRS.Projection.Abstractions/Abstractions/ProjectionReadModelCapabilityValidator.cs`
- `src/Aevatar.CQRS.Projection.Abstractions/Abstractions/ProjectionReadModelRequirements.cs`（如需引入匹配模式）

改造项：

1. `RequiredIndexKinds` 校验从 `Overlaps` 切换为 `All(...)`。
2. 如保留双语义，新增显式 `IndexKindMatchMode`，默认 `All`。

### 4.2 Runtime 层

目标文件：

- `src/Aevatar.CQRS.Projection.Runtime/Runtime/ProjectionReadModelBindingResolver.cs`
- `src/Aevatar.CQRS.Projection.Runtime/Runtime/ProjectionReadModelProviderSelector.cs`

改造项：

1. Binding resolver 仅解析 `FullName` 键。
2. 异常消息增强：输出 read model type + 期望配置键 + 实际键列表（截断）。
3. 选择器错误日志保持结构化，补充“配置缺失/未知 provider”的专有 reason。

### 4.3 Elasticsearch Provider 层

目标文件：

- `src/Aevatar.CQRS.Projection.Providers.Elasticsearch/DependencyInjection/ServiceCollectionExtensions.cs`
- `src/Aevatar.CQRS.Projection.Providers.Elasticsearch/Stores/ElasticsearchProjectionReadModelStore.cs`
- `src/Aevatar.CQRS.Projection.Providers.Elasticsearch/Configuration/ElasticsearchProjectionReadModelStoreOptions.cs`
- `src/Aevatar.CQRS.Projection.Providers.Elasticsearch/README.md`

改造项：

1. 能力声明修正：`supportsAliases=false`、`supportsSchemaValidation=false`。
2. OCC 落地：
   - 读取文档时抓取 `_seq_no`、`_primary_term`。
   - 更新请求带 `if_seq_no`、`if_primary_term`。
   - 冲突重试（可配置上限）。
3. 索引缺失行为策略化：新增 `MissingIndexBehavior` 与默认 `Throw`。
4. `ListAsync` 默认排序兜底为 `CreatedAt desc -> _id desc`。
5. 文档更新：能力矩阵、排序语义、索引缺失策略、并发冲突行为。

### 4.4 Workflow Host 组合层

目标文件：

- `src/workflow/extensions/Aevatar.Workflow.Extensions.Hosting/WorkflowProjectionProviderServiceCollectionExtensions.cs`
- `src/workflow/Aevatar.Workflow.Infrastructure/DependencyInjection/WorkflowCapabilityServiceCollectionExtensions.cs`
- `src/workflow/Aevatar.Workflow.Projection/Configuration/WorkflowExecutionProjectionOptions.cs`
- `src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowReadModelSelectionPlanner.cs`
- `src/workflow/Aevatar.Workflow.Projection/README.md`

改造项：

1. Provider 注册改为按需注册，不再全量注册三套 provider。
2. 配置缺失与未知 provider 启动即失败。
3. 移除隐式 provider 默认回退行为（保持配置显式化）。
4. 文档示例统一改为 `FullName` 绑定键。

## 5. 测试计划（必须新增）

### 5.1 单元测试

目标项目：`test/Aevatar.CQRS.Projection.Core.Tests`

新增/改造用例：

1. 能力声明一致性：Elasticsearch capability 不再声明 alias/schema 支持。
2. `RequiredIndexKinds` 全包含语义测试（正例/反例）。
3. Binding resolver 仅 FullName：短名键失败、FullName 成功。

### 5.2 组件/集成测试

目标项目：

- `test/Aevatar.CQRS.Projection.Core.Tests`
- `test/Aevatar.Workflow.Host.Api.Tests`

新增/改造用例：

1. Elasticsearch OCC 并发冲突测试：并发 mutate 不发生静默覆盖，冲突可观测。
2. `AutoCreateIndex=false` 且索引缺失：默认抛错；`WarnAndReturnEmpty` 下返回空并记录警告。
3. Host 按需注册：仅配置单 provider 时无歧义；空配置启动失败；未知 provider 启动失败。
4. `ListAsync` 默认排序稳定性测试（同一数据集多次读取顺序一致）。

### 5.3 回归验证命令

1. `dotnet build aevatar.slnx --nologo`
2. `dotnet test test/Aevatar.CQRS.Projection.Core.Tests/Aevatar.CQRS.Projection.Core.Tests.csproj --nologo`
3. `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo`
4. `dotnet test aevatar.slnx --nologo`
5. `bash tools/ci/architecture_guards.sh`
6. `bash tools/ci/test_stability_guards.sh`

## 6. 实施阶段（WBS）

### Phase 0（Blocking，必须先完成）

1. 修正 Elasticsearch capability 声明（B1）。
2. 引入 OCC 并发控制与冲突异常（B2）。
3. Host 组合层改为按需注册 + 显式配置 fail-fast（B3）。

交付标准：

1. 3 个 blocking 问题对应测试全部通过。
2. 无配置歧义路径可进入运行期。

### Phase 1（Major）

1. 索引缺失策略改为显式配置且默认抛错（M1）。
2. Binding resolver 改为 FullName-only 并补齐启动校验（M2）。

交付标准：

1. 配置错误在启动阶段暴露。
2. 读模型绑定无短名误绑定路径。

### Phase 2（Minor + 文档收口）

1. List 默认排序兜底（N1）。
2. IndexKind 全包含语义（N2）。
3. README/架构文档与配置示例统一更新。

交付标准：

1. 行为确定性增强且文档可执行。
2. 全量构建/测试/门禁通过。

## 7. 验收标准（Definition of Done）

1. 所有 review 项对应的代码路径均有测试覆盖。
2. Provider 能力声明与实现一致，不存在“声明支持但无实现”的字段。
3. Elasticsearch 读改写路径具备 OCC，冲突有确定性行为（重试或失败）。
4. Workflow Host Provider 组合无歧义，配置缺失/错误启动即失败。
5. Binding 只接受 FullName，消除跨命名空间同名冲突风险。
6. List 默认顺序稳定。
7. CI 门禁与全量测试通过，文档与实现一致。

## 8. 风险与控制

1. 风险：去除短名绑定与隐式 provider 默认值会导致旧配置启动失败。
2. 控制：错误信息必须指向具体配置路径，并在 README 给出新配置示例。
3. 风险：OCC 重试增加写延迟。
4. 控制：重试次数与超时可配置，冲突率纳入日志/指标观察。

## 9. 文档同步清单

需要同步更新：

1. `src/Aevatar.CQRS.Projection.Providers.Elasticsearch/README.md`
2. `src/workflow/Aevatar.Workflow.Projection/README.md`
3. `docs/CQRS_ARCHITECTURE.md`（Provider 选择与 binding 规则）
4. `docs/architecture/readmodel-graph-relations-refactor-blueprint.md`（补充本次 hardening 决策链接）

## 10. 本轮实施结果（2026-02-24）

1. 已完成 B1/B2/B3、M1/M2、N1/N2 全部代码改造与测试补齐。
2. 验证结果：
   - `dotnet test test/Aevatar.CQRS.Projection.Core.Tests/Aevatar.CQRS.Projection.Core.Tests.csproj --nologo`：通过。
   - `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo`：通过。
   - `dotnet test aevatar.slnx --nologo`：通过。
   - `bash tools/ci/architecture_guards.sh`：通过。
   - `bash tools/ci/test_stability_guards.sh`：通过。
