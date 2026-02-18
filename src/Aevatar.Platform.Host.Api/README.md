# Aevatar.Platform.Host.Api

`Aevatar.Platform.Host.Api` 是主系统统一入口，并承担 Platform 子系统的 CQRS API 宿主职责。

职责：

- 暴露内置 GAgent 能力目录与路由解析（Query 侧）。
- 暴露平台统一命令入口（Command 侧）。
- 命令受理后异步分发到子系统，并可通过查询端点查看状态。
- 不承载 Workflow/Maker 领域编排逻辑。

CQRS 端点：

- `POST /api/commands`
- `GET /api/commands/{commandId}`
- `GET /api/commands`
- `GET /api/agents`
- `GET /api/routes/{subsystem}/commands/{*command}`
- `GET /api/routes/{subsystem}/queries/{*query}`

运行：

```bash
dotnet run --project src/Aevatar.Platform.Host.Api
```
