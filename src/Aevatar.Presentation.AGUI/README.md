# Aevatar.Presentation.AGUI

`Aevatar.Presentation.AGUI` 定义 Aevatar 与前端 UI 之间的事件协议和 SSE 写出基础设施。

## 职责

- 定义标准 AG-UI 事件模型（运行、步骤、文本流、工具调用、自定义事件）
- 提供 SSE 序列化写出器 `AGUISseWriter`
- 作为 HTTP/SSE presentation adapter 消费上游 CQRS/projection 已发布的 `AGUIEvent`

## 核心类型

- `agui_events.proto`：`RunStartedEvent`、`TextMessageContentEvent` 等事件定义
- `AGUISseWriter`：将 `AGUIEvent` 序列化为 `data: {json}\n\n` 输出

## 使用场景

- API 层从 CQRS/projection interaction stream 收到 `AGUIEvent` 后，通过 SSE 推送给前端
- 作为协议层被 `Aevatar.Workflow.Host.Api` 引用

## 依赖

- `Microsoft.AspNetCore.App`（FrameworkReference）
