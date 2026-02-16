using Aevatar.Context.Abstractions;

namespace Aevatar.Context.Retrieval;

/// <summary>
/// 意图分析产生的类型化查询。
/// 每个 TypedQuery 指定了查询文本、目标上下文类型和优先级。
/// </summary>
public sealed record TypedQuery(
    string Query,
    ContextType ContextType,
    string Intent,
    int Priority);
