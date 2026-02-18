# Aevatar.Platform.Infrastructure

Platform 基础设施实现。

包含：

- `BuiltInAgentCatalog`（子系统路由配置）
- `HttpPlatformCommandDispatchGateway`（HTTP 分发）
- `FileSystemPlatformCommandStateStore`（命令状态文件存储）
- `PlatformDispatchCommandHandler`（命令执行处理）
- `AddPlatformSubsystem(...)`（仅 Platform 业务依赖与路由装配）

说明：

- CQRS Runtime（Wolverine/MassTransit）统一由 `Aevatar.CQRS.Runtime.Hosting` 在 Host 层装配。
