# Projection Store / ReadModel 严格评分卡（2026-02-24 重新分析）

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

本次重新分析执行并通过：

1. `dotnet build aevatar.slnx --nologo`
2. `dotnet test test/Aevatar.CQRS.Projection.Core.Tests/Aevatar.CQRS.Projection.Core.Tests.csproj --nologo`
3. `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo`
4. `bash tools/ci/architecture_guards.sh`
5. `bash tools/ci/projection_route_mapping_guard.sh`
6. `bash tools/ci/test_stability_guards.sh`
7. `dotnet test aevatar.slnx --nologo`

## 3. 总分结论

- **总分：92 / 100**
- **等级：B+（严格口径，高于上版 84）**
- **结论：核心架构问题（查询源规则不清、图清理窗口清理、Neo4j 子图查询放大）已实质修复；当前主要短板转为跨 Store 一致性与可观测性。**

## 4. 维度评分（严格）

| 维度 | 权重 | 得分 | 证据与说明 |
|---|---:|---:|---|
| 架构边界与抽象收敛 | 15 | 15 | `IProjectionStoreRegistration` 收敛为最小契约（`ProviderName + Create`），Runtime 组装边界清晰（`IProjectionStoreRegistration.cs`，`DelegateProjectionStoreRegistration.cs`）。 |
| Provider 模型清晰度 | 15 | 15 | Document/Graph fan-out 使用统一规则“注册顺序即查询顺序”（首注册 provider 读，全部 provider 写 fan-out）（`ProjectionDocumentStoreFanout.cs`，`ProjectionGraphStoreFanout.cs`）；Workflow Host 落地耐久优先注册顺序（`WorkflowProjectionProviderServiceCollectionExtensions.cs`）。 |
| Document 索引语义完整性 | 20 | 19 | `DocumentIndexMetadata` 已升级为结构化对象（`Mappings/Settings/Aliases`），ES 初始化直接消费结构化元数据（`DocumentIndexMetadata.cs`，`ElasticsearchProjectionReadModelStore.cs`）；复杂 metadata 组合行为已有单测覆盖（`ElasticsearchProjectionReadModelStoreBehaviorTests.cs`）。扣分：仍缺少真实 ES 集群下复杂 settings/aliases 的端到端回归。 |
| Graph 关系语义与正确性 | 15 | 15 | Graph materializer 已完成 owner-based 边/节点差集清理，节点删除前有邻接检查防误删共享节点（`ProjectionGraphMaterializer.cs`）；owner 标识收敛为 `IGraphReadModel.Id`，`Id` 为空 fail-fast，消除 fallback 残留语义（`ProjectionGraphMaterializer.cs`、`ProjectionGraphMaterializerTests.cs`）。 |
| 一致性与失败语义 | 10 | 7 | Router 仍是顺序写入（先 Document，再 Graph）（`ProjectionMaterializationRouter.cs:31-37`），跨 provider 非事务；`Mutate` 后读回再刷新 graph（`ProjectionMaterializationRouter.cs:49-63`），失败时可能留部分成功状态。 |
| Provider 实现质量与性能 | 10 | 9 | ES `MutateAsync` OCC 重试与冲突处理较完整（`ElasticsearchProjectionReadModelStore.cs`）；Neo4j `GetSubgraphAsync` 已从逐节点循环改为单次 Cypher 拉边 + 一次节点补全（`Neo4jProjectionGraphStore.cs`），显著降低查询放大。扣分：仍缺少高规模数据集下的性能基准回归。 |
| 可观测性 | 5 | 3 | Fan-out 初始化日志已有 provider 信息（`ProjectionDocumentStoreFanout.cs:76-80`，`ProjectionGraphStoreFanout.cs:62-65`）；ES 冲突日志较完整（`ElasticsearchProjectionReadModelStore.cs:124-133`）。扣分：Graph provider 成功路径指标/日志仍偏薄，Neo4j 侧主要是反序列化 warning（`Neo4jProjectionGraphStore.cs:546-551`）。 |
| 测试与治理门禁 | 10 | 9 | fan-out 查询顺序与写扩散语义测试已补齐（`ProjectionReadModelRuntimeTests.cs`，`ProjectionReadModelStoreSelectorTests.cs`）；owner 维度边/节点清理与空 Id fail-fast 回归已补（`ProjectionGraphMaterializerTests.cs`）；structured metadata 初始化测试已补（`ElasticsearchProjectionReadModelStoreBehaviorTests.cs`）；门禁脚本通过。扣分：仍缺少真实 Neo4j/ES 双 provider 联动压力测试。 |

## 5. 关键扣分项（按优先级）

### P1

1. **跨 Store 写入非原子**
   - `ProjectionMaterializationRouter` 仍是串行双写，缺少统一事务/补偿机制（`ProjectionMaterializationRouter.cs:31-37`）。

### P2

1. **跨 Provider 高规模性能基线不足**
   - 缺少 Neo4j/ES 双 provider 联动的压力测试与容量基线，尚未形成性能门禁。

### P3

1. **Graph 成功路径可观测性不足**
   - 缺少统一写入吞吐、清理计数、owner 命中率等指标日志。

## 6. 重新评分判定

当前实现已进入“结构清晰 + 关键缺陷大幅收敛”的阶段，但在严格生产工程标准下仍未到 A 档。重新评分为 **92/100（B+）**。
