// ─────────────────────────────────────────────────────────────
// OrnnRemoteSkillFetcher — 从 Ornn 平台拉取技能
// 实现 IRemoteSkillFetcher，将 Ornn API 响应转换为 SkillDefinition
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.ToolProviders.Skills;

namespace Aevatar.AI.ToolProviders.Ornn;

/// <summary>
/// Ornn 远程技能拉取器。通过 OrnnSkillClient 从 Ornn 平台获取技能。
/// </summary>
public sealed class OrnnRemoteSkillFetcher : IRemoteSkillFetcher
{
    private readonly OrnnSkillClient _client;
    private static readonly char[] PathSeparators = ['/', '\\'];

    public OrnnRemoteSkillFetcher(OrnnSkillClient client) => _client = client;

    public async Task<SkillDefinition?> FetchSkillAsync(
        string accessToken, string nameOrId, CancellationToken ct = default)
    {
        var skill = await _client.GetSkillJsonAsync(accessToken, nameOrId, ct);
        if (skill == null)
            return null;

        // 从 SKILL.md 文件内容中提取 instructions
        var instructions = "";
        Dictionary<string, string>? associatedFiles = null;

        if (skill.Files != null && skill.Files.Count > 0)
        {
            var skillMd = skill.Files
                .FirstOrDefault(f => f.Key.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                .Value;
            if (skillMd is not null)
                instructions = skillMd;

            var others = skill.Files
                .Where(f => !f.Key.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(f => f.Key, f => f.Value);

            if (others.Count > 0)
                associatedFiles = others;
        }

        // 抽出工作流 YAML，从剩余关联文件中剔除
        var extraction = new SkillWorkflowExtractor().ExtractFromFiles(associatedFiles);

        // 尝试从 instructions 解析 frontmatter
        var parser = new SkillFrontmatterParser();
        var parsed = parser.Parse(instructions);
        var workflows = DiscoverWorkflows(skill.Files, parsed.WorkflowEntry);
        if (workflows.Count == 0)
            workflows = extraction.Workflows;
        var scriptExtraction = new SkillScriptExtractor().ExtractFromFiles(
            parsed.Name ?? skill.Name ?? nameOrId,
            parsed.ScriptEntry,
            extraction.RemainingFiles);

        return new SkillDefinition
        {
            Name = parsed.Name ?? skill.Name ?? nameOrId,
            Description = parsed.Description ?? skill.Description ?? "",
            Instructions = parsed.Body,
            Source = SkillSource.Remote,
            RemoteId = nameOrId,
            Arguments = parsed.Arguments,
            WhenToUse = parsed.WhenToUse,
            IsModelInvocable = parsed.IsModelInvocable,
            IsUserInvocable = parsed.IsUserInvocable,
            AssociatedFiles = scriptExtraction.RemainingFiles,
            Workflows = workflows,
            Scripts = scriptExtraction.Scripts,
        };
    }

    private static IReadOnlyList<SkillWorkflowDescriptor> DiscoverWorkflows(
        IReadOnlyDictionary<string, string>? files,
        string? workflowEntry)
    {
        if (files is not { Count: > 0 })
            return [];

        // Refactor (iter161/cluster-triage-ornn-skill-workflow-tool-signal #1259-first):
        //   Old pattern: Ornn workflow YAML files stayed in AssociatedFiles as untyped text, so the model had to infer tool handoff from generic attachments.
        //   New principle: Keep the existing Ornn Files surface, but lift non-empty workflows/*.yaml|*.yml files into a typed skill workflow descriptor for aevatar_start_workflow.
        var workflowFiles = files
            .Where(static file => IsWorkflowYamlPath(file.Key) && !string.IsNullOrWhiteSpace(file.Value))
            .OrderBy(static file => file.Key, StringComparer.Ordinal)
            .ToArray();

        if (workflowFiles.Length == 0)
            return [];

        var defaultWorkflowId = GetWorkflowFileId(workflowFiles[0].Key);
        var workflowId = string.IsNullOrWhiteSpace(workflowEntry)
            ? defaultWorkflowId
            : GetWorkflowFileId(workflowEntry.Trim());
        var orderedWorkflowFiles = OrderWorkflowFilesByEntry(workflowFiles, workflowId);

        return
        [
            new SkillWorkflowDescriptor
            {
                WorkflowId = workflowId,
                WorkflowYamls = orderedWorkflowFiles.Select(static file => file.Value.Trim()).ToArray(),
            }
        ];
    }

    private static IReadOnlyList<KeyValuePair<string, string>> OrderWorkflowFilesByEntry(
        IReadOnlyList<KeyValuePair<string, string>> workflowFiles,
        string workflowId)
    {
        if (string.IsNullOrWhiteSpace(workflowId))
            return workflowFiles;

        var entry = workflowFiles.FirstOrDefault(file =>
            string.Equals(
                GetWorkflowFileId(file.Key),
                workflowId,
                StringComparison.Ordinal));
        if (entry.Key == null)
            return workflowFiles;

        return workflowFiles
            .OrderByDescending(file => string.Equals(file.Key, entry.Key, StringComparison.Ordinal))
            .ThenBy(static file => file.Key, StringComparer.Ordinal)
            .ToArray();
    }

    private static string GetWorkflowFileId(string path)
    {
        var fileName = path.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? path;
        return Path.GetFileNameWithoutExtension(fileName);
    }

    private static bool IsWorkflowYamlPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var normalized = path.Replace('\\', '/');
        if (!normalized.StartsWith("workflows/", StringComparison.Ordinal))
            return false;

        var fileName = normalized.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return fileName is not null &&
               (fileName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase));
    }
}
