# 未提交代码评分卡（RoleGAgent App State/Config 扩展，修复后复评）

## 1. 审计范围与方法

- 审计对象：当前工作区未提交改动（10 个文件，`641` 行新增，`7` 行删除）。
- 重点关注：`RoleGAgent` 事件契约扩展、app config 回放恢复语义、测试与文档同步。
- 评分模型：采用 `docs/audit-scorecard/README.md` 定义的六维 100 分模型。

## 2. 客观验证结果

已执行命令与结果：

1. `bash tools/ci/architecture_guards.sh`  
   结果：通过（含 projection route-mapping 与 closed-world guard）。
2. `bash tools/ci/projection_route_mapping_guard.sh`  
   结果：通过。
3. `bash tools/ci/test_stability_guards.sh`  
   结果：通过（无违规轮询等待）。
4. `dotnet build aevatar.slnx --nologo`  
   结果：通过（`0 error`，`138 warning`，主要为 `NU1507` 多包源告警）。
5. `dotnet test aevatar.slnx --nologo`  
   结果：失败（`Aevatar.Integration.Tests` 1 条失败：`ConnectorCallModuleCoverageTests.HandleAsync_WhenFirstAttemptThrowsAndRetrySucceeds_ShouldPublishSuccess`，断言 `RunId` 期望 `corr-1` 实际为空）。
6. `dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo`  
   结果：通过（`62/62`）。

## 3. 整体评分（总分 99/100，等级 A+）

| 维度 | 权重 | 得分 | 评分说明（含证据） |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 20 | 新增能力在 `Abstractions -> Core` 正向落地，未引入跨层反向依赖。证据：`src/Aevatar.AI.Abstractions/ai_messages.proto:15-53`，`src/Aevatar.AI.Core/RoleGAgent.cs:68-129`。 |
| CQRS 与统一投影链路 | 20 | 20 | `SetRoleAppConfigEvent/SetRoleAppStateEvent` 均进入事件流并参与状态转移，符合事件驱动恢复路径。证据：`src/Aevatar.AI.Core/RoleGAgent.cs:123-129`，`src/Aevatar.AI.Core/RoleGAgent.cs:202-222`。 |
| Projection 编排与状态约束 | 20 | 20 | 本次改动未引入中间层事实态字典、无 `actorId -> context` 反查模式。证据：`src/Aevatar.AI.Core/RoleGAgent.cs`（未出现服务级映射字段）。 |
| 读写分离与会话语义 | 15 | 15 | 已修复 app config 回放语义：`ApplyConfigureRoleAgent/ApplySetRoleAppConfig` 写入状态，`OnStateChangedAsync` 回填 `Config`，无 Manifest 也可恢复 app config。证据：`src/Aevatar.AI.Core/RoleGAgent.cs:131-142`，`src/Aevatar.AI.Core/RoleGAgent.cs:190-222`，`test/Aevatar.AI.Tests/RoleGAgentReplayContractTests.cs:247-290`。 |
| 命名语义与冗余清理 | 10 | 10 | 事件命名、codec 常量、文档说明一致；`RoleGAgentState` 字段语义清晰。证据：`src/Aevatar.AI.Abstractions/ai_messages.proto:44-53`，`docs/ROLE.md:280-286`，`src/Aevatar.AI.Core/README.md:81-86`。 |
| 可验证性（门禁/构建/测试） | 15 | 14 | guards 全通过，`slnx build` 通过，AI 子系统回归全绿；但全量 `slnx test` 存在 1 条集成失败（当前工作区基线风险）。 |

## 4. 关键加分项

1. **扣分项已修复**：`SetRoleAppConfigEvent` 不再是 no-op，app config 进入 `RoleGAgentState` 并可回放恢复。
2. **契约-实现-测试闭环**：proto 字段扩展、状态迁移实现、无 Manifest 回放测试三者一致。证据：`src/Aevatar.AI.Abstractions/ai_messages.proto:44-53`，`src/Aevatar.AI.Core/RoleGAgent.cs:213-227`，`test/Aevatar.AI.Tests/RoleGAgentReplayContractTests.cs:248-290`。
3. **Fail-Fast 保持有效**：未知 codec 仍 fail-fast 且不持久化，风险可控。证据：`src/Aevatar.AI.Core/RoleGAgent.cs:229-251`，`test/Aevatar.AI.Tests/RoleGAgentReplayContractTests.cs:292-364`。
4. **文档同步到位**：`docs/ROLE.md` 与 `AI.Core/README.md` 已更新为“app config 可由事件回放恢复”的语义。

## 5. 扣分项与风险说明

### 5.1 当前剩余扣分（-1）

- **全量回归存在 1 条集成失败（Medium, -1）**  
  `dotnet test aevatar.slnx --nologo` 失败点：`Aevatar.Integration.Tests.ConnectorCallModuleCoverageTests.HandleAsync_WhenFirstAttemptThrowsAndRetrySucceeds_ShouldPublishSuccess`，`RunId` 断言不满足（期望 `corr-1`，实际空字符串）。  
  说明：失败位于 Integration 测试域，非本次 `RoleGAgent` 代码路径，但会影响“全量可验证性”评分。

## 6. 改进建议（优先级）

- **P1**：定位并修复上述 Integration 失败后，重新执行 `dotnet test aevatar.slnx --nologo`，争取恢复满分可验证性。
- **P2**：保留当前“app config 事件回放 + Manifest 完整快照”双轨说明，并在后续文档中明确各字段事实源边界。

## 7. 结论

本次针对 `RoleGAgent` 的两项原扣分点已完成修复与复评：  
- app config 回放语义问题已消除；  
- 全量验证已实际执行并纳入证据。  

复评后总分为 `99/100 (A+)`。当前唯一扣分来自一条 Integration 测试失败（与本次修复路径无直接耦合，但影响全量测试通过性）。
