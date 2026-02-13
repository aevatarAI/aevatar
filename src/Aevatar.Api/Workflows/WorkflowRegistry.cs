// ─────────────────────────────────────────────────────────────
// WorkflowRegistry — 工作流名称 → YAML 注册表
//
// 从指定目录加载 .yaml 文件，文件名（无扩展）作为 workflow 名称。
// 也支持通过 Register 手动注册（测试 / 内嵌场景）。
// ─────────────────────────────────────────────────────────────

using System.Collections.Concurrent;

namespace Aevatar.Api.Workflows;

/// <summary>
/// 工作流 YAML 注册表。按名称查询 workflow 定义。
/// </summary>
public sealed class WorkflowRegistry
{
    private readonly ConcurrentDictionary<string, string> _workflows = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>手动注册一个 workflow。</summary>
    public void Register(string name, string yaml) => _workflows[name] = yaml;

    /// <summary>按名称获取 YAML，不存在返回 null。</summary>
    public string? GetYaml(string name) =>
        _workflows.GetValueOrDefault(name);

    /// <summary>获取所有已注册的 workflow 名称。</summary>
    public IReadOnlyList<string> GetNames() => _workflows.Keys.ToList();

    /// <summary>
    /// 从目录加载所有 .yaml / .yml 文件。
    /// 文件名（不含扩展名）作为 workflow 名称。
    /// </summary>
    public int LoadFromDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath)) return 0;

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(directoryPath, "*.*")
                     .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                                 f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var yaml = File.ReadAllText(file);
            _workflows[name] = yaml;
            count++;
        }
        return count;
    }
}
