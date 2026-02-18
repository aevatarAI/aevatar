# Aevatar.CQRS.Runtime.Implementations.Wolverine

Wolverine 运行时适配层（默认实现）。

包含：

- `WolverineCommandBus`：实现 `ICommandBus`/`ICommandScheduler`
- `WolverineQueuedCommandHandler`：消费 `QueuedCommandMessage` 并委托 `IQueuedCommandExecutor`
- `AddCqrsRuntimeWolverine()`：DI 注册
- `UseAevatarCqrsWolverine()`：HostBuilder 扩展
