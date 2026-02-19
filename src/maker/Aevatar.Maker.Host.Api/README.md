# Aevatar.Maker.Host.Api

`Aevatar.Maker.Host.Api` 是 Maker 能力的独立宿主。

职责：

- 仅负责 HTTP 协议适配与依赖组合。
- 调用 `IMakerRunApplicationService` 执行用例。
- 不承载 Maker 领域规则与编排逻辑。

端点：

- `POST /api/maker/runs`

运行时装配：

- 通过 `UseAevatarCqrsRuntime(...)` 与 `AddAevatarCqrsRuntime(...)` 统一接入 CQRS Runtime。

运行：

```bash
dotnet run --project src/maker/Aevatar.Maker.Host.Api
```
