# Aevatar.Platform.Application

Platform 应用层实现。

职责：

- 命令受理（生成 `CommandId`、写入状态、通过 `ICommandBus` 入队）。
- 查询编排（命令状态查询、内置 Agent 能力查询）。

依赖：

- `Aevatar.Platform.Application.Abstractions`
- `Aevatar.Platform.Abstractions`
- `Aevatar.CQRS.Core.Abstractions`
- `Aevatar.CQRS.Runtime.Abstractions`
