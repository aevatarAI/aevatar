using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Tools;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Workflow.Abstractions;

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
    private readonly IRemoteSkillAccessTokenResolver? _remoteAccessTokenResolver;
    private readonly ISkillWorkflowMountPort _workflowMountPort;
    private readonly IScopeWorkflowCommandPort? _scopeWorkflowCommandPort;

    public UseSkillTool(
        LocalSkillCatalog localCatalog,
        IRemoteSkillFetcher? remoteFetcher = null,
        ISkillWorkflowMountPort? workflowMountPort = null,
        IScopeWorkflowCommandPort? scopeWorkflowCommandPort = null,
        IRemoteSkillAccessTokenResolver? remoteAccessTokenResolver = null)
    {
        _localCatalog = localCatalog;
        _remoteFetcher = remoteFetcher;
        _remoteAccessTokenResolver = remoteAccessTokenResolver;
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
              "description": "When true, mount the skill's workflow YAML bundles into the current scope as callable workflows. Omit or set false to load instructions without changing workflows."
            }
          },
          "required": ["skill"]
        }
        """;

    public ToolPresentationDescriptor Presentation =>
        ToolPresentationDescriptors.Skill(
            Name,
            "Use skill",
            Description,
            skillName: string.Empty,
            source: "local-or-remote");

    public ToolPresentationDescriptor ResolvePresentation(string argumentsJson)
    {
        var requestedSkillName = ParseArguments(argumentsJson).SkillName.Trim();
        if (string.IsNullOrWhiteSpace(requestedSkillName))
            return Presentation;

        if (_localCatalog.TryGet(requestedSkillName, out var localSkill) && localSkill != null)
        {
            return ToolPresentationDescriptors.Skill(
                Name,
                localSkill.Name,
                localSkill.Description,
                localSkill.Name,
                source: "local");
        }

        return ToolPresentationDescriptors.Skill(
            Name,
            requestedSkillName,
            Description,
            requestedSkillName,
            source: "local-or-remote");
    }

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;

    public bool? RequiresApproval(string argumentsJson) => false;

    public AgentToolCallSafety GetCallSafety(string argumentsJson)
    {
        var mountsWorkflows = ParseArguments(argumentsJson).MountWorkflows == true;
        return new AgentToolCallSafety(
            RequiresApproval: false,
            IsReadOnly: !mountsWorkflows,
            IsDestructive: false);
    }

    public AgentToolReceipt? CreateResultReceipt(
        string callId,
        string toolName,
        string argumentsJson,
        string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("result_type", out var resultType) ||
                !string.Equals(resultType.GetString(), "skill_load", StringComparison.Ordinal) ||
                !root.TryGetProperty("status", out var statusValue) ||
                statusValue.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("loaded", out var loadedValue) ||
                loadedValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            var status = statusValue.GetString() ?? string.Empty;
            if (!loadedValue.GetBoolean() || !string.Equals(status, "success", StringComparison.Ordinal))
                return ErrorReceipt(callId, toolName, LoadFailureCode(status), "The skill could not be loaded.");

            if (ParseArguments(argumentsJson).MountWorkflows != true)
                return SuccessReceipt(callId, toolName);

            if (!root.TryGetProperty("workflow_mount", out var workflowMount) ||
                workflowMount.ValueKind != JsonValueKind.Object)
            {
                return ErrorReceipt(
                    callId,
                    toolName,
                    "USE_SKILL_MOUNT_RESULT_INVALID",
                    "Skill workflow mounting returned an invalid result.");
            }

            var mounted = workflowMount.TryGetProperty("mounted", out var mountedValue) &&
                          mountedValue.ValueKind == JsonValueKind.True;
            var accepted = workflowMount.TryGetProperty("accepted", out var acceptedValue) &&
                           acceptedValue.ValueKind == JsonValueKind.True;
            var succeeded = workflowMount.TryGetProperty("success", out var successValue) &&
                            successValue.ValueKind == JsonValueKind.True;
            if (mounted || accepted && succeeded)
                return SuccessReceipt(callId, toolName, sideEffectKind: "workflow.mount");

            var mountStatus = workflowMount.TryGetProperty("status", out var mountStatusValue) &&
                              mountStatusValue.ValueKind == JsonValueKind.String
                ? mountStatusValue.GetString()
                : null;
            return ErrorReceipt(
                callId,
                toolName,
                MountFailureCode(mountStatus),
                "Skill workflow mounting failed.");
        }
        catch (JsonException)
        {
            return null;
        }
    }

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
                mountWorkflows: ShouldMountWorkflows(requestedMountWorkflows),
                ct: ct);

        if (_remoteFetcher != null)
        {
            var token = _remoteAccessTokenResolver is null
                ? AgentToolRequestContext.NyxIdAccessToken
                : await _remoteAccessTokenResolver.ResolveAsync(skillName, ct).ConfigureAwait(false);
            if (_remoteAccessTokenResolver is not null && string.IsNullOrWhiteSpace(token))
            {
                return BuildLoadResult(
                    skillName: skillName,
                    loaded: false,
                    error: "Remote skill access is unavailable for the current caller.",
                    status: "access_denied",
                    text: BuildErrorWithAvailableSkills(
                        $"Remote skill '{skillName}' could not be loaded for the current caller."));
            }

            if (!string.IsNullOrWhiteSpace(token))
            {
                try
                {
                    skill = await _remoteFetcher.FetchSkillAsync(token.Trim(), skillName, ct);
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
                        mountWorkflows: ShouldMountWorkflows(requestedMountWorkflows),
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

        var callerId = ResolveCapabilityCallerId();
        if (string.IsNullOrWhiteSpace(callerId))
        {
            return new SkillWorkflowMountResult(
                Status: "missing_identity",
                Mounted: false,
                Workflows: [],
                Message: "Workflow mounting skipped because authenticated caller identity is missing from the request context.");
        }

        try
        {
            return await _workflowMountPort.MountAsync(
                new SkillWorkflowMountRequest(scopeId.Trim(), token.Trim(), skill.Workflows)
                {
                    CallerId = callerId.Trim(),
                },
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

        var callerId = ResolveCapabilityCallerId();
        if (string.IsNullOrWhiteSpace(callerId))
            return BuildScopeWorkflowMountError(
                "missing_identity",
                "Workflow mounting skipped because authenticated caller identity is missing from the request context.",
                "authenticated caller identity not available in request context");

        callerId = callerId.Trim();
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
                    DisplayName: workflow.WorkflowId.Trim(),
                    InlineWorkflowYamls: BuildInlineWorkflowYamls(workflowYamls))
                {
                    CapabilityAdmission = new WorkflowCapabilityAdmissionContext(
                        callerId,
                        NyxIdCallerCredentialSelection.SourceReadableUserBearerOrNull(
                            AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(
                                AgentToolRequestContext.Current?.Credentials)),
                        AgentToolRequestContext.NyxIdOrgToken),
                },
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

    private static string ResolveCapabilityCallerId()
    {
        var authority = AgentToolRequestContext.NyxIdAuthority;
        return authority.IsComplete
            ? authority.ExternalUserId!.Trim()
            : string.Empty;
    }

    private static UseSkillArguments ParseArguments(string argumentsJson)
    {
        string skillName = "";
        string args = "";
        bool? mountWorkflows = null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new UseSkillArguments("", "", null);

            if (doc.RootElement.TryGetProperty("skill", out var s) && s.ValueKind == JsonValueKind.String)
                skillName = s.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.String)
                args = a.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("mount_workflows", out var m) &&
                (m.ValueKind == JsonValueKind.True || m.ValueKind == JsonValueKind.False))
            {
                mountWorkflows = m.GetBoolean();
            }
        }
        catch (JsonException)
        {
            return new UseSkillArguments("", "", null);
        }

        return new UseSkillArguments(skillName, args, mountWorkflows);
    }

    private static bool ShouldMountWorkflows(bool? requestedMountWorkflows) =>
        requestedMountWorkflows == true;

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

        if (skill.Workflows.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Workflow Templates");
            sb.AppendLine();
            sb.AppendLine("These workflow YAMLs are Ornn skill templates/import sources, not runnable scope workflows by themselves. Mount or import them through the Scope Workflow command path before starting a run. Use the inline `workflow_yamls` fallback only when the Mounted Workflows section says mounting was unavailable.");

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
        sb.AppendLine("Workflow mount/import commands were accepted for dispatch through the Scope Workflow command path; read models may still be propagating before the workflows are page-visible or runnable.");
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
        sb.AppendLine("Workflow templates were not mounted. Treat any inline workflow YAMLs above as unmounted templates/import sources, not as page-visible runnable scope workflows.");
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

    private AgentToolReceipt SuccessReceipt(
        string callId,
        string toolName,
        string sideEffectKind = "") =>
        new()
        {
            CallId = callId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? Name : toolName,
            Status = AgentToolReceiptStatus.Success,
            ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
            SideEffectKind = sideEffectKind,
        };

    private AgentToolReceipt ErrorReceipt(
        string callId,
        string toolName,
        string errorCode,
        string errorMessage) =>
        new()
        {
            CallId = callId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? Name : toolName,
            Status = AgentToolReceiptStatus.Error,
            ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            ResultJson = JsonSerializer.Serialize(new { error = errorCode, message = errorMessage }),
        };

    private static string LoadFailureCode(string status) =>
        status switch
        {
            "access_denied" => "USE_SKILL_ACCESS_DENIED",
            "not_found" => "USE_SKILL_NOT_FOUND",
            _ => "USE_SKILL_LOAD_FAILED",
        };

    private static string MountFailureCode(string? status) =>
        status switch
        {
            "missing_scope" => "USE_SKILL_MOUNT_MISSING_SCOPE",
            "missing_identity" => "USE_SKILL_MOUNT_MISSING_IDENTITY",
            "no_workflows" => "USE_SKILL_MOUNT_NO_WORKFLOWS",
            "invalid_workflow" => "USE_SKILL_MOUNT_INVALID_WORKFLOW",
            "not_available" => "USE_SKILL_MOUNT_NOT_AVAILABLE",
            _ => "USE_SKILL_MOUNT_FAILED",
        };

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
