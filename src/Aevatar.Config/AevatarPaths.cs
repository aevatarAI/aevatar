// ─────────────────────────────────────────────────────────────
// AevatarPaths — ~/.aevatar/ 目录路径管理
//
// 统一管理所有子目录路径：
// secrets.json / config.json / mcp.json /
// agents/ / workflows/ / skills/ / tools/ / sessions/ / logs/
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Config;

/// <summary>
/// ~/.aevatar/ 目录路径管理。解析根目录和所有子目录。
/// 支持通过环境变量 AEVATAR_HOME 自定义根目录。
/// </summary>
public static class AevatarPaths
{
    // ─── 环境变量 ───

    /// <summary>自定义根目录环境变量名。</summary>
    public const string HomeEnv = "AEVATAR_HOME";

    /// <summary>密钥文件路径环境变量名。</summary>
    public const string SecretsPathEnv = "AEVATAR_SECRETS_PATH";

    // ─── 根目录 ───

    /// <summary>
    /// 解析 Aevatar 配置根目录。
    /// 优先级：AEVATAR_HOME 环境变量 → ~/.aevatar
    /// </summary>
    public static string Root
    {
        get
        {
            var envRoot = Environment.GetEnvironmentVariable(HomeEnv);
            if (!string.IsNullOrEmpty(envRoot))
                return ExpandPath(envRoot);

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".aevatar");
        }
    }

    // ─── 配置文件路径 ───

    /// <summary>plaintext 配置文件（非敏感配置）。</summary>
    public static string ConfigJson => Path.Combine(Root, "config.json");

    /// <summary>密钥文件（API Key 等敏感信息）。</summary>
    public static string SecretsJson
    {
        get
        {
            var envPath = Environment.GetEnvironmentVariable(SecretsPathEnv);
            return !string.IsNullOrEmpty(envPath) ? ExpandPath(envPath) : Path.Combine(Root, "secrets.json");
        }
    }

    /// <summary>MCP 服务器配置文件（Cursor 兼容格式）。</summary>
    public static string McpJson => Path.Combine(Root, "mcp.json");

    /// <summary>Connector 配置文件（命名连接器 + 安全策略）。</summary>
    public static string ConnectorsJson => Path.Combine(Root, "connectors.json");

    // ─── 子目录路径 ───

    /// <summary>Agent YAML 配置目录。</summary>
    public static string Agents => Path.Combine(Root, "agents");

    /// <summary>Workflow YAML 定义目录。</summary>
    public static string Workflows => Path.Combine(Root, "workflows");

    /// <summary>Skills 文档目录（含 SKILL.md）。</summary>
    public static string Skills => Path.Combine(Root, "skills");

    /// <summary>Tools 目录（.NET file-based 工具）。</summary>
    public static string Tools => Path.Combine(Root, "tools");

    /// <summary>Session 持久化目录。</summary>
    public static string Sessions => Path.Combine(Root, "sessions");

    /// <summary>日志目录。</summary>
    public static string Logs => Path.Combine(Root, "logs");

    /// <summary>MCP 服务器配置目录。</summary>
    public static string Mcp => Path.Combine(Root, "mcp");

    /// <summary>
    /// 仓库根目录下的 workflows 路径（用于从 repo 根加载 YAML，用户无需拷贝到 ~/.aevatar）。
    /// 解析方式：从当前目录或程序基目录向上查找含 aevatar.slnx、.git 或 Directory.Build.props 的目录，取其 workflows 子目录；未找到则用当前目录的 workflows。
    /// </summary>
    public static string RepoRootWorkflows => Path.Combine(RepoRoot, "workflows");

    /// <summary>
    /// 解析仓库根目录。向上查找包含 aevatar.slnx、.git 或 Directory.Build.props 的目录；未找到则返回当前目录。
    /// </summary>
    public static string RepoRoot => GetRepoRoot();

    private static string GetRepoRoot()
    {
        var candidates = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
            Path.GetDirectoryName(AppContext.BaseDirectory) ?? "",
            Path.GetDirectoryName(Path.GetDirectoryName(AppContext.BaseDirectory) ?? "") ?? "",
        };

        foreach (var start in candidates.Distinct().Where(d => !string.IsNullOrEmpty(d)))
        {
            var dir = start;
            for (var i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                if (File.Exists(Path.Combine(dir, "aevatar.slnx")) ||
                    Directory.Exists(Path.Combine(dir, ".git")) ||
                    File.Exists(Path.Combine(dir, "Directory.Build.props")))
                    return dir;
                var parent = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(parent) || parent == dir) break;
                dir = parent;
            }
        }

        return Directory.GetCurrentDirectory();
    }

    // ─── 工具方法 ───

    /// <summary>确保所有子目录已创建。</summary>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Agents);
        Directory.CreateDirectory(Workflows);
        Directory.CreateDirectory(Skills);
        Directory.CreateDirectory(Tools);
        Directory.CreateDirectory(Sessions);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Mcp);
    }

    /// <summary>获取指定 Agent 的 YAML 配置文件路径。</summary>
    public static string AgentYaml(string agentId) =>
        Path.Combine(Agents, $"{agentId}.yaml");

    /// <summary>获取指定 Workflow 的 YAML 文件路径。</summary>
    public static string WorkflowYaml(string workflowName) =>
        Path.Combine(Workflows, $"{workflowName}.yaml");

    /// <summary>展开 ~ 和环境变量。</summary>
    private static string ExpandPath(string path)
    {
        if (path.StartsWith('~'))
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[2..]);
        return Environment.ExpandEnvironmentVariables(path);
    }
}