using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Workflows;

namespace Aevatar.AI.ToolProviders.StudioProvisioning;

/// <summary>
/// <c>aevatar_provision_workflow_schedule</c> — the channel-free, Team-owned,
/// Observatory-delivered analogue of the Lark <c>scheduled_agent_creator</c>. It
/// provisions a runnable workflow (member create inside a confirmed Team → bind
/// inline YAML → <c>ScheduleKind=Workflow</c> scheduled-dispatch) so its recurring
/// runs surface in <c>/workflow/observatory</c>, never in a chat/bot.
///
/// The tool takes ONLY workflow/scheduling inputs from the LLM. The owning scope and
/// caller identity come from the tool execution context (W1 threads
/// <c>Caller.ScopeId</c>/<c>OwnerSubject</c> on the workflow llm_call path; the
/// forwarded NyxID access token remains a boundary input and is not persisted in
/// schedule auth). There are NO
/// channel / Lark / owner / scope / credential inputs, and the result carries no
/// channel/Lark fields — only the Team/member/schedule ids plus Studio and
/// Observatory links.
///
/// Because its outcome lands in the Observatory and never in a chat, it declares the
/// <see cref="AgentToolCapabilities.ExcludeFromDirectChannelChat"/> capability so any
/// direct channel/chat agent filters it out generically (by capability, not by name).
/// </summary>
internal sealed class ProvisionWorkflowScheduleTool : IAgentTool, IAgentToolCapabilityDescriptor
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly IWorkflowScheduleProvisioningPort _provisioningPort;

    public ProvisionWorkflowScheduleTool(IWorkflowScheduleProvisioningPort provisioningPort)
    {
        _provisioningPort = provisioningPort
            ?? throw new ArgumentNullException(nameof(provisioningPort));
    }

    public string Name => "aevatar_provision_workflow_schedule";

    public string Description =>
        "Schedule a runnable Aevatar workflow under a confirmed Studio Team whose recurring runs appear in /workflow/observatory (never a chat/bot). " +
        "Supply team_id, the workflow body inline as workflow_yaml, plus a display_name; the tool creates the member inside that Team, binds the YAML, " +
        "and creates a workflow-kind scheduled dispatch under the caller's scope. Do not call this tool until the user has selected an existing Team or confirmed a new Team. " +
        "The workflow_yaml is validated synchronously before anything is created: an invalid document returns a typed error " +
        "describing the problem and provisions nothing — fix the YAML per the error message and call again. " +
        "Provisioning is idempotent per display_name: calling again with the same display_name re-binds the same member and " +
        "updates (and re-enables) its schedule instead of creating duplicates, so retries are safe. That also means an " +
        "existing display_name is REPLACED by a re-provision — reuse a display_name only to retry or update the same " +
        "automation, and pick a distinct display_name for a different automation. " +
        "Provide schedule_cron + schedule_timezone for a recurring monitor; omit them for a single near-future demo run (unless run_immediately is false). " +
        "This is the Observatory-delivered alternative to scheduled_agent_creator: use it for workflow automation instead of publishing a prose skill or scheduling a bot delivery. " +
        "Returns the Team id, member id, schedule id, Studio member workflow URL, and Observatory link; the scope and caller identity are taken from the session context, not from arguments. " +
        "A status of 'accepted' means the YAML was validated and the bind was dispatched — the bind and any run complete " +
        "asynchronously, so verify the run in the Observatory before reporting the workflow as running.";

    public string ParametersSchema => $$"""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "team_id": {
              "type": "string",
              "description": "Confirmed existing Studio team id that will own the provisioned workflow member. Required; ask the user to select or create a Team before calling this tool."
            },
            "workflow_yaml": {
              "type": "string",
              "description": "The workflow definition body as inline YAML. Required. Schema (snake_case keys): the authorable top-level keys are {{WorkflowYamlRootSchema.FormatAuthorableRootFields()}}. name is required. roles is a list of {id, name, system_prompt, ...}; steps is a list of {id, type, target_role, parameters, next, branches, ...}. Do NOT use top-level keys from other workflow dialects such as {{WorkflowYamlRootSchema.FormatUnsupportedDialectRootFields()}} — the parser rejects unknown keys and the tool returns the parse error."
            },
            "display_name": {
              "type": "string",
              "description": "Human-readable name for the provisioned workflow member. Required."
            },
            "prompt": {
              "type": "string",
              "description": "Optional user prompt the scheduled run starts from."
            },
            "schedule_cron": {
              "type": "string",
              "description": "Standard 5-field cron expression (minute hour day-of-month month day-of-week) for a recurring monitor. Omit for a single demo run. Seconds fields are not supported."
            },
            "schedule_timezone": {
              "type": "string",
              "description": "IANA timezone name for schedule_cron evaluation (e.g. Asia/Shanghai). Required only when schedule_cron is set."
            },
            "run_immediately": {
              "type": "boolean",
              "description": "When true (default), a demo run fires shortly after the bind even without a recurring cron. Set false with no cron for a bind-only provision (no run)."
            }
          },
          "required": ["team_id", "workflow_yaml", "display_name"]
        }
        """;

    public ToolApprovalMode ApprovalMode => ToolApprovalPolicies.CreateScopedResource;
    public bool IsReadOnly => false;
    public bool IsDestructive => false;

    // Observatory-delivered, never chat-delivered: declare the generic surface signal so a
    // direct channel/chat agent (e.g. the Lark/NyxID reply path) filters this out by capability
    // rather than by hardcoding the tool name. The workflow allowlist path still selects it.
    public IReadOnlyCollection<string> Capabilities { get; } =
        [AgentToolCapabilities.ExcludeFromDirectChannelChat];

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var scopeId = StudioToolScopeResolver.ResolveOwnerScopeOrCallerScope();
        if (scopeId is null)
        {
            return ErrorJson(
                "caller_scope_unavailable",
                "scope_id is required in AgentToolRequestContext. The studio workflow chat threads the run scope onto the tool caller; a missing scope means the request did not run under a scope.");
        }

        ProvisionWorkflowScheduleArguments? args;
        try
        {
            args = JsonSerializer.Deserialize<ProvisionWorkflowScheduleArguments>(argumentsJson, s_jsonOptions);
        }
        catch (JsonException ex)
        {
            return ErrorJson("invalid_arguments", $"Could not parse tool arguments: {ex.Message}");
        }

        if (args is null)
            return ErrorJson("invalid_arguments", "Tool arguments are required.");

        var teamId = Normalize(args.TeamId);
        if (teamId is null)
            return ErrorJson("invalid_arguments", "team_id is required.");

        var workflowYaml = Normalize(args.WorkflowYaml);
        if (workflowYaml is null)
            return ErrorJson("invalid_arguments", "workflow_yaml is required.");

        var displayName = Normalize(args.DisplayName);
        if (displayName is null)
            return ErrorJson("invalid_arguments", "display_name is required.");

        var typedAuthority = AgentToolRequestContext.NyxIdAuthority;
        var request = new WorkflowScheduleProvisioningRequest(
            ScopeId: scopeId,
            TeamId: teamId,
            DisplayName: displayName,
            WorkflowYaml: workflowYaml)
        {
            Prompt = Normalize(args.Prompt),
            ScheduleCron = Normalize(args.ScheduleCron),
            ScheduleTimezone = Normalize(args.ScheduleTimezone),
            RunImmediately = args.RunImmediately ?? true,
            CallerSubjectPlatform = typedAuthority.IsComplete
                ? Normalize(typedAuthority.Platform) ?? "nyxid"
                : "nyxid",
            CallerSubjectTenant = typedAuthority.IsComplete ? Normalize(typedAuthority.Tenant) : null,
            CallerSubjectExternalUserId = typedAuthority.IsComplete
                ? Normalize(typedAuthority.ExternalUserId)
                : Normalize(AgentToolRequestContext.OwnerSubject),
            CapabilityAdmission = StudioWorkflowCapabilityToolContext.Create(
                ExternalCapabilityExecutionMode.Durable),
        };

        try
        {
            var result = await _provisioningPort.ProvisionAsync(request, ct);
            return JsonSerializer.Serialize(new ProvisionWorkflowScheduleResultJson(
                Status: result.BindingStatus,
                MemberId: result.MemberId,
                ScopeId: result.ScopeId,
                TeamId: result.TeamId,
                ScheduleId: result.ScheduleId,
                BindingRunId: result.BindingRunId,
                StudioUrl: result.StudioUrl,
                ObservatoryUrl: result.ObservatoryUrl),
                s_jsonOptions);
        }
        catch (InvalidOperationException ex)
        {
            // The provisioning service surfaces validation failures (missing YAML /
            // display name / scope) as InvalidOperationException; map to a typed
            // tool error rather than letting it bubble as an unstructured failure.
            return ErrorJson("invalid_arguments", ex.Message);
        }
    }

    private static string ErrorJson(string code, string message) =>
        JsonSerializer.Serialize(new ProvisionWorkflowScheduleErrorJson(
            new ProvisionWorkflowScheduleErrorBody(code, message)),
            s_jsonOptions);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ProvisionWorkflowScheduleArguments(
        [property: JsonPropertyName("team_id")] string? TeamId,
        [property: JsonPropertyName("workflow_yaml")] string? WorkflowYaml,
        [property: JsonPropertyName("display_name")] string? DisplayName,
        [property: JsonPropertyName("prompt")] string? Prompt,
        [property: JsonPropertyName("schedule_cron")] string? ScheduleCron,
        [property: JsonPropertyName("schedule_timezone")] string? ScheduleTimezone,
        [property: JsonPropertyName("run_immediately")] bool? RunImmediately);

    private sealed record ProvisionWorkflowScheduleResultJson(
        string Status,
        string MemberId,
        string ScopeId,
        string TeamId,
        string? ScheduleId,
        string? BindingRunId,
        string StudioUrl,
        string ObservatoryUrl);

    private sealed record ProvisionWorkflowScheduleErrorJson(ProvisionWorkflowScheduleErrorBody Error);

    private sealed record ProvisionWorkflowScheduleErrorBody(string Code, string Message);
}
