// ─────────────────────────────────────────────────────────────
// PromptTemplate — 提示词模板
// 支持 {{variable}} 插值和预定义 examples
// ─────────────────────────────────────────────────────────────

namespace Aevatar.AI.Prompt;

/// <summary>
/// 提示词模板。支持变量插值 {{name}} 和示例。
/// </summary>
public sealed class PromptTemplate
{
    /// <summary>模板内容（含 {{variable}} 占位符）。</summary>
    public required string Content { get; init; }

    /// <summary>预定义变量默认值。</summary>
    public Dictionary<string, string> Defaults { get; init; } = [];

    /// <summary>Few-shot 示例列表。</summary>
    public List<PromptExample> Examples { get; init; } = [];

    /// <summary>
    /// 渲染模板：替换变量 → 追加 examples。
    /// </summary>
    /// <param name="variables">运行时变量。</param>
    /// <returns>渲染后的完整 prompt。</returns>
    public string Render(IReadOnlyDictionary<string, string>? variables = null)
    {
        var result = Content;

        // 先用默认值替换
        foreach (var (key, val) in Defaults)
            result = result.Replace($"{{{{{key}}}}}", val);

        // 再用运行时变量覆盖
        if (variables != null)
            foreach (var (key, val) in variables)
                result = result.Replace($"{{{{{key}}}}}", val);

        // 追加 examples
        if (Examples.Count > 0)
        {
            result += "\n\n## Examples\n";
            foreach (var ex in Examples)
                result += $"\nUser: {ex.Input}\nAssistant: {ex.Output}\n";
        }

        return result;
    }
}

/// <summary>Few-shot 示例。</summary>
public sealed class PromptExample
{
    /// <summary>示例输入。</summary>
    public required string Input { get; init; }

    /// <summary>示例输出。</summary>
    public required string Output { get; init; }
}
