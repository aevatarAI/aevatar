# Aevatar.CQRS.Sagas.Core

CQRS Saga 通用核心实现。

包含：

- `SagaRuntime`：接收 `EventEnvelope` 并驱动 Saga 状态迁移。
- `ActorSagaSubscriptionHostedService`：统一订阅运行时 Actor 流。
- `CommandBusSagaCommandEmitter`：Saga 动作映射到 CQRS 命令总线。
- `DefaultSagaCorrelationResolver`：默认关联键解析。

该项目不包含业务 Saga 定义。
