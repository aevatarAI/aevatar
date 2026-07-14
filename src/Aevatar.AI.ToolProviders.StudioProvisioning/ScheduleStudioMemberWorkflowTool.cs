using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Authorization;

namespace Aevatar.AI.ToolProviders.StudioProvisioning;

internal sealed class ScheduleStudioMemberWorkflowTool : IAgentTool, IAgentToolCapabilityDescriptor
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly IStudioMemberWorkflowSchedulePort _schedulePort;

    public ScheduleStudioMemberWorkflowTool(IStudioMemberWorkflowSchedulePort schedulePort)
    {
        _schedulePort = schedulePort ?? throw new ArgumentNullException(nameof(schedulePort));
    }

    public string Name => "aevatar_schedule_member_workflow";

    public string Description =>
        "Create or update a schedule for an existing Studio member's already-bound workflow in the caller's current Aevatar scope. " +
        "Use this when the user already has or just created an m-... Studio member and asks to schedule that same member workflow. " +
        "Supply member_id, schedule_cron, and schedule_timezone, plus optional prompt/display_name. " +
        "This tool does not create standalone wf-... workflow members, does not accept workflow_yaml, and does not bind or rebind workflows. " +
        "Do not provide scope_id, published_service_id, service_id, or tokens because scope, service identity, and caller subject are resolved from platform context.";

    public string ParametersSchema => """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "member_id": {
              "type": "string",
              "description": "Existing Studio member id whose already-bound workflow should be scheduled. Required."
            },
            "schedule_cron": {
              "type": "string",
              "description": "Standard 5-field cron expression (minute hour day-of-month month day-of-week), e.g. '0 9 * * *'. Required."
            },
            "schedule_timezone": {
              "type": "string",
              "description": "IANA timezone used to evaluate schedule_cron, e.g. 'Asia/Shanghai'. Required."
            },
            "prompt": {
              "type": "string",
              "description": "Optional prompt passed to the workflow chat endpoint when the schedule fires."
            },
            "display_name": {
              "type": "string",
              "description": "Optional display name for the schedule."
            }
          },
          "required": ["member_id", "schedule_cron", "schedule_timezone"]
        }
        """;

    public ToolApprovalMode ApprovalMode => ToolApprovalPolicies.CreateScopedResource;

    public IReadOnlyCollection<string> Capabilities { get; } =
        [AgentToolCapabilities.ExcludeFromDirectChannelChat];

    public bool IsReadOnly => false;
    public bool IsDestructive => false;
    public string SideEffectKind => "studio.member.workflow.schedule";

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var scopeId = Normalize(AgentToolRequestContext.ScopeId);
        if (scopeId is null)
        {
            return ErrorJson(
                "caller_scope_unavailable",
                "scope_id is required in AgentToolRequestContext. The local Studio member workflow schedule tool uses the caller scope from the tool execution context.");
        }

        var ownerSubject = Normalize(AgentToolRequestContext.OwnerSubject);
        if (ownerSubject is null)
        {
            return ErrorJson(
                "caller_subject_unavailable",
                "owner subject is required in AgentToolRequestContext so the schedule can re-mint caller NyxID credentials when it fires.");
        }
        var bindingId = Normalize(AgentToolRequestContext.SenderBindingId);
        var nyxUserId = Normalize(AgentToolRequestContext.SenderNyxUserId) ?? ownerSubject;
        var subjectPlatform = Normalize(AgentToolRequestContext.ChannelPlatform) ?? "nyxid";
        var subjectExternalUserId = Normalize(AgentToolRequestContext.ChannelSenderId) ?? ownerSubject;
        if (bindingId is null && !string.Equals(subjectPlatform, "nyxid", StringComparison.Ordinal))
            return ErrorJson("authenticated_owner_context_unavailable", "A verified NyxID binding is required to authorize a Team schedule.");
        bindingId ??= $"nyxid:{nyxUserId}";
        var provisioningBearerToken = Normalize(AgentToolRequestContext.NyxIdAccessToken);
        if (provisioningBearerToken is null)
            return ErrorJson("caller_credential_unavailable", "A current NyxID credential is required to create the schedule credential.");

        ScheduleStudioMemberWorkflowArguments? args;
        try
        {
            var unknownArgument = FindUnknownArgument(argumentsJson);
            if (unknownArgument is not null)
                return ErrorJson("invalid_arguments", $"Unknown argument: {unknownArgument}");

            args = JsonSerializer.Deserialize<ScheduleStudioMemberWorkflowArguments>(argumentsJson, s_jsonOptions);
        }
        catch (JsonException ex)
        {
            return ErrorJson("invalid_arguments", $"Could not parse tool arguments: {ex.Message}");
        }

        if (args is null)
            return ErrorJson("invalid_arguments", "Tool arguments are required.");

        var memberId = Normalize(args.MemberId);
        if (memberId is null)
            return ErrorJson("invalid_arguments", "member_id is required.");

        var scheduleCron = Normalize(args.ScheduleCron);
        if (scheduleCron is null)
            return ErrorJson("invalid_arguments", "schedule_cron is required.");

        var scheduleTimezone = Normalize(args.ScheduleTimezone);
        if (scheduleTimezone is null)
            return ErrorJson("invalid_arguments", "schedule_timezone is required.");

        var request = new StudioMemberWorkflowScheduleRequest(
            ScopeId: scopeId,
            MemberId: memberId,
            ScheduleCron: scheduleCron,
            ScheduleTimezone: scheduleTimezone,
            AuthenticatedOwner: new AuthenticatedNyxIdOwnerContext
            {
                Owner = new NyxIdCatalogOwnerIdentity
                {
                    Authority = "nyxid",
                    OwnerKind = NyxIdCatalogOwnerKind.Personal,
                    OwnerSubject = nyxUserId,
                },
                SubjectPlatform = subjectPlatform,
                SubjectTenant = Normalize(AgentToolRequestContext.ChannelRegistrationScopeId) ?? string.Empty,
                SubjectExternalUserId = subjectExternalUserId,
                VerifiedBindingId = bindingId,
            },
            CredentialExpiresAtUtc: DateTimeOffset.UtcNow.AddDays(30))
        {
            Prompt = Normalize(args.Prompt),
            DisplayName = Normalize(args.DisplayName),
            ProvisioningBearerToken = provisioningBearerToken,
        };

        try
        {
            var preflight = await _schedulePort.PreflightAsync(request, ct);
            if (!preflight.Success)
                return ErrorJson(preflight.FailureCode.ToString(), preflight.Detail);
            var result = await _schedulePort.CreateAsync(request, preflight.Plan!.PermissionDigest, ct);
            return JsonSerializer.Serialize(
                new ScheduleStudioMemberWorkflowResultJson(
                    Success: result.Success,
                    Status: result.Status,
                    ScopeId: result.ScopeId,
                    MemberId: result.MemberId,
                    ScheduleId: result.ScheduleId,
                    PublishedServiceId: result.PublishedServiceId,
                    ObservatoryUrl: result.ObservatoryUrl),
                s_jsonOptions);
        }
        catch (InvalidOperationException ex)
        {
            return ErrorJson("invalid_arguments", ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ErrorJson("member_workflow_schedule_failed", $"Studio member workflow schedule failed: {ex.GetType().Name}");
        }
    }

    private static string ErrorJson(string code, string message) =>
        JsonSerializer.Serialize(new ScheduleStudioMemberWorkflowErrorJson(
            new ScheduleStudioMemberWorkflowErrorBody(code, message)),
            s_jsonOptions);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? FindUnknownArgument(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name is not "member_id" and not "schedule_cron" and not "schedule_timezone"
                and not "prompt" and not "display_name")
                return property.Name;
        }

        return null;
    }

    private sealed record ScheduleStudioMemberWorkflowArguments(
        [property: JsonPropertyName("member_id")] string? MemberId,
        [property: JsonPropertyName("schedule_cron")] string? ScheduleCron,
        [property: JsonPropertyName("schedule_timezone")] string? ScheduleTimezone,
        [property: JsonPropertyName("prompt")] string? Prompt,
        [property: JsonPropertyName("display_name")] string? DisplayName);

    private sealed record ScheduleStudioMemberWorkflowResultJson(
        bool Success,
        string Status,
        string ScopeId,
        string MemberId,
        string ScheduleId,
        string PublishedServiceId,
        string ObservatoryUrl);

    private sealed record ScheduleStudioMemberWorkflowErrorJson(ScheduleStudioMemberWorkflowErrorBody Error);

    private sealed record ScheduleStudioMemberWorkflowErrorBody(string Code, string Message);
}
