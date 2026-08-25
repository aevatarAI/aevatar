using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Tools;
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

    static UseSkillTool()
    {
        s_jsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
    }

    private readonly LocalSkillCatalog _localCatalog;
    private readonly IRemoteSkillFetcher? _remoteFetcher;
    private readonly IRemoteSkillAccessTokenResolver? _remoteAccessTokenResolver;
    private readonly ISkillWorkflowMountPort _workflowMountPort;

    public UseSkillTool(
        LocalSkillCatalog localCatalog,
        IRemoteSkillFetcher? remoteFetcher = null,
        ISkillWorkflowMountPort? workflowMountPort = null,
        IRemoteSkillAccessTokenResolver? remoteAccessTokenResolver = null)
    {
        _localCatalog = localCatalog;
        _remoteFetcher = remoteFetcher;
        _remoteAccessTokenResolver = remoteAccessTokenResolver;
        _workflowMountPort = workflowMountPort ?? new NoOpSkillWorkflowMountPort();
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
            },
            "workflow_mount_confirmation_token": {
              "type": "string",
              "description": "Exact opaque confirmation_token returned by a prior read-only mount preview. Supplying it with mount_workflows=true makes the call approval-gated; prefer this over copying confirmation objects."
            },
            "workflow_mount_confirmations": {
              "type": "array",
              "description": "Legacy exact confirmation objects returned by a prior read-only mount preview. New calls should use workflow_mount_confirmation_token.",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "workflow_id": { "type": "string" },
                  "revision_id": { "type": "string" },
                  "workflow_bundle_digest": { "type": "string" },
                  "explicit_requests": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "properties": {
                        "call_site_id": { "type": "string" },
                        "request_contract_digest": { "type": "string" },
                        "attested_risk": {
                          "type": "string",
                          "enum": ["read_only", "write", "destructive"]
                        }
                      },
                      "required": ["call_site_id", "request_contract_digest", "attested_risk"]
                    }
                  }
                },
                "required": ["workflow_id", "revision_id", "workflow_bundle_digest", "explicit_requests"]
              }
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

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

    public bool? RequiresApproval(string argumentsJson)
    {
        var arguments = ParseArguments(argumentsJson);
        return arguments.MountWorkflows == true && arguments.HasMountConfirmation;
    }

    public AgentToolCallSafety GetCallSafety(string argumentsJson)
    {
        var arguments = ParseArguments(argumentsJson);
        var approvedMountRequested = arguments.MountWorkflows == true && arguments.HasMountConfirmation;
        return new AgentToolCallSafety(
            RequiresApproval: approvedMountRequested,
            IsReadOnly: !approvedMountRequested,
            IsDestructive: false);
    }

    public AgentToolReceipt? CreateResultReceipt(
        string callId,
        string toolName,
        string argumentsJson,
        string resultJson)
    {
        var arguments = ParseArguments(argumentsJson);
        var sideEffectKind = arguments.MountWorkflows == true && arguments.HasMountConfirmation
            ? "workflow.mount"
            : string.Empty;
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
            {
                return ErrorReceipt(
                    callId,
                    toolName,
                    LoadFailureCode(status),
                    "The skill could not be loaded.",
                    arguments.SkillName,
                    sideEffectKind);
            }

            if (arguments.MountWorkflows != true)
                return SuccessReceipt(callId, toolName, arguments.SkillName);

            if (!root.TryGetProperty("workflow_mount", out var workflowMount) ||
                workflowMount.ValueKind != JsonValueKind.Object)
            {
                return ErrorReceipt(
                    callId,
                    toolName,
                    "USE_SKILL_MOUNT_RESULT_INVALID",
                    "Skill workflow mounting returned an invalid result.",
                    arguments.SkillName,
                    sideEffectKind);
            }

            if (IsCanonicalMountObserved(workflowMount))
            {
                return SuccessReceipt(
                    callId,
                    toolName,
                    arguments.SkillName,
                    sideEffectKind,
                    AgentToolReceiptMutationStage.ReadModelObserved);
            }

            var mountStatus = workflowMount.TryGetProperty("status", out var mountStatusValue) &&
                              mountStatusValue.ValueKind == JsonValueKind.String
                ? mountStatusValue.GetString()
                : null;
            if (string.Equals(mountStatus, "confirmation_required", StringComparison.Ordinal))
                return SuccessReceipt(callId, toolName, arguments.SkillName);

            var failureCode = workflowMount.TryGetProperty("failure_code", out var failureCodeValue) &&
                              failureCodeValue.ValueKind == JsonValueKind.String
                ? failureCodeValue.GetString()
                : null;
            return ErrorReceipt(
                callId,
                toolName,
                string.IsNullOrWhiteSpace(failureCode) ? MountFailureCode(mountStatus) : failureCode,
                "Skill workflow mounting failed.",
                arguments.SkillName,
                sideEffectKind,
                string.Equals(mountStatus, "mount_observation_unavailable", StringComparison.Ordinal)
                    ? AgentToolReceiptMutationStage.Accepted
                    : AgentToolReceiptMutationStage.Unspecified);
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
                workflowMountConfirmationToken: arguments.WorkflowMountConfirmationToken,
                workflowMountConfirmations: arguments.WorkflowMountConfirmations,
                ct: ct);

        if (_remoteFetcher != null)
        {
            string? token;
            if (_remoteAccessTokenResolver is null)
            {
                token = AgentToolRequestContext.NyxIdAccessToken;
            }
            else
            {
                var resolution = await _remoteAccessTokenResolver
                    .ResolveAsync(skillName, ct)
                    .ConfigureAwait(false);
                if (!resolution.Succeeded)
                {
                    var guidance = RemoteSkillAccessTokenFailureMessage.Build(resolution.FailureKind);
                    return BuildLoadResult(
                        skillName: skillName,
                        loaded: false,
                        error: guidance,
                        status: "access_denied",
                        text: BuildErrorWithAvailableSkills(
                            $"Remote skill '{skillName}' could not be loaded for the current caller. {guidance}"));
                }

                token = resolution.AccessToken;
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
                        workflowMountConfirmationToken: arguments.WorkflowMountConfirmationToken,
                        workflowMountConfirmations: arguments.WorkflowMountConfirmations,
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
        string workflowMountConfirmationToken,
        IReadOnlyList<SkillWorkflowMountConfirmation> workflowMountConfirmations,
        CancellationToken ct)
    {
        object? workflowMount = null;
        var renderedText = text;
        if (loaded && mountWorkflows)
        {
            var mountRenderResult = await BuildWorkflowMountRenderResultAsync(
                skill,
                workflowMountConfirmationToken,
                workflowMountConfirmations,
                ct);
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
        string workflowMountConfirmationToken,
        IReadOnlyList<SkillWorkflowMountConfirmation> workflowMountConfirmations,
        CancellationToken ct)
    {
        var workflowMount = await TryMountWorkflowsAsync(
            skill,
            workflowMountConfirmationToken,
            workflowMountConfirmations,
            ct);
        return new WorkflowMountRenderResult(
            workflowMount,
            BuildMountedWorkflowsSummary(workflowMount));
    }

    private async Task<SkillWorkflowMountResult> TryMountWorkflowsAsync(
        SkillDefinition? skill,
        string workflowMountConfirmationToken,
        IReadOnlyList<SkillWorkflowMountConfirmation> workflowMountConfirmations,
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

        var token = AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(
            AgentToolRequestContext.Current?.Credentials);
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
                new SkillWorkflowMountRequest(
                    scopeId.Trim(),
                    token.Trim(),
                    skill.Workflows,
                    workflowMountConfirmations)
                {
                    CallerId = callerId.Trim(),
                    ConfirmationToken = workflowMountConfirmationToken,
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
        string workflowMountConfirmationToken = "";
        IReadOnlyList<SkillWorkflowMountConfirmation> workflowMountConfirmations = [];

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new UseSkillArguments("", "", null, "", []);

            if (doc.RootElement.TryGetProperty("skill", out var s) && s.ValueKind == JsonValueKind.String)
                skillName = s.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.String)
                args = a.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("mount_workflows", out var m) &&
                (m.ValueKind == JsonValueKind.True || m.ValueKind == JsonValueKind.False))
            {
                mountWorkflows = m.GetBoolean();
            }
            if (doc.RootElement.TryGetProperty("workflow_mount_confirmation_token", out var token) &&
                token.ValueKind == JsonValueKind.String)
            {
                workflowMountConfirmationToken = token.GetString()?.Trim() ?? string.Empty;
            }
            if (doc.RootElement.TryGetProperty("workflow_mount_confirmations", out var confirmations) &&
                confirmations.ValueKind == JsonValueKind.Array)
            {
                workflowMountConfirmations =
                    JsonSerializer.Deserialize<SkillWorkflowMountConfirmation[]>(
                        confirmations.GetRawText(),
                        s_jsonOptions) ?? [];
            }
        }
        catch (JsonException)
        {
            return new UseSkillArguments("", "", null, "", []);
        }

        return new UseSkillArguments(
            skillName,
            args,
            mountWorkflows,
            workflowMountConfirmationToken,
            workflowMountConfirmations);
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
        string skillName,
        string sideEffectKind = "",
        AgentToolReceiptMutationStage mutationStage = AgentToolReceiptMutationStage.Unspecified) =>
        new()
        {
            CallId = callId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? Name : toolName,
            Status = AgentToolReceiptStatus.Success,
            ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
            Effect = string.IsNullOrWhiteSpace(sideEffectKind)
                ? AgentToolReceiptEffect.ReadOnly
                : AgentToolReceiptEffect.Mutating,
            SideEffectKind = sideEffectKind,
            MutationStage = mutationStage,
            SubjectKind = string.IsNullOrWhiteSpace(skillName) ? string.Empty : "ornn.skill",
            SubjectId = skillName?.Trim() ?? string.Empty,
        };

    private AgentToolReceipt ErrorReceipt(
        string callId,
        string toolName,
        string errorCode,
        string errorMessage,
        string skillName,
        string sideEffectKind,
        AgentToolReceiptMutationStage mutationStage = AgentToolReceiptMutationStage.Unspecified) =>
        new()
        {
            CallId = callId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? Name : toolName,
            Status = AgentToolReceiptStatus.Error,
            ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
            Effect = string.IsNullOrWhiteSpace(sideEffectKind)
                ? AgentToolReceiptEffect.ReadOnly
                : AgentToolReceiptEffect.Mutating,
            SideEffectKind = sideEffectKind,
            MutationStage = mutationStage,
            SubjectKind = string.IsNullOrWhiteSpace(skillName) ? string.Empty : "ornn.skill",
            SubjectId = skillName?.Trim() ?? string.Empty,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            ResultJson = JsonSerializer.Serialize(new { error = errorCode, message = errorMessage }),
        };

    private static bool IsCanonicalMountObserved(JsonElement workflowMount) =>
        workflowMount.TryGetProperty("mounted", out var mountedValue) &&
        mountedValue.ValueKind == JsonValueKind.True &&
        workflowMount.TryGetProperty("read_model_observed", out var observedValue) &&
        observedValue.ValueKind == JsonValueKind.True;

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

    private sealed record WorkflowMountRenderResult(
        object Payload,
        string Text);

    private readonly record struct UseSkillArguments(
        string SkillName,
        string Args,
        bool? MountWorkflows,
        string WorkflowMountConfirmationToken,
        IReadOnlyList<SkillWorkflowMountConfirmation> WorkflowMountConfirmations)
    {
        public bool HasMountConfirmation =>
            !string.IsNullOrWhiteSpace(WorkflowMountConfirmationToken) || WorkflowMountConfirmations.Count > 0;
    }

    private static readonly JsonSerializerOptions SnakeCaseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };
}
