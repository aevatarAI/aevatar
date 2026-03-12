# GAgent 协议优先第五阶段：Direct Delivery、Publication 与 StateEvent 分层重构（2026-03-12）

## 1. 文档元信息

- 状态：Completed
- 版本：Final
- 日期：2026-03-12
- 适用范围：
  - `Aevatar.Foundation.Abstractions`
  - `Aevatar.Foundation.Core`
  - `Aevatar.Foundation.Runtime.*`
  - `Aevatar.CQRS.Projection.*`
  - `Aevatar.Workflow.*`
  - `Aevatar.Scripting.*`

## 2. 最终边界

第五阶段最终收口为以下模型：

1. 统一消息平面继续使用 `EventEnvelope + IStream/IStreamProvider + Actor Runtime`
2. `EnvelopeRoute` 已收敛为：
   - `DirectRoute`
   - `PublicationRoute`
3. `PublicationRoute` 明确区分：
   - `Topology Publication`
   - `Observer Publication`
4. `StateEvent` 继续作为 Event Sourcing 的权威持久化事实，不与 runtime message 混义
5. 业务 actor 公共能力面只保留：
   - `IEventPublisher.PublishAsync(...)`
   - `IEventPublisher.SendToAsync(...)`
6. commit 后 observer publication 已限制为 framework-internal `ICommittedStateEventPublisher`

## 3. 已完成实现

### 3.1 Route 语义

- `agent_messages.proto` 已从旧的混轴 route 模型收敛到 `DirectRoute + PublicationRoute`
- `TopologyAudience` 已替代旧 broadcast direction 语义
- `ObserverAudience.CommittedFacts` 已作为 committed fact publication 的强类型 audience

### 3.2 Event Sourcing 出口

- `StateEvent` 与 `EventEnvelope` 已明确分层
- `EventStoreCommitResult` 成为 commit 后的权威返回值
- `CommittedStateEventPublished` 成为 committed fact 的统一 publication payload
- 业务公共 `IEventPublisher` 已不再暴露 committed publication
- framework commit 后 publication 已通过 internal `ICommittedStateEventPublisher` 发出

### 3.3 Runtime 与拓扑

- Local / Orleans runtime 的 self-message 语义已统一为入队处理
- Local runtime 的 parent/children 拓扑状态已直接内联到 `LocalActor`
- 真实消息传播已收敛为：
  - direct delivery: runtime direct dispatch
  - topology publication: stream forwarding / relay binding
  - observer publication: projection / live sink / observer 可见，actor inbox 忽略

### 3.4 Projection / Workflow / Scripting

- projection/read-side 持续运行在同一消息平面上，不单独造 runtime
- workflow continuation 持续使用 `PublishAsync(..., TopologyAudience.Self)`
- scripting committed observation 已统一走 `CommittedStateEventPublished + PublicationRoute.observer`

## 4. 关键代码落点

- `src/Aevatar.Foundation.Abstractions/agent_messages.proto`
- `src/Aevatar.Foundation.Abstractions/IEventPublisher.cs`
- `src/Aevatar.Foundation.Core/EventSourcing/ICommittedStateEventPublisher.cs`
- `src/Aevatar.Foundation.Core/GAgentBase.cs`
- `src/Aevatar.Foundation.Core/GAgentBase.TState.cs`
- `src/Aevatar.Foundation.Runtime.Implementations.Local/Actors/LocalActor.cs`
- `src/Aevatar.Foundation.Runtime.Implementations.Local/Actors/LocalActorPublisher.cs`
- `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Actors/OrleansGrainEventPublisher.cs`
- `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionEnvelopeNormalizer.cs`

## 5. 验证结果

本阶段最终状态已通过以下验证：

- `dotnet build aevatar.slnx --nologo`
- `dotnet test test/Aevatar.Foundation.Core.Tests/Aevatar.Foundation.Core.Tests.csproj --nologo`
- `dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo`
- `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --nologo --filter "FullyQualifiedName~TextNormalizationProtocolContractTests|FullyQualifiedName~ClaimReplayTests|FullyQualifiedName~ClaimScriptDocumentDrivenFlexibilityTests|FullyQualifiedName~WorkflowGAgentCoverageTests"`
- `bash tools/ci/architecture_guards.sh`
- `bash tools/ci/test_stability_guards.sh`

覆盖率报告：

- `test/Aevatar.Foundation.Core.Tests/TestResults/33854f19-13e1-4fe5-b93d-32de26d1889e/coverage.cobertura.xml`
- `test/Aevatar.Foundation.Runtime.Hosting.Tests/TestResults/e83bddac-a644-4b81-87d9-a1adaf2eec3c/coverage.cobertura.xml`

## 6. 结论

第五阶段现在可以正式视为完成。

它解决的不是“再造第二套消息系统”，而是把统一消息平面内部的三类语义彻底分开：

1. `Direct Delivery`
2. `Publication`
3. `StateEvent`

同时把 framework-only commit publication 从业务公共 publisher 面收回，避免继续污染 actor 公共能力边界。
