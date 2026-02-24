# Projection Store / ReadModel 严格评分卡（2026-02-24，重构后）

## 1. 审计范围

1. `src/Aevatar.CQRS.Projection.Stores.Abstractions`
2. `src/Aevatar.CQRS.Projection.Runtime.Abstractions`
3. `src/Aevatar.CQRS.Projection.Runtime`
4. `src/Aevatar.CQRS.Projection.Providers.InMemory`
5. `src/Aevatar.CQRS.Projection.Providers.Elasticsearch`
6. `src/Aevatar.CQRS.Projection.Providers.Neo4j`
7. `src/workflow/Aevatar.Workflow.Projection`
8. `src/workflow/extensions/Aevatar.Workflow.Extensions.Hosting`
9. `test/Aevatar.CQRS.Projection.Core.Tests`、`test/Aevatar.Workflow.Host.Api.Tests`

## 2. 验证基线

已执行并通过：

1. `dotnet build aevatar.slnx --nologo`
2. `dotnet test aevatar.slnx --nologo`
3. `bash tools/ci/architecture_guards.sh`
4. `bash tools/ci/projection_route_mapping_guard.sh`
5. `bash tools/ci/solution_split_guards.sh`
6. `bash tools/ci/solution_split_test_guards.sh`
7. `bash tools/ci/test_stability_guards.sh`

## 3. 总分结论

- **总分：90 / 100**
- **等级：A-（严格口径）**
- **结论：架构主干已清晰收敛为“Document/Graph 分离 + 各自一对多 Fan-out + ReadModel 接口驱动”。主要剩余风险集中在 Graph 清理窗口与跨存储一致性策略。**

## 4. 维度评分（严格扣分）

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 架构边界与抽象收敛 | 15 | 15 | 已删除 `Factory/Selector/RuntimeOptions/ProviderNames` 冗余层；Runtime 仅保留 fan-out 与 materialization。 |
| Provider 模型清晰度 | 15 | 15 | 从单选改为一对多广播：`ProjectionDocumentStoreFanout<,>`、`ProjectionGraphStoreFanout`。 |
| Document 索引语义完整性 | 20 | 18 | Elasticsearch 已使用 `DocumentIndexMetadata` 的 `MappingJson/Settings/Aliases` 初始化索引。 |
| Graph 关系语义与正确性 | 15 | 12 | `IGraphReadModel` 声明式节点/边清晰；但图清理仍依赖固定窗口扫描。 |
| 一致性与失败语义 | 10 | 8 | 双写为顺序 fan-out，非事务；故障语义仍是“部分成功即失败抛出”。 |
| Provider 实现质量与性能 | 10 | 8 | ES OCC 完整；Neo4j 子图遍历仍存在逐层多次查询放大风险。 |
| 可观测性 | 5 | 4 | 文档写路径日志较完整；图路径仍可补充统一成功日志与关键指标。 |
| 测试与治理门禁 | 10 | 10 | Core/Workflow 测试已对 fan-out 语义更新，build/test/guards 全通过。 |

## 5. 关键发现

### 高优先级

1. **Graph 清理窗口固定值风险**
   - `ProjectionGraphMaterializer` 仍使用固定 `Depth/Take` 子图扫描清理旧边。

### 中优先级

1. **跨 Provider 双写非事务**
   - 当前策略是顺序写入，失败后由上层重试/补偿，不保证原子。

2. **Neo4j 子图查询存在潜在放大**
   - 深度遍历为循环邻接查询模式，大图场景需继续压测与优化。

## 6. 严格整改建议

### P1（必须）

1. 为 Graph 清理增加可配置窗口、分批策略与 owner/version 标记。

### P2（应做）

1. 增加跨 Provider 写失败重试/补偿策略文档与可观测指标。
2. 为 Neo4j 子图查询补充压力测试与查询计划基准。

### P3（可选）

1. 引入图写入/清理统计指标（吞吐、延迟、清理命中率）。

## 7. 最终判定

重构后已达到“结构正确、边界清晰、可验证”的高质量状态；在严格口径下为 **90/100（A-）**。
