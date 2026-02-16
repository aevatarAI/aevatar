// ─────────────────────────────────────────────────────────────
// IEventRouteEvaluator — 事件路由求值器
//
// 从 EventEnvelope 中提取用于路由匹配的属性：
// - event.type  → payload TypeUrl 的短名
// - event.step_type → StepRequestEvent 的 step_type 字段
//
// Cognitive 层可注册自定义 evaluator 提取 step_type。
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;

namespace Aevatar.AI.Core.Routing;

/// <summary>
/// 事件路由求值器。从 EventEnvelope 中提取路由匹配属性。
/// </summary>
public interface IEventRouteEvaluator
{
    /// <summary>提取事件类型名（payload TypeUrl 的短名部分）。</summary>
    bool TryGetEventType(EventEnvelope envelope, out string typeName);

    /// <summary>提取步骤类型（仅 StepRequestEvent 有，其他返回 false）。</summary>
    bool TryGetStepType(EventEnvelope envelope, out string stepType);
}

/// <summary>
/// 默认事件路由求值器。从 TypeUrl 提取事件类型名。
/// </summary>
public sealed class DefaultEventRouteEvaluator : IEventRouteEvaluator
{
    /// <summary>单例。</summary>
    public static readonly DefaultEventRouteEvaluator Instance = new();

    /// <inheritdoc />
    public bool TryGetEventType(EventEnvelope envelope, out string typeName)
    {
        typeName = "";
        var typeUrl = envelope.Payload?.TypeUrl;
        if (string.IsNullOrEmpty(typeUrl)) return false;

        // TypeUrl 格式：type.googleapis.com/package.MessageName → 取 MessageName
        var idx = typeUrl.LastIndexOf('.');
        if (idx < 0) idx = typeUrl.LastIndexOf('/');
        typeName = idx >= 0 ? typeUrl[(idx + 1)..] : typeUrl;
        return typeName.Length > 0;
    }

    /// <inheritdoc />
    public bool TryGetStepType(EventEnvelope envelope, out string stepType)
    {
        stepType = "";
        // 默认实现不知道 StepRequestEvent 的结构
        // Cognitive 层可注册自定义 evaluator 处理
        return false;
    }
}
