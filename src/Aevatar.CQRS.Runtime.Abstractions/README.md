# Aevatar.CQRS.Runtime.Abstractions

CQRS 运行时抽象层，只定义契约，不依赖具体中间件。

包含：

- 命令契约：`ICommandBus`、`ICommandScheduler`、`CommandEnvelope`、`QueuedCommandMessage`
- 命令处理契约：`ICommandHandler<TCommand>`
