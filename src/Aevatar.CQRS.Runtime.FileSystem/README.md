# Aevatar.CQRS.Runtime.FileSystem

CQRS 本地文件系统实现，供 Wolverine/MassTransit 共享。

包含：

- 文件存储：CommandState / Inbox / Outbox / DeadLetter / ProjectionCheckpoint
- JSON 命令序列化：`ICommandPayloadSerializer`
- 通用命令分发：`ServiceProviderCommandDispatcher`
- 通用执行器：`QueuedCommandExecutor`（重试 + 死信 + 状态更新）
- DI 入口：`AddCqrsRuntimeFileSystemCore(...)`

默认工作目录：`artifacts/cqrs`。
