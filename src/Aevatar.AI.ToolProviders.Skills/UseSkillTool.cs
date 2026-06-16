using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;

namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>
/// Unified skill loading tool for local and remote skills.
/// </summary>
// Refactor (iter27/cluster-027-skill-registry-remote-skill-process-state):
//   Old pattern: SkillRegistry mixed local and remote registrations and cached remote skills
//   in process memory for five minutes, breaking read/write separation and user-token isolation.
//   New principle: use a local-only LocalSkillCatalog; every remote use_skill call fetches
//   through IRemoteSkillFetcher with the current token and does not cache process facts.
public sealed class UseSkillTool : IAgentTool
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private readonly LocalSkillCatalog _localCatalog;
    private readonly IRemoteSkillFetcher? _remoteFetcher;
    private readonly ISkillWorkflowMountPort _workflowMountPort;
    private readonly IScopeWorkflowCommandPort? _scopeWorkflowCommandPort;

    public UseSkillTool(
        LocalSkillCatalog localCatalog,
        IRemoteSkillFetcher? remoteFetcher = null,
        ISkillWorkflowMountPort? workflowMountPort = null,
        IScopeWorkflowCommandPort? scopeWorkflowCommandPort = null)
    {
        _localCatalog = localCatalog;
        _remoteFetcher = remoteFetcher;
        _workflowMountPort = workflowMountPort ?? new NoOpSkillWorkflowMountPort();
        _scopeWorkflowCommandPort = scopeWorkflowCommandPort;
    }

    public UseSkillTool(
        LocalSkillCatalog localCatalog,
        IRemoteSkillFetcher? remoteFetcher,
        IScopeWorkflowCommandPort scopeWorkflowCommandPort)
        : this(localCatalog, remoteFetcher, workflowMountPort: null, scopeWorkflowCommandPort: scopeWorkflowCommandPort)
    {
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
            "mount_workflows": {
              "type": "boolean",
              "description": "When true, mount the skill's workflow YAML bundles into the current scope as callable workflows. When omitted, hosts with workflow mounting support mount workflow skills automatically."
            }
          },
          "required": ["skill"]
        }
        """;

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;

    public bool? RequiresApproval(string argumentsJson) => false;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var arguments = ParseArguments(argumentsJson);
        var skillName = arguments.SkillName;
        var args = arguments.Args;
        var requestedMountWorkflows = arguments.MountWorkflows;

        if (string.IsNullOrWhiteSpace(skillName))
            return BuildLoadResult(
                skillName: null,
                loaded: false,
                error: "skill name is required.",
                status: "error",
                text: BuildErrorWithAvailableSkills("Error: skill name is required."));

        // Resolve the requested skill.
        SkillDefinition? skill = null;

        // Refactor (iter27/cluster-027-skill-registry-remote-skill-process-state):
        //   Old pattern: SkillRegistry mixed local and remote registrations and cached remote skills
        //   in process memory for five minutes, breaking read/write separation and user-token isolation.
        //   New principle: use a local-only LocalSkillCatalog; every remote use_skill call fetches
        //   through IRemoteSkillFetcher with the current token and does not cache process facts.
        if (_localCatalog.TryGet(skillName, out skill) && skill != null)
            return await BuildLoadResultAsync(
                skillName: skill.Name,
                loaded: true,
                error: null,
                status: "success",
                text: BuildSkillResponse(skill, args),
                skill: skill,
                mountWorkflows: ShouldMountWorkflows(skill, requestedMountWorkflows),
                ct: ct);

        if (_remoteFetcher != null)
        {
            var token = AgentToolRequestContext.NyxIdAccessToken;
            if (!string.IsNullOrWhiteSpace(token))
            {
                try
                {
                    skill = await _remoteFetcher.FetchSkillAsync(token, skillName, ct);
                }
                catch (RemoteSkillFetchException ex)
                {
                    return BuildLoadResult(
                        skillName: skillName,
                        loaded: false,
                        error: ex.Message,
                        status: ex.FailureKind == RemoteSkillFetchFailureKind.AccessDenied ? "access_denied" : "error",
                        text: BuildErrorWithAvailableSkills($"Error loading skill '{skillName}': {ex.Message}"));
                }

                if (skill != null)
                {
                    return await BuildLoadResultAsync(
                        skillName: skill.Name,
                        loaded: true,
                        error: null,
                        status: "success",
                        text: BuildSkillResponse(skill, args),
                        skill: skill,
                        mountWorkflows: ShouldMountWorkflows(skill, requestedMountWorkflows),
                        ct: ct);
                }
            }
        }

        return BuildLoadResult(
            skillName: skillName,
            loaded: false,
            error: $"Skill '{skillName}' not found.",
            status: "not_found",
            text: BuildErrorWithAvailableSkills($"Skill '{skillName}' not found."));
    }

    private async Task<string> BuildLoadResultAsync(
        string? skillName,
        bool loaded,
        string? error,
        string status,
        string text,
        SkillDefinition? skill,
        bool mountWorkflows,
        CancellationToken ct)
    {
        object? workflowMount = null;
        var renderedText = text;
        if (loaded && mountWorkflows)
        {
            var mountRenderResult = await BuildWorkflowMountRenderResultAsync(skill, ct);
            workflowMount = mountRenderResult.Payload;
            if (!string.IsNullOrWhiteSpace(mountRenderResult.Text))
                renderedText = string.Concat(text, Environment.NewLine, mountRenderResult.Text);
        }

        return BuildLoadResult(
            skillName,
            loaded,
            error,
            status,
            renderedText,
            workflowMount);
    }

    private async Task<WorkflowMountRenderResult> BuildWorkflowMountRenderResultAsync(
        SkillDefinition? skill,
        CancellationToken ct)
    {
        if (_workflowMountPort is not NoOpSkillWorkflowMountPort)
        {
            var workflowMount = await TryMountWorkflowsAsync(skill, ct);
            return new WorkflowMountRenderResult(
                workflowMount,
                BuildMountedWorkflowsSummary(workflowMount));
        }

        return await TryMountWorkflowsViaScopeCommandPortAsync(skill, ct);
    }

    private async Task<SkillWorkflowMountResult> TryMountWorkflowsAsync(
        SkillDefinition? skill,
        CancellationToken ct)
    {
        if (skill == null || skill.Workflows.Count == 0)
        {
            return new SkillWorkflowMountResult(
                Status: "no_workflows",
                Mounted: false,
                Workflows: [],
                Message: "The skill does not expose workflow YAML bundles.");
        }

        var scopeId = AgentToolRequestContext.ScopeId;
        if (string.IsNullOrWhiteSpace(scopeId))
        {
            return new SkillWorkflowMountResult(
                Status: "missing_scope",
                Mounted: false,
                Workflows: [],
                Message: "Workflow mounting skipped because scope_id is missing from the request context.");
        }

        var token = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return new SkillWorkflowMountResult(
                Status: "missing_identity",
                Mounted: false,
                Workflows: [],
                Message: "Workflow mounting skipped because nyxid access token is missing from the request context.");
        }

        try
        {
            return await _workflowMountPort.MountAsync(
                new SkillWorkflowMountRequest(scopeId.Trim(), token.Trim(), skill.Workflows),
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SkillWorkflowMountResult(
                Status: "mount_failed",
                Mounted: false,
                Workflows: [],
                Message: $"Workflow mounting failed: {ex.GetType().Name}.");
        }
    }

    private async Task<WorkflowMountRenderResult> TryMountWorkflowsViaScopeCommandPortAsync(
        SkillDefinition? skill,
        CancellationToken ct)
    {
        if (skill == null || skill.Workflows.Count == 0)
            return BuildScopeWorkflowMountError(
                "no_workflows",
                "The skill does not expose workflow YAML bundles.",
                "skill has no workflow descriptors to mount");

        var scopeId = AgentToolRequestContext.ScopeId;
        if (string.IsNullOrWhiteSpace(scopeId))
            return BuildScopeWorkflowMountError(
                "missing_scope",
                "Workflow mounting skipped because scope_id is missing from the request context.",
                "scope_id not available in request context");

        if (_scopeWorkflowCommandPort == null)
            return BuildScopeWorkflowMountError(
                "not_available",
                "Workflow mounting is not available in this host.",
                "scope workflow command port is not available in this host");

        var mountedPayloads = new List<object>(skill.Workflows.Count);
        var mountedWorkflows = new List<MountedSkillWorkflow>(skill.Workflows.Count);
        foreach (var workflow in skill.Workflows)
        {
            if (string.IsNullOrWhiteSpace(workflow.WorkflowId))
                return BuildScopeWorkflowMountError(
                    "invalid_workflow",
                    "Workflow mounting skipped because the skill contains a workflow descriptor without a workflow_id.",
                    "skill workflow descriptor has no workflow_id");

            var workflowYamls = workflow.WorkflowYamls
                .Where(yaml => !string.IsNullOrWhiteSpace(yaml))
                .ToArray();
            if (workflowYamls.Length == 0)
                return BuildScopeWorkflowMountError(
                    "invalid_workflow",
                    $"Workflow mounting skipped because skill workflow '{workflow.WorkflowId}' has no workflow YAML.",
                    $"skill workflow '{workflow.WorkflowId}' has no workflow YAML");

            var upsertResult = await _scopeWorkflowCommandPort.UpsertAsync(
                new ScopeWorkflowUpsertRequest(
                    scopeId.Trim(),
                    workflow.WorkflowId.Trim(),
                    workflowYamls[0],
                    WorkflowName: workflow.WorkflowId.Trim(),
                    DisplayName: workflow.WorkflowId.Trim(),
                    InlineWorkflowYamls: BuildInlineWorkflowYamls(workflowYamls)),
                ct);

            mountedPayloads.Add(ToMountedWorkflowPayload(upsertResult));
            mountedWorkflows.Add(new MountedSkillWorkflow(
                workflow.WorkflowId.Trim(),
                upsertResult.WorkflowId,
                "chat",
                upsertResult.RevisionId));
        }

        var workflowMount = new SkillWorkflowMountResult(
            Status: "mounted",
            Mounted: mountedWorkflows.Count > 0,
            Workflows: mountedWorkflows,
            Message: mountedWorkflows.Count > 0
                ? "Mounted skill workflows into the current scope."
                : "No skill workflows were mounted.");

        return new WorkflowMountRenderResult(
            new
            {
                success = true,
                accepted = true,
                workflows = mountedPayloads,
            },
            BuildMountedWorkflowsPayload(mountedPayloads));
    }

    private static UseSkillArguments ParseArguments(string argumentsJson)
    {
        string skillName = "";
        string args = "";
        bool? mountWorkflows = null;

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

    private bool ShouldMountWorkflows(SkillDefinition skill, bool? requestedMountWorkflows) =>
        requestedMountWorkflows ??
        skill.Workflows.Count > 0 &&
        (_workflowMountPort is not NoOpSkillWorkflowMountPort || _scopeWorkflowCommandPort is not null);

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

        // Replace argument placeholders.
        var instructions = skill.Instructions;
        if (!string.IsNullOrEmpty(args))
        {
            instructions = instructions.Replace("$ARGUMENTS", args);

            // Support positional arguments $0, $1, ...
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
            sb.AppendLine("Call `aevatar_start_workflow` with `workflow_id` after the workflow is mounted. Use `workflow_yamls` only if the Mounted Workflows section says mounting was unavailable.");

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

        if (skill.Scripts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## script_compile/script_execute Handoff");
            sb.AppendLine();
            sb.AppendLine("Call `script_compile` with the inline script package before calling `script_execute` for the user task.");

            foreach (var script in skill.Scripts)
            {
                var compilePayload = new
                {
                    script_id = script.ScriptId,
                    source_files = script.SourceFiles,
                    proto_files = script.ProtoFiles,
                    entry_behavior_type_name = script.EntryBehaviorTypeName,
                };

                var executePayload = new
                {
                    script_id = script.ScriptId,
                    input = "Use the current user request and skill arguments.",
                };

                sb.AppendLine();
                sb.AppendLine("### script_compile");
                sb.AppendLine("```json");
                sb.AppendLine(JsonSerializer.Serialize(compilePayload, new JsonSerializerOptions { WriteIndented = true }));
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine("### script_execute");
                sb.AppendLine("```json");
                sb.AppendLine(JsonSerializer.Serialize(executePayload, new JsonSerializerOptions { WriteIndented = true }));
                sb.AppendLine("```");
            }
        }

        // Attach associated files.
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

    private static string BuildMountedWorkflowsSummary(SkillWorkflowMountResult workflowMount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Mounted Workflows");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(workflowMount.Message))
        {
            sb.AppendLine(workflowMount.Message);
            sb.AppendLine();
        }

        sb.AppendLine("```json");
        sb.AppendLine(JsonSerializer.Serialize(workflowMount, SnakeCaseJson));
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

    private static string BuildLoadResult(
        string? skillName,
        bool loaded,
        string? error,
        string status,
        string text,
        object? workflowMount = null)
    {
        return JsonSerializer.Serialize(new
        {
            result_type = "skill_load",
            status,
            skill_name = skillName,
            loaded,
            error,
            http_status = (int?)null,
            text,
            workflow_mount = workflowMount,
        }, s_jsonOptions);
    }

    private static WorkflowMountRenderResult BuildScopeWorkflowMountError(
        string status,
        string message,
        string renderMessage) =>
        new(
            new
            {
                status,
                success = false,
                accepted = false,
                mounted = false,
                error = message,
            },
            BuildMountedWorkflowsError(renderMessage));

    private sealed record WorkflowMountRenderResult(
        object Payload,
        string Text);

    private readonly record struct UseSkillArguments(string SkillName, string Args, bool? MountWorkflows);

    private static readonly JsonSerializerOptions SnakeCaseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };
}
