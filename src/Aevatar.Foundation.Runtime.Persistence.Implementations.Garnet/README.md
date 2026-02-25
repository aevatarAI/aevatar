# Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet

该项目提供 `IEventStore` 的 Garnet 生产实现，不绑定 Workflow/AI 业务语义。

## 提供能力

- `GarnetEventStore`：基于 Redis 协议（Garnet）持久化 `StateEvent`。
- `AddGarnetEventStore(...)`：DI 装配扩展，替换默认 `IEventStore`。
- `GarnetEventStoreOptions`：连接串、Key 前缀、Database 配置。

## 关键语义

- 追加写入走 Lua 脚本，带 `expectedVersion` 乐观并发检查。
- 读取按版本有序回放，支持 `fromVersion` 增量读取。
- `DeleteEventsUpToAsync` 支持快照后的历史事件裁剪。

## 运行时装配

在 Orleans runtime 中，当 `PersistenceBackend=Garnet` 时会自动装配 `GarnetEventStore`（无需业务层额外绑定）。
