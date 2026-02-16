// ─────────────────────────────────────────────────────────────
// SecretManager — API Key / 密钥管理
// 支持从环境变量、文件、配置中解析 ${VAR} 引用
// ─────────────────────────────────────────────────────────────

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aevatar.AI.Core.Secrets;

/// <summary>
/// 密钥管理器。解析 ${VAR} 引用，从环境变量或 secrets 文件获取。
/// </summary>
public sealed partial class SecretManager
{
    private readonly Dictionary<string, string> _secrets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 从 secrets JSON 文件加载密钥。
    /// 文件格式：{"OPENAI_API_KEY": "sk-...", "DEEPSEEK_API_KEY": "..."}
    /// </summary>
    public SecretManager LoadFromFile(string filePath)
    {
        var expandedPath = ExpandPath(filePath);
        if (!File.Exists(expandedPath)) return this;

        var json = File.ReadAllText(expandedPath);
        var secrets = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        if (secrets != null)
            foreach (var (k, v) in secrets)
                _secrets[k] = v;
        return this;
    }

    /// <summary>
    /// 从环境变量加载（按前缀过滤）。
    /// </summary>
    public SecretManager LoadFromEnvironment(string prefix = "AEVATAR_")
    {
        foreach (var entry in Environment.GetEnvironmentVariables())
        {
            if (entry is System.Collections.DictionaryEntry de &&
                de.Key is string key && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                de.Value is string value)
            {
                _secrets[key] = value;
            }
        }
        return this;
    }

    /// <summary>
    /// 解析字符串中的 ${VAR} 引用。
    /// 查找顺序：内部 secrets → 环境变量。
    /// </summary>
    public string Resolve(string input)
    {
        if (string.IsNullOrEmpty(input) || !input.Contains("${")) return input;

        return VarPattern().Replace(input, match =>
        {
            var varName = match.Groups[1].Value;
            if (_secrets.TryGetValue(varName, out var val)) return val;
            return Environment.GetEnvironmentVariable(varName) ?? match.Value;
        });
    }

    /// <summary>直接获取密钥。</summary>
    public string? Get(string key) =>
        _secrets.TryGetValue(key, out var val) ? val : Environment.GetEnvironmentVariable(key);

    /// <summary>手动设置密钥。</summary>
    public SecretManager Set(string key, string value) { _secrets[key] = value; return this; }

    private static string ExpandPath(string path)
    {
        if (path.StartsWith('~'))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        return Environment.ExpandEnvironmentVariables(path);
    }

    [GeneratedRegex(@"\$\{(\w+)\}")]
    private static partial Regex VarPattern();
}
