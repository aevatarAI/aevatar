// ─────────────────────────────────────────────────────────────
// RoutedEventModule — 路由过滤包装器
//
// 包装一个 IEventModule，只有匹配路由规则的事件才传给内部模块。
// 实现了 IRouteBypassModule 的模块不会被此包装器包装。
// ─────────────────────────────────────────────────────────────

using Aevatar;
using Aevatar.EventModules;

namespace Aevatar.AI.Routing;

/// <summary>
/// 路由过滤包装器。只有匹配路由规则且目标模块名一致的事件才传给内部模块。
/// </summary>
public sealed class RoutedEventModule : IEventModule
{
    private readonly IEventModule _inner;
    private readonly EventRoute[] _routes;
    private readonly IEventRouteEvaluator _evaluator;

    public RoutedEventModule(IEventModule inner, EventRoute[] routes, IEventRouteEvaluator? evaluator = null)
    {
        _inner = inner;
        _routes = routes;
        _evaluator = evaluator ?? DefaultEventRouteEvaluator.Instance;
    }

    /// <summary>内部模块名。</summary>
    public string Name => _inner.Name;

    /// <summary>内部模块优先级。</summary>
    public int Priority => _inner.Priority;

    /// <summary>
    /// 路由匹配逻辑：
    /// 1. 内部模块的 CanHandle 必须先通过
    /// 2. 路由规则中必须有一条：target == 本模块名 且 when 条件匹配
    /// 3. 如果没有路由规则，默认通过（兼容无路由配置的场景）
    /// </summary>
    public bool CanHandle(EventEnvelope envelope)
    {
        if (!_inner.CanHandle(envelope)) return false;
        if (_routes.Length == 0) return true;

        return _routes.Any(r =>
            string.Equals(r.TargetModule, Name, StringComparison.OrdinalIgnoreCase) &&
            r.Matches(envelope, _evaluator));
    }

    /// <summary>委托给内部模块处理。</summary>
    public Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
        => _inner.HandleAsync(envelope, ctx, ct);
}
