# Aevatar.Mainnet.Host.Api

`Aevatar.Mainnet.Host.Api` 是主网宿主。

## 默认能力装配

- `builder.AddAevatarDefaultHost(...)`
- `builder.Services.AddMainnetCore(...)`
- `builder.AddWorkflowCapability()`
- `app.UseAevatarDefaultHost()`（自动挂载能力端点）

## 端点

- `POST /api/chat`
- `GET /api/ws/chat`
- `GET /api/agents`
- `GET /api/workflows`
- `GET /api/actors/{actorId}`
- `GET /api/actors/{actorId}/timeline`
