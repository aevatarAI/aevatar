# Aevatar.CQRS.Runtime.Abstractions

CQRS 运行时抽象层，只定义契约，不依赖具体中间件。

包含：

- 命令契约：`ICommandBus`、`ICommandScheduler`、`CommandEnvelope`、`QueuedCommandMessage`
- 执行与分发：`ICommandDispatcher`、`ICommandHandler<TCommand>`、`IQueuedCommandExecutor`
- 企业级持久化契约：`ICommandStateStore`、`IInboxStore`、`IOutboxStore`、`IDeadLetterStore`、`IProjectionCheckpointStore`
- 运行配置：`CqrsRuntimeOptions`
