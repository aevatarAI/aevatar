// ─────────────────────────────────────────────────────────────
// WorkflowVariables — 工作流变量系统
//
// assign / transform / conditional / while 等原语都依赖变量。
// 变量在工作流执行期间动态创建和修改。
// 支持点号路径访问（如 "research.output"）。
// ─────────────────────────────────────────────────────────────

using System.Text.Json;

namespace Aevatar.Workflows.Core.Primitives;

/// <summary>
/// 工作流运行时变量存储。支持点号路径和 JSON 序列化。
/// </summary>
public sealed class WorkflowVariables
{
    private readonly Dictionary<string, string> _vars = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>设置变量值。</summary>
    public void Set(string key, string value) => _vars[key] = value;

    /// <summary>获取变量值。支持点号路径（如 "step1.output"），不存在返回 null。</summary>
    public string? Get(string key)
    {
        if (_vars.TryGetValue(key, out var val)) return val;

        // 尝试点号路径：把 "a.b" 解释为取变量 "a" 中的 JSON 字段 "b"
        var dotIdx = key.IndexOf('.');
        if (dotIdx <= 0) return null;

        var root = key[..dotIdx];
        var path = key[(dotIdx + 1)..];
        if (!_vars.TryGetValue(root, out var json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var element = doc.RootElement;
            foreach (var part in path.Split('.'))
            {
                if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(part, out var child))
                    element = child;
                else
                    return null;
            }
            return element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
        }
        catch { return null; }
    }

    /// <summary>获取所有变量（快照）。</summary>
    public IReadOnlyDictionary<string, string> GetAll() => new Dictionary<string, string>(_vars);

    /// <summary>合并另一组变量（覆盖同名变量）。</summary>
    public void Merge(IReadOnlyDictionary<string, string> other)
    {
        foreach (var (k, v) in other) _vars[k] = v;
    }

    /// <summary>清除所有变量。</summary>
    public void Clear() => _vars.Clear();

    /// <summary>变量数量。</summary>
    public int Count => _vars.Count;

    /// <summary>在模板字符串中替换变量引用 {{var_name}}。</summary>
    public string Interpolate(string template)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains("{{")) return template;

        var result = template;
        foreach (var (key, value) in _vars)
            result = result.Replace($"{{{{{key}}}}}", value);
        return result;
    }
}
