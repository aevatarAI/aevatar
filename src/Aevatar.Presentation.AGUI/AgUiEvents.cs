// ─────────────────────────────────────────────────────────────
// AG-UI 事件定义 — 标准化 Agent ↔ UI 事件流协议
//
// 核心 12 种事件类型，对齐 AG-UI Protocol。
// 业务扩展通过 CustomEvent 承载，不污染核心协议。
// JSON 序列化使用 camelCase，Timestamp 为 Unix 毫秒。
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Presentation.AGUI;

// ─── 基类 ───

public abstract record AgUiEvent
{
    public abstract string Type { get; }
    public long? Timestamp { get; init; }
}

// ─── Run 生命周期 ───

public sealed record RunStartedEvent : AgUiEvent
{
    public override string Type => "RUN_STARTED";
    public required string ThreadId { get; init; }
    public required string RunId { get; init; }
}

public sealed record RunFinishedEvent : AgUiEvent
{
    public override string Type => "RUN_FINISHED";
    public required string ThreadId { get; init; }
    public required string RunId { get; init; }
    public object? Result { get; init; }
}

public sealed record RunErrorEvent : AgUiEvent
{
    public override string Type => "RUN_ERROR";
    public required string Message { get; init; }
    public string? Code { get; init; }
}

// ─── Step 生命周期 ───

public sealed record StepStartedEvent : AgUiEvent
{
    public override string Type => "STEP_STARTED";
    public required string StepName { get; init; }
}

public sealed record StepFinishedEvent : AgUiEvent
{
    public override string Type => "STEP_FINISHED";
    public required string StepName { get; init; }
}

// ─── 文本消息流式 ───

public sealed record TextMessageStartEvent : AgUiEvent
{
    public override string Type => "TEXT_MESSAGE_START";
    public required string MessageId { get; init; }
    public required string Role { get; init; }
}

public sealed record TextMessageContentEvent : AgUiEvent
{
    public override string Type => "TEXT_MESSAGE_CONTENT";
    public required string MessageId { get; init; }
    public required string Delta { get; init; }
}

public sealed record TextMessageEndEvent : AgUiEvent
{
    public override string Type => "TEXT_MESSAGE_END";
    public required string MessageId { get; init; }
}

// ─── 状态同步 ───

public sealed record StateSnapshotEvent : AgUiEvent
{
    public override string Type => "STATE_SNAPSHOT";
    public required object Snapshot { get; init; }
}

// ─── 工具调用 ───

public sealed record ToolCallStartEvent : AgUiEvent
{
    public override string Type => "TOOL_CALL_START";
    public required string ToolCallId { get; init; }
    public required string ToolName { get; init; }
}

public sealed record ToolCallEndEvent : AgUiEvent
{
    public override string Type => "TOOL_CALL_END";
    public required string ToolCallId { get; init; }
    public string? Result { get; init; }
}

// ─── 扩展点 ───

public sealed record CustomEvent : AgUiEvent
{
    public override string Type => "CUSTOM";
    public required string Name { get; init; }
    public object? Value { get; init; }
}
