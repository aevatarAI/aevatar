# Aevatar.CQRS.Sagas.Abstractions

通用 Saga 抽象契约项目。

包含：

- `ISaga` / `SagaBase<TState>`：Saga 状态机定义。
- `ISagaState`：Saga 状态最小契约。
- `ISagaRuntime`：事件驱动执行入口。
- `ISagaRepository`：Saga 状态持久化抽象。
- `ISagaCommandEmitter` / `ISagaTimeoutScheduler`：外部副作用抽象。
- `SagaRuntimeOptions`：运行时配置。

该项目不包含任何业务语义（Workflow/Maker/Platform）。
