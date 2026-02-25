# PR #13 严格架构审计报告（2026-02-25）

- 审计日期：2026-02-25
- 审计对象：`feat/generic-event-sourcing-elasticsearch-readmodel`（相对 `dev`）
- 关联 PR：[Feat/generic event sourcing elasticsearch readmodel #13](https://github.com/aevatarAI/aevatar/pull/13)
- 审计方式：代码证据核查 + 定向测试 + 架构门禁
- 评分规范：`docs/audit-scorecard/README.md`（100 分模型，6 维度）

---

## 1. 严格结论

本次 PR 当前状态 **不满足合并条件**。

- Blocking：2（P1=1，P2=1）
- Major：0
- Medium：0

合并裁决：**Reject（需先修复 P1/P2 并补齐回归测试）**。

---

## 2. 整体评分（100 分制）

**总分：79 / 100（B）**

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 20 | 未发现跨层反向依赖。 |
| CQRS 与统一投影链路 | 20 | 14 | Lease 重放时间语义不确定，影响事件重放一致性。 |
| Projection 编排与状态约束 | 20 | 11 | ownership lease 在重放时被“刷新到当前时间”。 |
| 读写分离与会话语义 | 15 | 10 | 会话 TTL 过期判定可被激活重放扰动。 |
| 命名语义与冗余清理 | 10 | 10 | 命名和结构未见新增冗余壳层。 |
| 可验证性（门禁/构建/测试） | 15 | 14 | 门禁/测试通过，但关键语义测试缺口导致缺陷未被捕获。 |

---

## 3. 阻断问题清单（必须先修）

### P1：Ownership lease 使用运行时当前时间写状态，破坏回放语义

风险级别：**Blocking**

证据：
1. `ApplyAcquire` 使用 `DateTime.UtcNow` 写入 `LastUpdatedAtUtc`，而非事件发生时刻：  
   `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionOwnershipCoordinatorGAgent.cs:109`
2. `ApplyRelease` 同样使用 `DateTime.UtcNow`：  
   `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionOwnershipCoordinatorGAgent.cs:125`
3. 回放链路仅重放 payload，不携带 envelope timestamp 到状态迁移函数：  
   `src/Aevatar.Foundation.Core/EventSourcing/EventSourcingBehavior.cs:159-163`  
   `src/Aevatar.Foundation.Core/GAgentBase.TState.cs:72`

架构影响：
1. 激活重放历史事件时，租约更新时间会被重写为“当前激活时刻”，导致过期 lease 被误判为新鲜 lease。
2. ownership takeover 被额外阻塞一个 TTL 窗口，破坏会话过期接管语义。
3. 违反“事件重放确定性”和“事实源唯一”的架构要求。

修复准入标准（必须全部满足）：
1. 将 lease 更新时间改为“持久化事件时间”，禁止在 `Apply*` 中直接取 `DateTime.UtcNow`。
2. `ProjectionOwnershipAcquireEvent/ReleaseEvent` 增加可重放的时间字段（或等价机制），并在写入时设置。
3. 兼容历史事件（无时间字段时使用可解释降级策略），避免重放崩溃。
4. 新增回归测试：`Acquire -> Deactivate -> Activate -> stale lease takeover` 必须稳定通过。

---

### P2：YAML 显式 `temperature: 0` 语义丢失

风险级别：**Blocking**

证据：
1. YAML 应用路径将缺失温度和显式 0 都写成 `0`：  
   `src/Aevatar.AI.Core/RoleGAgentFactory.cs:56`
2. 处理器把 `evt.Temperature == 0` 转换为 `null`：  
   `src/Aevatar.AI.Core/RoleGAgent.cs:76`
3. `ConfigureRoleAgentEvent.temperature` 为 proto3 `double`，无法区分“未设置”和“显式 0”：  
   `src/Aevatar.AI.Abstractions/ai_messages.proto:19`

架构影响：
1. 用户无法表达“确定性 0 温度”配置，行为退化为 provider 默认值。
2. 配置语义在“YAML -> 事件 -> 运行时”链路发生信息损失，不满足显式配置优先原则。

修复准入标准（必须全部满足）：
1. 事件契约改为可区分“未设置/显式设置”的表示（例如 `optional`/`oneof`/wrapper）。
2. `RoleGAgentFactory.ApplyConfig` 精确保留 YAML 的 `null` 与 `0` 语义差异。
3. `HandleConfigureRoleAgent` 删除“0 => null”的语义折叠（仅在真正未设置时回落默认）。
4. 新增回归测试至少 2 条：  
   - 显式 `temperature: 0` 应保留为 0。  
   - 缺省 `temperature` 才走 provider 默认。

---

## 4. 测试与门禁验证（本次实跑）

1. `dotnet test test/Aevatar.CQRS.Projection.Core.Tests/Aevatar.CQRS.Projection.Core.Tests.csproj --nologo --filter "FullyQualifiedName~ProjectionOwnershipCoordinatorGAgentTests"`  
   - 结果：Passed（7 passed / 0 failed）
2. `dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --filter "FullyQualifiedName~RoleGAgentReplayContractTests|FullyQualifiedName~AIHooksAndRoleFactoryCoverageTests"`  
   - 结果：Passed（6 passed / 0 failed）
3. `bash tools/ci/architecture_guards.sh`  
   - 结果：Passed

说明：现有测试与门禁未覆盖上述两类语义回归点，因此“通过”不构成合并充分条件。

---

## 5. 覆盖缺口证据

1. ownership 回放测试只验证“Acquire+Release 后重放为 inactive”，未覆盖“active lease 重放后 TTL 语义”：  
   `test/Aevatar.CQRS.Projection.Core.Tests/ProjectionOwnershipAndSessionHubTests.cs:309`
2. role factory 测试覆盖了 `temperature: 0.2`，未覆盖显式 `temperature: 0`：  
   `test/Aevatar.AI.Tests/AIHooksAndRoleFactoryCoverageTests.cs:112`

---

## 6. PR 合并前强制检查清单

1. P1/P2 代码修复完成并通过 Code Review。
2. 新增回归测试覆盖“lease 重放时间语义”和“temperature:0 显式语义”。
3. 通过：
   - `dotnet test aevatar.slnx --nologo`
   - `bash tools/ci/architecture_guards.sh`
   - `bash tools/ci/test_stability_guards.sh`（如涉及测试改动）

在上述项全部满足前，本 PR 不应合并。
