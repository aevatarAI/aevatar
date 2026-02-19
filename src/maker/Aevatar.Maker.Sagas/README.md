# Aevatar.Maker.Sagas

Maker 子系统的 Saga 定义与查询服务。

- `MakerExecutionSaga`：按 `correlation_id` 追踪一次 maker 执行生命周期。
- `MakerExecutionSagaState`：执行状态与步骤统计。
- `IMakerExecutionSagaQueryService`：读侧查询接口。
