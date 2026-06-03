// ─────────────────────────────────────────────────────────────
// UseSkillTool — 统一技能调用工具
// LLM 通过此工具调用任何技能（本地或远程）
// 学习 Claude Code SkillTool 模式：单一入口 + 懒加载
// ─────────────────────────────────────────────────────────────

using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;

namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>
/// 统一技能调用工具。替代散装的 skill_xxx 工具和 ornn_use_skill 工具。
/// LLM 调用 use_skill(skill="名称") → 返回技能指令内容。
/// </summary>
// Refactor (iter27/cluster-027-skill-registry-remote-skill-process-state):
//   Old pattern: SkillRegistry 暴露混合 local + remote skill 注册并用 5min TTL process-wide cache 缓存 remote skill,违反读写分离 + 多用户 token 共享 + 进程内事实状态
//   New principle: 删 SkillRegistry + TTL tests + 5min cache;新建 local-only LocalSkillCatalog;remote skill 每次 use_skill 调用 IRemoteSkillFetcher.FetchSkillAsync(currentToken, ...) 不缓存;docs/canon factual sync
public sealed class UseSkillTool : IAgentTool
{
    private readonly LocalSkillCatalog _localCatalog;
    private readonly IRemoteSkillFetcher? _remoteFetcher;
    private readonly IScopeWorkflowCommandPort? _scopeWorkflowCommandPort;

    public UseSkillTool(
        LocalSkillCatalog localCatalog,
        IRemoteSkillFetcher? remoteFetcher = null,
        IScopeWorkflowCommandPort? scopeWorkflowCommandPort = null)
    {
        _localCatalog = localCatalog;
        _remoteFetcher = remoteFetcher;
        _scopeWorkflowCommandPort = scopeWorkflowCommandPort;
    }

    public string Name => "use_skill";

    public string Description =>
        "Load and activate a skill by name. " +
        "Returns the skill's instructions so you can follow them to complete the user's task. " +
        "Proactively use this when a user's request matches a known skill. " +
        "Use ornn_search_skills first to discover skills if you're unsure what's available.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "skill": { "type": "string", "description": "The skill name to invoke" },
            "args": { "type": "string", "description": "Optional arguments for the skill" },
            "mount_workflows": { "type": "boolean", "description": "When true, upsert all workflow YAML descriptors from the skill into the current scope workflow command pipeline" }
          },
          "required": ["skill"]
        }
        """;

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

    public bool? RequiresApproval(string argumentsJson) => ParseArguments(argumentsJson).MountWorkflows;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var arguments = ParseArguments(argumentsJson);
        var skillName = arguments.SkillName;
        var args = arguments.Args;
        var mountWorkflows = arguments.MountWorkflows;

        if (string.IsNullOrWhiteSpace(skillName))
            return BuildErrorWithAvailableSkills("Error: skill name is required.");

        // ─── 查找技能 ───
        SkillDefinition? skill = null;

        // Refactor (iter27/cluster-027-skill-registry-remote-skill-process-state):
        //   Old pattern: SkillRegistry 暴露混合 local + remote skill 注册并用 5min TTL process-wide cache 缓存 remote skill,违反读写分离 + 多用户 token 共享 + 进程内事实状态
        //   New principle: 删 SkillRegistry + TTL tests + 5min cache;新建 local-only LocalSkillCatalog;remote skill 每次 use_skill 调用 IRemoteSkillFetcher.FetchSkillAsync(currentToken, ...) 不缓存;docs/canon factual sync
        if (_localCatalog.TryGet(skillName, out skill) && skill != null)
            return await BuildSkillResponseAsync(skill, args, mountWorkflows, ct);

        if (_remoteFetcher != null)
        {
            var token = AgentToolRequestContext.NyxIdAccessToken;
            if (!string.IsNullOrWhiteSpace(token))
            {
                skill = await _remoteFetcher.FetchSkillAsync(token, skillName, ct);
                if (skill != null)
                {
                    return await BuildSkillResponseAsync(skill, args, mountWorkflows, ct);
                }
            }
        }

        return BuildErrorWithAvailableSkills($"Skill '{skillName}' not found.");
    }

    private static UseSkillArguments ParseArguments(string argumentsJson)
    {
        string skillName = "";
        string args = "";
        var mountWorkflows = false;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("skill", out var s))
                skillName = s.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("args", out var a))
                args = a.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("mount_workflows", out var m) &&
                (m.ValueKind == JsonValueKind.True || m.ValueKind == JsonValueKind.False))
            {
                mountWorkflows = m.GetBoolean();
            }
        }
        catch { /* use defaults */ }

        return new UseSkillArguments(skillName, args, mountWorkflows);
    }

    private async Task<string> BuildSkillResponseAsync(
        SkillDefinition skill,
        string args,
        bool mountWorkflows,
        CancellationToken ct)
    {
        var response = BuildSkillResponse(skill, args);
        if (!mountWorkflows)
            return response;

        var mountedWorkflows = await MountWorkflowsAsync(skill, ct);
        return response + Environment.NewLine + mountedWorkflows;
    }

    private static string BuildSkillResponse(SkillDefinition skill, string args)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {skill.Name}");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(skill.Description))
        {
            sb.AppendLine(skill.Description);
            sb.AppendLine();
        }

        // 替换参数占位符
        var instructions = skill.Instructions;
        if (!string.IsNullOrEmpty(args))
        {
            instructions = instructions.Replace("$ARGUMENTS", args);

            // 支持位置参数 $0, $1, ...
            var argParts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < argParts.Length && i < 10; i++)
                instructions = instructions.Replace($"${i}", argParts[i]);
        }

        sb.AppendLine("## Instructions");
        sb.AppendLine();
        sb.AppendLine(instructions);
        sb.AppendLine();
        sb.AppendLine("## Skill Continuation");
        sb.AppendLine();
        sb.AppendLine(
            "If these instructions leave you blocked by a missing capability, ambiguous workflow step, unavailable service, unknown API contract, repeated tool failure, or any other unsolved dependency, call `ornn_search_skills` with the concrete blocker/task and then `use_skill` the best matching result before trying generic proxy discovery or path guessing. Continue from the newly loaded skill.");

        // Refactor (iter161/cluster-triage-ornn-skill-workflow-tool-signal #1259-first):
        //   Old pattern: Runnable workflow YAML attachments were rendered only as generic Associated Files, leaving aevatar_start_workflow handoff implicit.
        //   New principle: Render an explicit handoff section before generic files, preserving workflow_id and workflow_yamls as single-semantics tool fields.
        if (skill.Workflows.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## aevatar_start_workflow Handoff");
            sb.AppendLine();
            sb.AppendLine("Call `aevatar_start_workflow` with this inline workflow bundle before treating workflow YAMLs as ordinary reference files.");

            foreach (var workflow in skill.Workflows)
            {
                var payload = new
                {
                    workflow_id = workflow.WorkflowId,
                    workflow_yamls = workflow.WorkflowYamls,
                };

                sb.AppendLine();
                sb.AppendLine("```json");
                sb.AppendLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
                sb.AppendLine("```");
            }
        }

        // 附带关联文件
        if (skill.AssociatedFiles is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("## Associated Files");
            sb.AppendLine();
            foreach (var (fileName, content) in skill.AssociatedFiles)
            {
                if (fileName.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                    continue;
                sb.AppendLine($"### {fileName}");
                sb.AppendLine("```");
                sb.AppendLine(content);
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private async Task<string> MountWorkflowsAsync(SkillDefinition skill, CancellationToken ct)
    {
        var scopeId = AgentToolRequestContext.ScopeId;
        if (string.IsNullOrWhiteSpace(scopeId))
            return BuildMountedWorkflowsError("scope_id not available in request context");

        if (_scopeWorkflowCommandPort == null)
            return BuildMountedWorkflowsError("scope workflow command port is not available in this host");

        if (skill.Workflows.Count == 0)
            return BuildMountedWorkflowsError("skill has no workflow descriptors to mount");

        var mounted = new List<object>(skill.Workflows.Count);
        foreach (var workflow in skill.Workflows)
        {
            if (string.IsNullOrWhiteSpace(workflow.WorkflowId))
                return BuildMountedWorkflowsError("skill workflow descriptor has no workflow_id");

            var workflowYamls = workflow.WorkflowYamls
                .Where(yaml => !string.IsNullOrWhiteSpace(yaml))
                .ToArray();
            if (workflowYamls.Length == 0)
                return BuildMountedWorkflowsError($"skill workflow '{workflow.WorkflowId}' has no workflow YAML");

            var result = await _scopeWorkflowCommandPort.UpsertAsync(new ScopeWorkflowUpsertRequest(
                scopeId.Trim(),
                workflow.WorkflowId.Trim(),
                workflowYamls[0],
                WorkflowName: workflow.WorkflowId.Trim(),
                DisplayName: workflow.WorkflowId.Trim(),
                InlineWorkflowYamls: BuildInlineWorkflowYamls(workflowYamls)), ct);

            mounted.Add(ToMountedWorkflowPayload(result));
        }

        return BuildMountedWorkflowsPayload(mounted);
    }

    private static IReadOnlyDictionary<string, string>? BuildInlineWorkflowYamls(IReadOnlyList<string> workflowYamls)
    {
        if (workflowYamls.Count <= 1)
            return null;

        var inlineWorkflowYamls = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < workflowYamls.Count; i++)
            inlineWorkflowYamls[$"workflow_{i}"] = workflowYamls[i];
        return inlineWorkflowYamls;
    }

    private static object ToMountedWorkflowPayload(ScopeWorkflowUpsertResult result) => new
    {
        success = true,
        accepted = true,
        scope_id = result.ScopeId,
        workflow_id = result.WorkflowId,
        service_key = result.ServiceKey,
        revision_id = result.RevisionId,
        expected_actor_id = result.ExpectedActorId,
        expected_deployment_id = result.ExpectedDeploymentId,
        acceptance_stage = result.AcceptanceStage,
        propagation_stage = result.PropagationStage,
        read_model_url = result.ReadModelUrl,
        command_handles = result.CommandHandles,
    };

    private static string BuildMountedWorkflowsPayload(IReadOnlyList<object> mounted)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Mounted Workflows");
        sb.AppendLine();
        sb.AppendLine("Workflow mount commands were accepted for dispatch; read models may still be propagating.");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(JsonSerializer.Serialize(new { workflows = mounted }, SnakeCaseJson));
        sb.AppendLine("```");
        return sb.ToString();
    }

    private static string BuildMountedWorkflowsError(string message)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Mounted Workflows");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(JsonSerializer.Serialize(new
        {
            success = false,
            accepted = false,
            error = message,
        }, SnakeCaseJson));
        sb.AppendLine("```");
        return sb.ToString();
    }

    private string BuildErrorWithAvailableSkills(string errorMessage)
    {
        var sb = new StringBuilder();
        sb.AppendLine(errorMessage);

        var skills = _localCatalog.GetModelInvocable();
        if (skills.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Available skills:");
            foreach (var s in skills)
            {
                sb.Append("- ");
                sb.Append(s.Name);
                if (!string.IsNullOrEmpty(s.Description))
                {
                    sb.Append(": ");
                    sb.Append(s.Description.Length > 100 ? s.Description[..97] + "..." : s.Description);
                }
                sb.AppendLine();
            }
        }

        sb.AppendLine();
        sb.AppendLine("You can also use ornn_search_skills to discover more skills from the user's library.");

        return sb.ToString();
    }

    private readonly record struct UseSkillArguments(string SkillName, string Args, bool MountWorkflows);

    private static readonly JsonSerializerOptions SnakeCaseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };
}
