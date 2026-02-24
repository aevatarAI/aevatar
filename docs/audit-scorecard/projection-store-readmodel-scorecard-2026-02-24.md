# Projection Store / ReadModel 严格评分卡（2026-02-24 复评分）

## 1. 审计范围

1. `src/Aevatar.CQRS.Projection.Stores.Abstractions`
2. `src/Aevatar.CQRS.Projection.Runtime.Abstractions`
3. `src/Aevatar.CQRS.Projection.Runtime`
4. `src/Aevatar.CQRS.Projection.Providers.InMemory`
5. `src/Aevatar.CQRS.Projection.Providers.Elasticsearch`
6. `src/Aevatar.CQRS.Projection.Providers.Neo4j`
7. `src/workflow/Aevatar.Workflow.Projection`
8. `src/workflow/extensions/Aevatar.Workflow.Extensions.Hosting`
9. `test/Aevatar.CQRS.Projection.Core.Tests`
10. `test/Aevatar.Workflow.Host.Api.Tests`

## 2. 本次验证基线

本次复评执行并通过：

1. `dotnet build aevatar.slnx --nologo`
2. `dotnet test test/Aevatar.CQRS.Projection.Core.Tests/Aevatar.CQRS.Projection.Core.Tests.csproj --nologo`
3. `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo`
4. `bash tools/ci/architecture_guards.sh`
5. `bash tools/ci/projection_route_mapping_guard.sh`

## 3. 总分结论

- **总分：84 / 100**
- **等级：B+（严格口径）**
- **结论：主架构已经从“单选 provider”收敛到“Document/Graph 分离的一对多 fan-out”，方向正确；但仍存在主查询源隐式选择、图清理窗口固定、跨 store 非事务一致性等高影响工程风险。**

## 4. 维度评分（严格）

| 维度 | 权重 | 得分 | 证据与说明 |
|---|---:|---:|---|
| 架构边界与抽象收敛 | 15 | 14 | 已删 `Factory/Selector/RuntimeOptions`，Runtime 仅保留 fan-out + materialization（`Projection.Runtime/DependencyInjection/ServiceCollectionExtensions.cs:11-15`）。扣分：`IProjectionStoreRegistration` 仍保留 `ProviderName`，但运行时不再用其做治理约束（`Runtime.Abstractions/.../IProjectionStoreRegistration.cs:3-7`）。 |
| Provider 模型清晰度 | 15 | 14 | Fan-out 明确：`ProjectionDocumentStoreFanout` 与 `ProjectionGraphStoreFanout`（`.../ProjectionDocumentStoreFanout.cs:6-84`，`.../ProjectionGraphStoreFanout.cs:6-82`）。扣分：主查询源由“第一个注册”隐式决定（`ProjectionDocumentStoreFanout.cs:34`，`ProjectionGraphStoreFanout.cs:31`）。 |
| Document 索引语义完整性 | 20 | 17 | ES 已使用 `DocumentIndexMetadata` 初始化 mappings/settings/aliases（`ElasticsearchProjectionReadModelStore.cs:486-503`，`505-532`）。扣分：元数据结构仍是 `Dictionary<string,string>`，复杂 settings/aliases 需字符串内嵌 JSON（`DocumentIndexMetadata.cs:3-7`，`ElasticsearchProjectionReadModelStore.cs:515-529`）；Workflow 默认 metadata 仍是空 mapping（`WorkflowExecutionReportDocumentMetadataProvider.cs:8-12`）。 |
| Graph 关系语义与正确性 | 15 | 12 | `IGraphReadModel` 采用声明式节点/边接口（`IGraphReadModel.cs:3-9`）；Workflow ReadModel 同时实现 Doc+Graph（`WorkflowExecutionReadModel.cs:32-45`）。扣分：图清理固定窗口 `Depth=8/Take=5000`（`ProjectionGraphMaterializer.cs:42-50`），且锚点推断为首节点/ReadModel.Id/首边 from（`66-79`）。 |
| 一致性与失败语义 | 10 | 7 | 路由按能力执行（`ProjectionMaterializationRouter.cs:31-37`）。扣分：跨 store 非事务；`Document` 先写成功后 `Graph` 失败会留下部分成功状态（`31-37`）；`Mutate` 后再 fan-out 到其它 store（`ProjectionDocumentStoreFanout.cs:58-73`）。 |
| Provider 实现质量与性能 | 10 | 8 | ES OCC 重试完备（`ElasticsearchProjectionReadModelStore.cs:93-143`）。扣分：Neo4j 子图遍历逐层调用 `GetNeighborsAsync`，存在放大风险（`Neo4jProjectionGraphStore.cs:190-209`）。 |
| 可观测性 | 5 | 3 | 文档路径日志完整（`ElasticsearchProjectionReadModelStore.cs:282-355`，`ProjectionDocumentStoreFanout.cs:35-38`）。扣分：Graph provider 成功路径日志薄弱，Neo4j 仅反序列化 warning（`Neo4jProjectionGraphStore.cs:475-480`）。 |
| 测试与治理门禁 | 10 | 9 | Fan-out 行为有单测（`ProjectionReadModelRuntimeTests.cs:10-57`，`ProjectionReadModelStoreSelectorTests.cs:10-64`）；Workflow 注册策略有覆盖（`WorkflowExecutionProjectionRegistrationTests.cs:18-64`，`WorkflowHostingExtensionsCoverageTests.cs:99-160`）；架构/路由守卫通过。扣分：缺少 `DocumentIndexMetadata` 的 mapping/settings/aliases 初始化行为专门测试。 |

## 5. 关键扣分项（按优先级）

### P1

1. **主查询源隐式顺序问题**
   - 当前 query store 取第一个注册，缺少显式 primary 机制（`ProjectionDocumentStoreFanout.cs:34`，`ProjectionGraphStoreFanout.cs:31`）。

2. **Graph 清理窗口固定值**
   - `Depth=8/Take=5000` 可能导致边清理不完整（`ProjectionGraphMaterializer.cs:42-50`）。

### P2

1. **跨 provider 双写非事务**
   - 先写后写失败不回滚（`ProjectionMaterializationRouter.cs:31-37`）。

2. **Neo4j 子图遍历放大风险**
   - 每层每节点邻接查询（`Neo4jProjectionGraphStore.cs:190-209`）。

### P3

1. **Graph 可观测性偏弱**
   - 缺少统一写入成功/失败指标日志模板。

## 6. 复评判定

当前实现已经达到“架构方向正确、分层清晰、可落地运行”，但在严格工程口径下仍未达到 A 档稳定性。复评分为 **84/100（B+）**。
