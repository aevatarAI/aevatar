# Aevatar 全量架构审计评分卡

- 审计日期：`2026-03-15`
- 审计范围：当前未提交工作区，全仓 `aevatar.slnx`
- 变更规模：`当前工作区大规模重构，含 GAgentService Phase 2/3 收敛、治理迁移路径与文档收口`
- 审计方法：全量命令验证 + 关键代码路径抽样 + 已确认 review finding 复核

## 1. 客观验证结果

| 命令 | 结果 |
|---|---|
| `git diff --check` | 通过 |
| `dotnet build aevatar.slnx --nologo` | 通过 |
| `dotnet test aevatar.slnx --nologo` | 通过 |
| `bash tools/ci/architecture_guards.sh` | 通过 |
| `bash tools/ci/projection_route_mapping_guard.sh` | 通过 |
| `bash tools/ci/solution_split_guards.sh` | 通过 |
| `bash tools/ci/solution_split_test_guards.sh` | 通过 |
| `bash tools/ci/test_stability_guards.sh` | 通过 |
| `bash tools/ci/code_metrics_analyzer_guard.sh` | 通过 |
| `bash tools/ci/coverage_quality_guard.sh` | 通过，报告见 `artifacts/coverage/20260315-170543-ci-gate/report/Summary.txt`，`line=88.4%`，`branch=72.0%` |

说明：

1. 全量测试命令通过；部分外部环境依赖测试为条件性 `skip`，主要集中在 Elasticsearch / Neo4j / Orleans / Garnet 场景。
2. 本次评分遵循 [TEMPLATE.md](TEMPLATE.md) 的基线口径，不对 `InMemory`、`Local Actor Runtime`、`ProjectReference` 单独扣分。

## 2. 整体评分

- 总分：`90 / 100`
- 等级：`A-`
- 结论：主重构方向正确，工程门禁、主链可验证性和升级安全性都已闭合。`GAgentService` 的治理聚合收敛、Phase 3 serving/rollout 主链、旧治理状态升级路径与端点目录语义回归都已经修复，当前状态可进入合并整理阶段。

### 2.1 六维评分

| 维度 | 得分 | 结论 |
|---|---:|---|
| 分层与依赖反转 | 18 / 20 | `Host / Application / Core / Projection` 边界清晰，查询口已拆成 lifecycle/serving 两组，治理升级逻辑被隔离在 migration/activation 边界。 |
| CQRS 与统一投影链路 | 18 / 20 | 已回到 `Query -> ReadModel` 主线，去掉 actor query；治理数据切换到新聚合后也补齐了导入路径与统一 projection。 |
| Projection 编排与状态约束 | 18 / 20 | 投影运行时样板明显收敛，`ServiceProjectionDescriptor` 等模板化抽象有效；治理读侧也收束为单一 `ServiceConfigurationReadModel`。 |
| 读写分离与会话语义 | 13 / 15 | 服务调用解析和治理能力视图都走读侧，`UpdateServiceEndpointCatalogCommand` 已恢复严格 create-vs-update 语义。 |
| 命名语义与冗余清理 | 8 / 10 | `ServicePhase3*` 这类阶段命名已被清除，重复投影外壳也大幅删除；仍有少量历史设计文档保留旧叙事。 |
| 可验证性（门禁 / 构建 / 测试） | 14 / 15 | 构建、测试、门禁、覆盖率、代码指标整体稳定；但当前测试还没覆盖到治理历史数据升级回归。 |

## 3. 分模块评分

| 模块 | 分数 | 结论 |
|---|---:|---|
| `Foundation + Runtime` | 92 | actor substrate、runtime-neutral 抽象和宿主测试链路稳定，分片构建与分片测试均通过。 |
| `CQRS + Projection` | 91 | 统一投影主链、读写抽象和 provider 适配稳定，且门禁覆盖较完整。 |
| `Workflow` | 90 | 主链成熟，门禁和测试强，当前未见新的架构级倒退。 |
| `Scripting` | 90 | protobuf-first 和 typed surface 主线已成型，质量门禁与覆盖率状态良好。 |
| `GAgentService` | 89 | Phase 1/2/3 主链已闭合，治理聚合、升级迁移、serving/rollout、查询面与投影运行时都已收敛。 |
| `Host + Docs + Guards` | 87 | Host 组合面仍然克制，门禁脚本完整；文档状态已明显收口，但仍有历史基线文档需要继续清噪。 |

## 4. 关键加分证据

1. 查询面已从单一大口拆回职责明确的窄接口，[IServiceLifecycleQueryPort.cs](../../src/platform/Aevatar.GAgentService.Abstractions/Ports/IServiceLifecycleQueryPort.cs) 与 [IServiceServingQueryPort.cs](../../src/platform/Aevatar.GAgentService.Abstractions/Ports/IServiceServingQueryPort.cs) 分离了生命周期查询与 serving 查询，避免继续膨胀成万能查询总线。
2. 治理读侧不再把服务绑定退化成字符串键，[ServiceBindingCatalogSnapshot.cs](../../src/platform/Aevatar.GAgentService.Governance.Abstractions/Queries/ServiceBindingCatalogSnapshot.cs) 里已经恢复 `BoundServiceReferenceSnapshot(ServiceIdentity Identity, string EndpointId)` 的强类型引用。
3. 激活能力视图已明确通过读侧与 artifact 组装，而不是回退到 actor query，[ActivationCapabilityViewAssembler.cs](../../src/platform/Aevatar.GAgentService.Governance.Application/Services/ActivationCapabilityViewAssembler.cs) 直接从 `catalog + configuration + artifact` 组装能力视图。
4. 旧治理持久态已有显式升级路径，[DefaultServiceGovernanceLegacyImporter.cs](../../src/platform/Aevatar.GAgentService.Governance.Infrastructure/Migration/DefaultServiceGovernanceLegacyImporter.cs) 会从旧 `bindings / endpoint-catalog / policies` 事实源导入新聚合，[ServiceGovernanceLegacyMigrationHostedService.cs](../../src/platform/Aevatar.GAgentService.Governance.Hosting/Migration/ServiceGovernanceLegacyMigrationHostedService.cs) 在宿主启动时主动执行导入。
5. `ServiceConfigurationGAgent` 已恢复 endpoint catalog 的严格 update 语义，[ServiceConfigurationGAgent.cs](../../src/platform/Aevatar.GAgentService.Governance.Core/GAgents/ServiceConfigurationGAgent.cs) 的 `HandleUpdateAsync(UpdateServiceEndpointCatalogCommand)` 会显式验证 catalog 已存在。
6. Phase 3 投影运行时样板已经模板化，[ServiceProjectionDescriptor.cs](../../src/platform/Aevatar.GAgentService.Projection/Orchestration/ServiceProjectionDescriptor.cs) 与 [ServiceProjectionPortServices.cs](../../src/platform/Aevatar.GAgentService.Projection/Orchestration/ServiceProjectionPortServices.cs) 消除了大量重复 `Activation / Release / Port` 外壳。
7. Host 层仍然只做组合与协议映射，[ServiceEndpoints.cs](../../src/platform/Aevatar.GAgentService.Hosting/Endpoints/ServiceEndpoints.cs) 负责总入口装配，[ServiceServingEndpoints.cs](../../src/platform/Aevatar.GAgentService.Hosting/Endpoints/ServiceServingEndpoints.cs) 则通过 `IServiceCommandPort / IServiceLifecycleQueryPort / IServiceServingQueryPort` 暴露 serving/rollout API，没有把业务编排塞回宿主。

## 5. 主要扣分项

### 5.1 [Medium] 历史设计文档仍有旧叙事噪音

- 证据：`docs/architecture/2026-03-14-*` 与 `docs/architecture/2026-03-15-*` 中仍保留多份历史基线文档，虽然顶部已经加了状态说明，但全文仍存在旧三-actor 和单一 `IServiceQueryPort` 叙事。
- 影响：
  1. 新人阅读成本偏高。
  2. 文档搜索结果仍会出现历史对象名。
- 扣分：`-1`

### 5.2 [Medium] 治理升级路径引入了额外运维复杂度

- 证据：[DefaultServiceGovernanceLegacyImporter.cs](../../src/platform/Aevatar.GAgentService.Governance.Infrastructure/Migration/DefaultServiceGovernanceLegacyImporter.cs) 与 [ServiceGovernanceLegacyMigrationHostedService.cs](../../src/platform/Aevatar.GAgentService.Governance.Hosting/Migration/ServiceGovernanceLegacyMigrationHostedService.cs) 为旧持久态补齐了导入链路。
- 影响：
  1. 升级期多了一次显式导入与宿主启动扫描。
  2. 后续如果再变更治理聚合模型，仍需要继续维护迁移测试。
- 扣分：`-1`

### 5.3 [Low] 条件性外部环境集成测试仍依赖 CI/专门环境

 证据：全量测试通过，但 Elasticsearch / Neo4j / Orleans 3 节点等用例仍以环境变量控制 `skip`。
 影响：这不是架构错误，但生产 provider 行为仍需要外部环境周期性验证。
- 扣分：`-1`

## 6. 合并准入结论

当前阻断项已经关闭：

1. 旧治理持久态已有显式升级路径。
2. endpoint catalog 的严格 update 语义已经恢复。
3. 对应回归测试已经补齐：
   - [GovernanceGAgentTests.cs](../../test/Aevatar.GAgentService.Tests/Core/GovernanceGAgentTests.cs)
   - [GovernanceInfrastructureTests.cs](../../test/Aevatar.GAgentService.Tests/Infrastructure/GovernanceInfrastructureTests.cs)
   - [ServiceConfigurationProjectorAndQueryTests.cs](../../test/Aevatar.GAgentService.Tests/Projection/ServiceConfigurationProjectorAndQueryTests.cs)

当前结论是：`Go for merge after normal review/commit hygiene`。

## 7. 改进优先级建议

### P1

1. 继续清理历史文档噪音，把仍标记为历史基线但全文容易误读当前实现的文档再收紧一轮。
2. 把治理升级导入链路的宿主观测与日志再补强，确保线上升级时能快速判断导入是否发生、是否跳过。

### P2

1. 如果后续还要继续收敛查询面，优先保持 capability-style query ports，不要重新回到单一聚合式 `IServiceQueryPort`。
2. 对 provider-backed 集成测试保持单独 CI profile，持续验证 Elasticsearch / Neo4j / Orleans 条件环境。

## 8. 非扣分观察项

1. `Phase 2 Governance` 单独拆项目本身不扣分。当前主要工作已经从“是否该拆”转向“如何保持单聚合高内聚和清晰迁移路径”。
2. `Phase 3` 继续留在主 `GAgentService` 项目组里不扣分。以当前复杂度看，它仍属于 service control-plane 主链，不需要再额外拆 bounded context。
3. 当前工作区删除量明显大于新增量，这符合“删除优于兼容”的重构原则；本次评分不因为删除旧壳层而扣分。
4. `InMemory`、`Local Runtime`、`ProjectReference` 仍按基线口径处理，不作为当前评分扣分项。

## 9. 最终结论

这轮重构把真正臃肿的部分收掉了：查询面更窄，投影样板更少，Host 命名和职责更干净，`GAgentService` 的主链也已经比前一轮清晰得多。  
关键的是，之前阻断合并的两个行为回归已经关闭：

1. 旧治理持久态已经有升级导入路径。
2. endpoint catalog 的 update 契约已经恢复。

当前评分卡结论更新为：`90 / 100 (A-)`，`Go for merge after normal review/commit hygiene`。
