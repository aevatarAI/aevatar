# Aevatar.Platform.Application.Abstractions

Platform 应用层抽象（CQRS）。

包含：

- Command：`IPlatformCommandApplicationService` 与命令模型
- Query：`IPlatformCommandQueryApplicationService`、`IPlatformAgentQueryApplicationService`
- Ports：`IPlatformCommandDispatchGateway`、`IPlatformCommandStateStore`

约束：不包含任何 HTTP/存储实现。
