// ─────────────────────────────────────────────────────────────
// AevatarAgentYamlLoader — Agent YAML 配置加载
//
// 从 ~/.aevatar/agents/ 发现和读取 Agent YAML 文件
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Configuration;

/// <summary>
/// Agent YAML 配置加载器。扫描 ~/.aevatar/agents/ 目录。
/// </summary>
public static class AevatarAgentYamlLoader
{
    /// <summary>
    /// 加载指定 Agent 的 YAML 配置内容。
    /// </summary>
    /// <param name="agentId">Agent ID。</param>
    /// <returns>YAML 字符串，文件不存在返回 null。</returns>
    public static string? LoadAgentYaml(string agentId)
    {
        var path = AevatarPaths.AgentYaml(agentId);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>
    /// 加载指定 Workflow 的 YAML 配置内容。
    /// </summary>
    /// <param name="workflowName">Workflow 名称。</param>
    /// <returns>YAML 字符串，文件不存在返回 null。</returns>
    public static string? LoadWorkflowYaml(string workflowName)
    {
        var path = AevatarPaths.WorkflowYaml(workflowName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>
    /// 发现所有 Agent YAML 文件。
    /// </summary>
    /// <returns>(agentId, filePath) 列表。</returns>
    public static IReadOnlyList<(string AgentId, string FilePath)> DiscoverAgents()
    {
        var dir = AevatarPaths.Agents;
        if (!Directory.Exists(dir)) return [];

        return Directory.GetFiles(dir, "*.yaml")
            .Concat(Directory.GetFiles(dir, "*.yml"))
            .Select(f => (AgentId: Path.GetFileNameWithoutExtension(f), FilePath: f))
            .ToList();
    }

    /// <summary>
    /// 发现所有 Workflow YAML 文件。
    /// </summary>
    public static IReadOnlyList<(string WorkflowName, string FilePath)> DiscoverWorkflows()
    {
        var dir = AevatarPaths.Workflows;
        if (!Directory.Exists(dir)) return [];

        return Directory.GetFiles(dir, "*.yaml")
            .Concat(Directory.GetFiles(dir, "*.yml"))
            .Select(f => (WorkflowName: Path.GetFileNameWithoutExtension(f), FilePath: f))
            .ToList();
    }
}
