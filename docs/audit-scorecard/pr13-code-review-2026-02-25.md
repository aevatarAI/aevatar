# PR #13 代码审查报告（2026-02-25）

- 审查日期：2026-02-25
- 审查对象：`feat/generic-event-sourcing-elasticsearch-readmodel`（相对 `dev`）
- 关联 PR：[Feat/generic event sourcing elasticsearch readmodel #13](https://github.com/aevatarAI/aevatar/pull/13)
- 审查方式：源码核查 + 自动化测试 + 架构门禁

---

## 1. 结论

本次审查范围内的 F1-F4 问题均已落地修复，核心链路可用。

- Blocking：0
- Major：0
- Medium：1（非阻断优化项）

**合并建议**：可合并。  
**剩余优化项**：F2 outbox 的 completed 记录建议增加清理/归档策略，降低长期状态体积与扫描开销。

---

## 2. 问题与修复清单

| ID | 问题 | 状态 | 修复摘要 |
| --- | --- | --- | --- |
| F1 | ownership 可能长期占用 | 已修复 | 增加 lease TTL、续租与过期接管。 |
| F2 | 双写失败补偿仅日志，可能读侧不一致 | 已修复 | 改为 actor 化 outbox + 异步 replay + backoff 重试。 |
| F3 | distributed 配置可能回退 InMemory | 已修复 | 显式 durable provider + Document 侧门禁。 |
| F4 | 启动校验未覆盖外部可达性 | 已修复 | 启动期改为真实 provider probe，并按环境分级处理失败。 |

---

## 3. 关键修复说明

### F1：Ownership lease 过期与接管

1. 协议增加 `lease_ttl_ms`。
2. `Acquire` 支持同 session renew 与过期 takeover。
3. 新增 `ProjectionOwnershipCoordinatorOptions`（默认 TTL=30 分钟，可配置）。
4. `WorkflowExecutionProjectionOptions` 新增 `ProjectionOwnershipLeaseTtlMs` 并注入 ownership coordinator。

关键代码：
- `src/Aevatar.CQRS.Projection.Core/projection_ownership_messages.proto`
- `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionOwnershipCoordinatorGAgent.cs`
- `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionOwnershipCoordinatorOptions.cs`
- `src/Aevatar.CQRS.Projection.Core/Orchestration/ActorProjectionOwnershipCoordinator.cs`
- `src/workflow/Aevatar.Workflow.Projection/Configuration/WorkflowExecutionProjectionOptions.cs`

### F2：双写失败补偿（actor 化 outbox）

1. 补偿上下文扩展为 `DispatchId / OccurredAtUtc / ReadModelType`。
2. `WorkflowProjectionDurableOutboxCompensator` 在失败时入队补偿事件。
3. `ActorProjectionDispatchCompensationOutbox` 通过 runtime 将事件投递到 outbox actor。
4. `WorkflowProjectionDispatchCompensationOutboxGAgent` 维护持久状态并执行 replay。
5. `WorkflowProjectionDispatchCompensationReplayHostedService` 周期触发 replay，失败 backoff，成功标记 completed。

关键代码：
- `src/Aevatar.CQRS.Projection.Core/projection_dispatch_compensation_messages.proto`
- `src/workflow/Aevatar.Workflow.Projection/Orchestration/IProjectionDispatchCompensationOutbox.cs`
- `src/workflow/Aevatar.Workflow.Projection/Orchestration/ActorProjectionDispatchCompensationOutbox.cs`
- `src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowProjectionDispatchCompensationOutboxGAgent.cs`
- `src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowProjectionDurableOutboxCompensator.cs`
- `src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowProjectionDispatchCompensationReplayHostedService.cs`
- `src/workflow/Aevatar.Workflow.Projection/DependencyInjection/ServiceCollectionExtensions.cs`

### F3：distributed durable provider 与门禁

1. `appsettings.Distributed.json` 显式启用 ES/Neo4j，关闭 InMemory。
2. Document 侧新增 InMemory 门禁，与 Graph 策略对齐。

关键代码：
- `src/Aevatar.Mainnet.Host.Api/appsettings.Distributed.json`
- `src/workflow/extensions/Aevatar.Workflow.Extensions.Hosting/WorkflowProjectionProviderServiceCollectionExtensions.cs`

### F4：启动探测升级

1. 启动期改为真实 probe：
   - Document：`ListAsync(take:1)`
   - Graph：`ListNodesByOwnerAsync(...)`
2. 失败策略：
   - Production：fail-fast
   - 非 Production：warning 并继续

关键代码：
- `src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowReadModelStartupValidationHostedService.cs`

---

## 4. 验证结果

1. `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo`
   - 结果：Passed（160 passed / 0 failed）
2. `dotnet test test/Aevatar.CQRS.Projection.Core.Tests/Aevatar.CQRS.Projection.Core.Tests.csproj --nologo`
   - 结果：Passed（55 passed / 1 skipped / 0 failed）
3. `bash tools/ci/architecture_guards.sh`
   - 结果：Passed

---

## 5. 后续优化（非阻断）

1. 为 F2 增加 outbox completed 记录清理/归档策略。
2. 增加补偿运行指标：backlog、重试次数、重放延迟。
3. 在 distributed smoke 中增加跨节点补偿收敛断言。
