# Aevatar.Foundation.Projection

`Aevatar.Foundation.Projection` 提供读侧最小公共抽象，不包含业务语义与运行时逻辑。

## 职责

- 定义读模型基础字段：`AevatarReadModelBase`（`RootActorId/CommandId/StateVersion/LastEventId/CreatedAt/UpdatedAt`）
- 定义通用读侧能力接口：
  - `IHasProjectionTimeline`
  - `IHasProjectionRoleReplies`
- 定义通用值对象：
  - `ProjectionTimelineEvent`
  - `ProjectionRoleReply`

## 设计约束

- 仅承载跨能力可复用的最小抽象。
- 不依赖 Workflow/Maker 等业务能力项目。
- 不承载 reducer/projector/存储实现。

## 用法

业务读模型可继承 `AevatarReadModelBase` 并按需实现能力接口：

- 需要时间线投影时实现 `IHasProjectionTimeline`
- 需要角色回复聚合时实现 `IHasProjectionRoleReplies`
