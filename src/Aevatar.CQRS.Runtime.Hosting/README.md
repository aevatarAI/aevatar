# Aevatar.CQRS.Runtime.Hosting

统一 CQRS Runtime 宿主接入层，供各子系统 Host 复用。

职责：

- `AddAevatarCqrsRuntime(...)`：统一注册 `CQRS.Core + Runtime.Implementations.*`。
- `UseAevatarCqrsRuntime(...)`：统一挂接 Host 运行时（当前 Wolverine 需要 HostBuilder 扩展）。
- 统一配置键：`Cqrs:Runtime = Wolverine|MassTransit`。

约束：

- 子系统 Host/Infrastructure 不应再直接编排 `Implementations.*`。
- 运行时切换只通过配置完成，不修改子系统业务代码。
