# Aevatar.CQRS.Runtime.Implementations.MassTransit

MassTransit 运行时适配层（分布式高性能实现）。

包含：

- `MassTransitCommandBus`：实现 `ICommandBus`/`ICommandScheduler`
- `QueuedCommandConsumer`：消费 `QueuedCommandMessage` 并直接分发到 `ICommandHandler<TCommand>`
- `AddCqrsRuntimeMassTransit()`：DI 注册（默认 in-memory transport）
