# Aevatar.Platform.Sagas

Platform 子系统命令生命周期的 Saga 追踪实现。

- `PlatformCommandSaga`：跟踪命令从 `Accepted -> Queued -> Running -> Completed/Failed`。
- `PlatformCommandSagaTracker`：将平台命令状态变更写入 Saga runtime。
- `IPlatformCommandSagaQueryService`：按 `commandId` 查询命令生命周期。
