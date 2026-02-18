# Aevatar.Platform.Infrastructure

Platform 基础设施实现。

包含：

- `BuiltInAgentCatalog`（子系统路由配置）
- `HttpPlatformCommandDispatchGateway`（HTTP 分发）
- `FileSystemPlatformCommandStateStore`（命令状态文件存储）
- `PlatformDispatchCommandHandler`（命令执行处理）
- `AddPlatformSubsystem(...)`（DI 组合，默认 `Wolverine`，可切 `MassTransit`）
