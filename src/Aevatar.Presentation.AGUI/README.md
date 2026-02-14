# Aevatar.Presentation.AGUI

`Aevatar.Presentation.AGUI` 定义 Aevatar 与前端 UI 之间的事件协议和 SSE 写出基础设施。

## 职责

- 定义标准 AG-UI 事件模型（运行、步骤、文本流、工具调用、自定义事件）
- 提供线程安全事件通道 `AGUIEventChannel`
- 提供 SSE 序列化写出器 `AGUISseWriter`
- 抽象事件接收接口 `IAGUIEventSink`

## 核心类型

- `AGUIEvents.cs`：`RunStartedEvent`、`TextMessageContentEvent` 等事件定义
- `AGUIEventChannel`：基于 `Channel<T>` 的事件聚合与异步读取
- `AGUISseWriter`：将 `AGUIEvent` 序列化为 `data: {json}\n\n` 输出

## 使用场景

- API 层收到 Agent 事件后，投影为 `AGUIEvent` 并通过 SSE 推送给前端
- 作为协议层被 `Aevatar.Hosts.Api` 引用

## 依赖

- `Microsoft.AspNetCore.App`（FrameworkReference）
