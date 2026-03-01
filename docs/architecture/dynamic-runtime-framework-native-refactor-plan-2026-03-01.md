# Dynamic Runtime 框架原生化重构落地说明（2026-03-01）

## 1. 重构结论

本次已完成“无兼容性”重构：Dynamic Runtime 删除了中间层无语义端口转发与 `InMemory*` 生产实现，改为在应用层直接依赖框架抽象（`IStateStore<>`、`IEventDeduplicator`、Projection Provider 配置选择）。

## 2. 已落地变更

### 2.1 Abstractions

1. 删除端口接口：
- `IIdempotencyPort`
- `IConcurrencyTokenPort`
- `IEventEnvelopePublisherPort`
- `IEventEnvelopeSubscriberPort`
- `IEventEnvelopeDedupPort`
- `IEventEnvelopeDeliveryPort`

2. 删除无用模型：
- `IdempotencyAcquireResult`
- `ConcurrencyCheckResult`
- `EnvelopeDedupResult`

3. 新增框架状态消息（`dynamic_runtime_messages.proto`）：
- `ScriptIdempotencyState`
- `ScriptAggregateVersionState`
- `ScriptEventEnvelopeState`
- `ScriptEnvelopeLeaseState`
- `ScriptEnvelopeDeliveryState`
- `ScriptEnvelopeDeliveryPointerState`
- `ScriptEnvelopeBusState`

说明：新增状态均为 Protobuf 消息，确保可被框架 `IStateStore<>` 在本地/Orleans provider 下统一承载。

### 2.2 Application

`DynamicRuntimeApplicationService` 已由端口依赖切换为框架抽象直连：

1. 幂等：`IStateStore<ScriptIdempotencyState>`
2. 并发版本：`IStateStore<ScriptAggregateVersionState>`
3. Envelope 订阅/投递状态：`IStateStore<ScriptEnvelopeBusState>`
4. 去重：`IEventDeduplicator`

同时完成以下内聚实现：

1. 删除对 `IIdempotencyPort`/`IConcurrencyTokenPort`/`IEventEnvelope*Port` 的全部调用。
2. 在 `DynamicRuntimeApplicationService` 内部以状态消息实现 `Subscribe/Publish/List/Pull/Ack/Retry` 流程。
3. `PublishActorEventAsync` 改为直接使用 `IEventDeduplicator`。

### 2.3 Infrastructure

`src/Aevatar.DynamicRuntime.Infrastructure/ServiceCollectionExtensions.cs` 已清理 InMemory 注册，仅保留业务策略/编排组件注册。

已删除以下生产文件：

1. `InMemoryDynamicRuntimeReadStore.cs`
2. `InMemoryScriptServiceDefinitionStateStore.cs`
3. `InMemoryIdempotencyPort.cs`
4. `InMemoryConcurrencyTokenPort.cs`
5. `InMemoryEventEnvelopeDedupPort.cs`
6. `InMemoryEventEnvelopeBusState.cs`
7. `InMemoryEventEnvelopePublisherPort.cs`
8. `InMemoryEventEnvelopeSubscriberPort.cs`
9. `InMemoryEventEnvelopeDeliveryPort.cs`

### 2.4 Projection 与 Host 组合

1. `Aevatar.DynamicRuntime.Projection/ServiceCollectionExtensions.cs` 改为 `AddDynamicRuntimeProjection(IServiceCollection, IConfiguration)`。
2. Projection Provider 改为配置单选：
- Document：`Elasticsearch` 或 `InMemory`
- Graph：`Neo4j` 或 `InMemory`
3. 增加 legacy 配置禁用与生产环境 InMemory 策略校验。
4. `DynamicRuntimeCapabilityHostBuilderExtensions` 已改为传入 `builder.Configuration` 进行 provider 选择。

### 2.5 测试

1. Dynamic Runtime 应用测试已移除对 `src` 中 `InMemory*` 生产实现的依赖。
2. 新增测试支持文件 `DynamicRuntimeTestSupport.cs`，仅在 `test/` 提供本地替身（`TestStateStore`、测试读存储等）。
3. 订阅/发布行为断言改为直接读取 `ScriptEnvelopeBusState`，不再依赖旧端口记录器。

## 3. 对齐说明

本次实现与“框架原生化、删除无价值层、无兼容壳层”目标一致，核心变化为：

1. Dynamic Runtime 不再自带 `InMemory*Port` 业务转发层。
2. 运行期事实态转移到框架 `IStateStore<>` 统一承载。
3. Projection provider 切换由宿主配置驱动，不再硬编码 InMemory。

## 4. 验证结果

已执行并通过：

1. `dotnet build test/Aevatar.DynamicRuntime.Application.Tests/Aevatar.DynamicRuntime.Application.Tests.csproj --nologo --tl:off`
2. `dotnet test test/Aevatar.DynamicRuntime.Application.Tests/Aevatar.DynamicRuntime.Application.Tests.csproj --nologo --tl:off`（35/35）
3. `bash tools/ci/architecture_guards.sh`
4. `bash tools/ci/test_stability_guards.sh`
5. `dotnet build aevatar.slnx --nologo --tl:off`
