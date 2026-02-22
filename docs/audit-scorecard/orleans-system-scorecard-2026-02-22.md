# Orleans 系统架构审计评分卡（2026-02-22 复评）

## 1. 审计范围与方法

1. 审计对象：`Aevatar.Foundation.Runtime.Implementations.Orleans` + `Aevatar.Foundation.Runtime.Hosting` + 对应测试。
2. 评分规范：`docs/audit-scorecard/README.md`（100 分模型）。
3. 复评基线：已纳入“全部修复”代码后重新执行门禁/构建/测试。

## 2. 客观验证结果

| 检查项 | 命令 | 结果 |
|---|---|---|
| 架构门禁 | `bash tools/ci/architecture_guards.sh` | 通过 |
| Orleans 实现构建 | `dotnet build src/Aevatar.Foundation.Runtime.Implementations.Orleans/Aevatar.Foundation.Runtime.Implementations.Orleans.csproj --nologo --tl:off -m:1 -nodeReuse:false` | 通过（0 warning / 0 error） |
| Orleans 相关测试 | `dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo --tl:off -m:1 -nodeReuse:false` | 通过（36 passed / 1 skipped） |

> 说明：被跳过用例是条件集成测试，需设置 `AEVATAR_TEST_KAFKA_BOOTSTRAP_SERVERS` 才执行。

## 3. 整体评分（Overall）

**98 / 100（A+）**

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 20 | Hosting 仅做 provider/transport 装配；Orleans 实现保持在 Runtime 实现层。 |
| CQRS 与统一投影链路 | 20 | 20 | 未引入双轨链路，分发入口保持统一。 |
| Projection 编排与状态约束 | 20 | 20 | 中间层事实态回归到 grain/distributed state；stream 缓存已具备销毁清理路径。 |
| 读写分离与会话语义 | 15 | 14 | Kafka 分发异常已可见且可上抛；剩余 1 分为真实 broker 环境验证依赖外部环境。 |
| 命名语义与冗余清理 | 10 | 10 | 命名空间、项目名、目录语义一致。 |
| 可验证性（门禁/构建/测试） | 15 | 14 | 门禁+构建+单测通过，已补集成测试但当前环境未执行（skip）。 |

## 4. 关键修复证据（本轮）

1. Kafka namespace 过滤与配置一致（去除硬编码前缀匹配）。
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Streaming/KafkaAdapter/OrleansKafkaQueueAdapterReceiver.cs:44`
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Streaming/KafkaAdapter/OrleansKafkaQueueAdapterFactory.cs:40`
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Streaming/KafkaAdapter/OrleansKafkaQueueAdapter.cs:73`

2. Kafka 分发异常不再吞掉，增加日志并向上抛出。
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Transport/Kafka/KafkaEnvelopeDispatcher.cs:33`
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Transport/Kafka/KafkaEnvelopeDispatcher.cs:65`

3. Stream 缓存回收路径已落地并接入销毁流程。
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Streaming/IStreamCacheManager.cs:3`
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Streaming/StreamProviderLifecycleManager.cs:6`
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Streaming/OrleansStreamProviderAdapter.cs:33`
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Streaming/MassTransitKafkaStreamProvider.cs:28`
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Transport/Kafka/ServiceCollectionExtensions.cs:66`
   - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Actors/OrleansActorRuntime.cs:82`

4. 补齐单元/集成测试覆盖。
   - `test/Aevatar.Foundation.Runtime.Hosting.Tests/OrleansKafkaQueueAdapterReceiverTests.cs:15`
   - `test/Aevatar.Foundation.Runtime.Hosting.Tests/KafkaEnvelopeDispatcherTests.cs:9`
   - `test/Aevatar.Foundation.Runtime.Hosting.Tests/StreamProviderLifecycleManagerTests.cs:11`
   - `test/Aevatar.Foundation.Runtime.Hosting.Tests/OrleansActorRuntimeForwardingTests.cs:78`
   - `test/Aevatar.Foundation.Runtime.Hosting.Tests/OrleansKafkaRuntimeIntegrationTests.cs:20`
   - `test/Aevatar.Foundation.Runtime.Hosting.Tests/KafkaIntegrationFactAttribute.cs:4`

## 5. 发现列表（复评）

### P1

- 无。

### P2

- 无。

### P3

1. Orleans+Kafka 端到端测试依赖外部 broker 环境变量，当前复评环境为 `skip`。
   - 证据：`test/Aevatar.Foundation.Runtime.Hosting.Tests/KafkaIntegrationFactAttribute.cs:8`
   - 建议：在 CI 增加带 Kafka 的作业并设置 `AEVATAR_TEST_KAFKA_BOOTSTRAP_SERVERS`，确保该用例常态执行。

## 6. 审计结论

- 结论：`PASS`
- 当前等级建议：`可合并`
- 综合评价：本轮已关闭此前所有 P2 缺陷，剩余风险为环境型验证项（P3）。
