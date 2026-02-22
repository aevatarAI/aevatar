# 未提交改动问题重构计划（2026-02-22）

## 1. 输入与目标

输入文档：`docs/audit-scorecard/worktree-architecture-scorecard-2026-02-22.md`

本计划聚焦关闭评分卡中的 3 类问题：

1. `P1`：Orleans 事件分发“自发布链路短路”。
2. `P2`：Projection Coordinator 类型校验在 manifest 缺失时放行。
3. `P3`：类型识别兜底使用 `Contains`，误判窗口偏大。

目标：在不引入兼容壳层的前提下，完成语义修复、测试补齐、门禁可验证闭环。

## 2. 重构原则

1. 正确性优先：先修复 `P1` 运行正确性，再收敛 `P2/P3`。
2. 单一事实源：类型判定以 manifest 精确类型信息为主，不依赖模糊字符串匹配。
3. 渐进落地：每个阶段必须可独立构建与测试通过。
4. 不保留弱语义兜底：移除 `Contains` 式类型判定。

## 3. 改造范围

### 3.1 Orleans 事件链路（P1）

目标文件：

1. `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Actors/OrleansGrainEventPublisher.cs`
2. `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrain.cs`

改造要求：

1. 调整 publisher 链追加时机，避免 `Self` 投递被自身 `ContainsPublisher` 过滤。
2. 保持跨 Actor 回环抑制语义不回退（跨节点仍可阻断重复回流）。
3. 为 `Self/Down/Up/Both/SendTo` 建立行为用例，覆盖“应处理/应抑制”两类路径。

### 3.2 Projection Coordinator 类型判定（P2）

目标文件：

1. `src/Aevatar.CQRS.Projection.Core/Orchestration/ActorProjectionOwnershipCoordinator.cs`
2. `test/Aevatar.CQRS.Projection.Core.Tests/ProjectionOwnershipAndSessionHubTests.cs`

改造要求：

1. `manifest == null` 不得直接放行。
2. 判定逻辑改为“可证明才通过”：
   - manifest 类型精确命中可通过；
   - manifest 缺失时仅允许可验证的本地类型证据（如明确 concrete 类型）；
   - 其余情况 fail-fast 并返回明确错误。
3. 补齐 manifest 缺失、类型不符、并发竞态恢复三类用例。

### 3.3 Workflow/Projection 类型识别收紧（P3）

目标文件：

1. `src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowRunActorPort.cs`
2. `src/Aevatar.CQRS.Projection.Core/Orchestration/ActorProjectionOwnershipCoordinator.cs`
3. `test/Aevatar.Workflow.Host.Api.Tests/WorkflowInfrastructureCoverageTests.cs`

改造要求：

1. 删除 `Contains` 兜底匹配。
2. 统一为精确策略：
   - 优先 `Type.GetType` + `IsAssignableFrom`；
   - 解析失败时仅允许 `AssemblyQualifiedName/FullName` 精确比较。
3. 增加“相似类型名不应命中”的反例测试。

## 4. 执行阶段

### Phase 1：修复 P1（阻断级）

1. 重构 Orleans publisher 链路追加逻辑与自投递路径。
2. 新增 Orleans 分发行为测试矩阵。
3. 通过 Foundation/Integration 相关测试。

### Phase 2：修复 P2（一致性）

1. 收紧 Coordinator actor 类型校验。
2. 明确 manifest 缺失时的可接受判定路径与失败语义。
3. 更新并发竞态测试断言。

### Phase 3：修复 P3（健壮性）

1. 移除 `Contains` 类型兜底。
2. 引入统一精确匹配工具方法（若重复逻辑出现则抽取共享方法）。
3. 补齐误判反例测试。

### Phase 4：回归与文档同步

1. 更新评分卡结论（`BLOCK -> PASS/CONDITIONAL` 取决于验证结果）。
2. 记录最终分值与剩余风险（若有）。

## 5. 验收标准

1. 不再出现 `Contains` 式类型兜底（目标文件内清零）。
2. Orleans `Self` 事件不被错误吞掉，跨 Actor 回环抑制仍有效。
3. Coordinator 类型校验在 manifest 缺失场景不再无条件放行。
4. 下列命令全部通过：
   - `dotnet build aevatar.slnx --nologo --no-restore -m:1 -nodeReuse:false --tl:off`
   - `dotnet test aevatar.slnx --nologo --no-build --no-restore -m:1 -nodeReuse:false --tl:off`
   - `bash tools/ci/architecture_guards.sh`
   - `bash tools/ci/projection_route_mapping_guard.sh`

## 6. 风险与应对

1. 风险：P1 修复后回环抑制退化。  
   应对：加入“跨 Actor 循环拓扑”反例测试，验证仍只处理一次。
2. 风险：P2 收紧导致现有本地测试路径失败。  
   应对：先补 manifest/类型证据，再收紧判定逻辑。
3. 风险：P3 精确匹配导致旧数据类型名无法识别。  
   应对：在测试中覆盖 `AssemblyQualifiedName` 与 `FullName` 两种合法格式。

## 7. 产出清单

1. 代码修复提交（P1/P2/P3）。
2. 测试补齐提交（Orleans 分发、类型判定反例）。
3. 复评分文档（更新 `docs/audit-scorecard/` 下对应评分卡）。
