using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Workflow.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.ToolProviders.StudioProvisioning;

internal sealed class ScheduleStudioMemberWorkflowTool : IStudioMutationReceiptTool
{
    private const string CredentialProvisioningKind = "dedicated_scheduled_invocation_agent_key";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly IStudioMemberWorkflowSchedulePort _schedulePort;
    private readonly IStudioMemberWorkflowDurableAdmissionPort? _durableAdmissionPort;
    private readonly ILogger<ScheduleStudioMemberWorkflowTool>? _logger;

    public ScheduleStudioMemberWorkflowTool(
        IStudioMemberWorkflowSchedulePort schedulePort,
        ILogger<ScheduleStudioMemberWorkflowTool>? logger = null,
        IStudioMemberWorkflowDurableAdmissionPort? durableAdmissionPort = null)
    {
        _schedulePort = schedulePort ?? throw new ArgumentNullException(nameof(schedulePort));
        _logger = logger;
        _durableAdmissionPort = durableAdmissionPort;
    }

    public string Name => "aevatar_schedule_member_workflow";

    public string Description =>
        "Create or update a schedule for an existing Studio member's already-bound workflow in the caller's current Aevatar scope. " +
        "Use this when the user already has or just created an m-... Studio member and asks to schedule that same member workflow. " +
        "Supply member_id, schedule_cron, and schedule_timezone, plus optional prompt/display_name. " +
        "If the serving revision needs durable admission, this tool may publish a new immutable durable revision and return durable_revision_propagating until that exact revision is invoke-ready in the read model. " +
        "A bind/publish acknowledgement is not invoke-ready evidence. This tool does not create standalone wf-... workflow members and does not accept workflow_yaml. " +
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
              "description": "Optional JSON prompt template passed to the workflow when the schedule fires. Trusted fire-time placeholders are allowed in JSON string values: {{@schedule.run_date}}, {{@schedule.run_year}}, {{@schedule.run_month}}, {{@schedule.days_until_month_end}}, {{@schedule.fire_at_utc}}, and {{@schedule.timezone}}."
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

    public bool IsReadOnly => false;
    public bool IsDestructive => false;
    public string SideEffectKind => "studio.member.workflow.schedule";
    public string SubjectKind => "studio_member_workflow_schedule";
    public string SubjectIdPropertyName => "schedule_id";

    public IReadOnlyList<StudioQueryToolJson.ResultPropertyRequirement> ResultRequirements { get; } = new[]
    {
        StudioQueryToolJson.StringProperty("status"),
        StudioQueryToolJson.StringProperty("scope_id"),
        StudioQueryToolJson.StringProperty("member_id"),
        StudioQueryToolJson.StringProperty("published_service_id"),
        StudioQueryToolJson.StringProperty("observatory_url"),
    };

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var scopeId = StudioToolScopeResolver.ResolveOwnerScopeOrCallerScope();
        if (scopeId is null)
        {
            return ErrorJson(
                "caller_scope_unavailable",
                "scope_id is required in AgentToolRequestContext. The local Studio member workflow schedule tool uses the caller scope from the tool execution context.");
        }

        var authorizationContext = StudioMemberWorkflowScheduleAuthorizationResolver.Resolve();
        if (authorizationContext.Error is { } authorizationError)
        {
            LogAuthorizationResolutionFailure(authorizationError.Code);
            return ErrorJson(
                authorizationError.Code,
                authorizationError.Message);
        }

        var resolvedAuthorization = authorizationContext.Resolved!;

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

        var prompt = Normalize(args.Prompt);
        var displayName = Normalize(args.DisplayName);
        var operationIdentity = TryBuildOperationIdentity(
            scopeId,
            memberId,
            resolvedAuthorization.OwnerSubject);
        if (operationIdentity is null)
        {
            return ErrorJson(
                "operation_identity_unavailable",
                "A trusted idempotency key or request and tool-call identity is required to create a schedule.");
        }

        var request = new StudioMemberWorkflowScheduleRequest(
            ScopeId: scopeId,
            MemberId: memberId,
            ScheduleCron: scheduleCron,
            ScheduleTimezone: scheduleTimezone,
            AuthenticatedOwner: resolvedAuthorization.AuthenticatedOwner)
        {
            OperationId = operationIdentity.OperationId,
            IdempotencyKey = operationIdentity.IdempotencyKey,
            CredentialProvisioningKind = CredentialProvisioningKind,
            Prompt = prompt,
            DisplayName = displayName,
            ProvisioningBearerToken = resolvedAuthorization.ProvisioningBearerToken,
        };

        try
        {
            var preflight = await _schedulePort.PreflightForWriteAsync(request, ct);
            if (!preflight.Success &&
                IsDurableAdmissionFailure(preflight.FailureCode) &&
                _durableAdmissionPort is not null)
            {
                var durableAdmission = await _durableAdmissionPort.AdmitAsync(
                    new StudioMemberWorkflowDurableAdmissionRequest(
                        scopeId,
                        memberId,
                        BuildDurableAdmissionContext(resolvedAuthorization.OwnerSubject)),
                    ct);
                if (!MatchesAdmissionIdentity(durableAdmission, scopeId, memberId))
                {
                    return ErrorJson(
                        "durable_admission_identity_mismatch",
                        "The durable admission result did not match the authenticated Studio member identity.");
                }

                if (durableAdmission.Status == StudioMemberWorkflowDurableAdmissionStatus.RevisionAccepted)
                    return DurableRevisionPropagatingJson(durableAdmission);

                if (!durableAdmission.ReadyForSchedule)
                {
                    return ErrorJson(
                        "durable_admission_status_invalid",
                        "The durable admission result did not provide an invoke-ready revision.");
                }

                request = BuildPinnedScheduleRequest(request, durableAdmission);
                preflight = await _schedulePort.PreflightForWriteAsync(request, ct);
                if (preflight.Success && !MatchesAdmissionTarget(preflight.Plan, durableAdmission))
                {
                    return ErrorJson(
                        "durable_revision_target_mismatch",
                        "The schedule authorization preflight did not resolve the exact durable published service revision.");
                }
            }

            if (!preflight.Success)
                return ErrorJson(preflight.FailureCode.ToString(), preflight.Detail);

            var permissionDigest = Normalize(preflight.Plan?.PermissionDigest);
            var policyVersion = Normalize(preflight.Plan?.CredentialPolicy?.PolicyVersion);
            if (permissionDigest is null || policyVersion is null)
            {
                return ErrorJson(
                    "authorization_plan_invalid",
                    "The authorization preflight plan must include permission_digest and credential_policy.policy_version.");
            }

            var confirmedRequest = request with { ConfirmedPolicyVersion = policyVersion };
            var result = await _schedulePort.CreateAsync(confirmedRequest, permissionDigest, ct);
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
        catch (StudioMemberAutomationProjectionPendingException)
        {
            return ErrorJson(
                "authorization_catalog_projection_pending",
                "The refreshed authorization catalog is still being projected. Retry this request.");
        }
        catch (StudioMemberAutomationCatalogRefreshUnavailableException)
        {
            return ErrorJson(
                "authorization_catalog_refresh_unavailable",
                "The authorization catalog could not be refreshed. Retry this request.");
        }
        catch (StudioMemberAutomationCatalogRouteUnresolvedException ex)
        {
            return ErrorJson(
                "authorization_catalog_route_unresolved",
                ex.Message,
                requiredUserServiceIds: ex.RequiredUserServiceIds);
        }
        catch (StudioMemberAutomationCatalogRefreshSupersededException)
        {
            return ErrorJson(
                "authorization_catalog_refresh_superseded",
                "A newer authorization catalog refresh superseded this request. Retry this request.");
        }
        catch (StudioMemberAutomationPlanConflictException ex)
        {
            return ErrorJson(
                ToPlanConflictCode(ex.Code),
                ToPlanConflictMessage(ex.Code),
                ScheduledAuthorizationPlanMismatchReasons.ToWireValue(ex.AuthorizationPlanMismatchReason));
        }
        catch (Exception ex)
        {
            return ErrorJson("member_workflow_schedule_failed", $"Studio member workflow schedule failed: {ex.GetType().Name}");
        }
    }

    private static WorkflowCapabilityAdmissionContext BuildDurableAdmissionContext(string ownerSubject) =>
        new(
            ownerSubject,
            NyxIdCallerCredentialSelection.SourceReadableUserBearerOrNull(
                AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(
                    AgentToolRequestContext.Current?.Credentials)),
            AgentToolRequestContext.NyxIdOrgToken,
            ExternalCapabilityExecutionMode.Durable);

    private static bool IsDurableAdmissionFailure(
        ScheduledInvocationAuthorizationFailureCode failureCode) =>
        failureCode is ScheduledInvocationAuthorizationFailureCode.DurableAdmissionRequired or
            ScheduledInvocationAuthorizationFailureCode.DurableRequestGrantMismatch;

    private static bool MatchesAdmissionIdentity(
        StudioMemberWorkflowDurableAdmissionResult result,
        string scopeId,
        string memberId) =>
        string.Equals(result.ScopeId, scopeId, StringComparison.Ordinal) &&
        string.Equals(result.MemberId, memberId, StringComparison.Ordinal) &&
        Normalize(result.TeamId) is not null &&
        Normalize(result.WorkflowId) is not null &&
        Normalize(result.PublishedServiceId) is not null &&
        Normalize(result.ServingRevisionId) is not null &&
        Normalize(result.TargetRevisionId) is not null;

    private static bool MatchesAdmissionTarget(
        ScheduledInvocationAuthorizationPlan? plan,
        StudioMemberWorkflowDurableAdmissionResult admission)
    {
        var target = plan?.InvocationTarget?.StudioMember;
        return target is not null &&
               string.Equals(target.ScopeId, admission.ScopeId, StringComparison.Ordinal) &&
               string.Equals(target.TeamId, admission.TeamId, StringComparison.Ordinal) &&
               string.Equals(target.MemberId, admission.MemberId, StringComparison.Ordinal) &&
               string.Equals(target.DraftWorkflowId, admission.WorkflowId, StringComparison.Ordinal) &&
               string.Equals(target.PublishedServiceId, admission.PublishedServiceId, StringComparison.Ordinal) &&
               string.Equals(target.WorkflowRevisionId, admission.TargetRevisionId, StringComparison.Ordinal);
    }

    private static StudioMemberWorkflowScheduleRequest BuildPinnedScheduleRequest(
        StudioMemberWorkflowScheduleRequest request,
        StudioMemberWorkflowDurableAdmissionResult admission) =>
        request with
        {
            TeamId = admission.TeamId,
            AcceptedBinding = new StudioMemberWorkflowAcceptedBindingContext(
                admission.TeamId,
                admission.PublishedServiceId,
                admission.WorkflowId,
                admission.TargetRevisionId),
        };

    private static string DurableRevisionPropagatingJson(
        StudioMemberWorkflowDurableAdmissionResult admission) =>
        JsonSerializer.Serialize(
            new ScheduleStudioMemberWorkflowErrorJson(
                new ScheduleStudioMemberWorkflowErrorBody(
                    "durable_revision_propagating",
                    "The durable workflow bind/publish was accepted, but the exact revision is not yet visible as invoke-ready. Retry after readiness is observed.",
                    ScopeId: admission.ScopeId,
                    TeamId: admission.TeamId,
                    MemberId: admission.MemberId,
                    WorkflowId: admission.WorkflowId,
                    PublishedServiceId: admission.PublishedServiceId,
                    ServingRevisionId: admission.ServingRevisionId,
                    TargetRevisionId: admission.TargetRevisionId,
                    BindingOperation: admission.BindingOperation,
                    BindingStatus: admission.BindingStatus)),
            s_jsonOptions);

    private static string ToPlanConflictCode(string code) => code switch
    {
        "authorization_plan_changed" => "authorization_plan_changed",
        "reauthorization_required" => "reauthorization_required",
        _ => "authorization_conflict",
    };

    private static string ToPlanConflictMessage(string code) => code switch
    {
        "authorization_plan_changed" =>
            "The authorization plan changed. Run schedule preflight again before retrying.",
        "reauthorization_required" =>
            "The schedule requires a fresh authorization review before it can be created.",
        _ => "The schedule request conflicts with the current authorization state.",
    };

    private void LogAuthorizationResolutionFailure(string code)
    {
        if (_logger is null)
            return;

        var typedAuthority = AgentToolRequestContext.NyxIdAuthority;
        _logger.LogWarning(
            "Studio member workflow schedule authorization resolution failed: code={Code} has_typed_authority={HasTypedAuthority} has_binding_id={HasBindingId} has_sender_nyx_user_id={HasSenderNyxUserId} has_sender_tenant={HasSenderTenant} has_channel_platform={HasChannelPlatform} has_channel_sender_id={HasChannelSenderId} has_owner_subject={HasOwnerSubject} has_owner_scope_id={HasOwnerScopeId}",
            code,
            typedAuthority.IsComplete,
            Normalize(AgentToolRequestContext.SenderBindingId) is not null,
            Normalize(AgentToolRequestContext.SenderNyxUserId) is not null,
            Normalize(AgentToolRequestContext.Current?.SenderBinding.SenderTenant) is not null,
            Normalize(AgentToolRequestContext.ChannelPlatform) is not null,
            Normalize(AgentToolRequestContext.ChannelSenderId) is not null,
            Normalize(AgentToolRequestContext.OwnerSubject) is not null,
            Normalize(AgentToolRequestContext.OwnerScopeId) is not null);
    }

    private static string ErrorJson(
        string code,
        string message,
        string? authorizationPlanMismatchReason = null,
        IReadOnlyList<string>? requiredUserServiceIds = null) =>
        JsonSerializer.Serialize(new ScheduleStudioMemberWorkflowErrorJson(
            new ScheduleStudioMemberWorkflowErrorBody(
                code,
                message,
                authorizationPlanMismatchReason,
                RequiredUserServiceIds: requiredUserServiceIds)),
            s_jsonOptions);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ScheduleOperationIdentity? TryBuildOperationIdentity(
        string scopeId,
        string memberId,
        string ownerSubject)
    {
        var callerIdempotencyKey = Normalize(AgentToolRequestContext.IdempotencyKey);
        var requestId = Normalize(AgentToolRequestContext.RequestId);
        var callId = Normalize(AgentToolRequestContext.CallId);
        if (callerIdempotencyKey is null && (requestId is null || callId is null))
            return null;

        var invocation = callerIdempotencyKey is null
            ? new ScheduleToolInvocationIdentity("request_call", string.Empty, requestId!, callId!)
            : new ScheduleToolInvocationIdentity("idempotency_key", callerIdempotencyKey, string.Empty, string.Empty);
        var canonical = new ScheduleOperationFingerprint(
            "studio-member-workflow-schedule/v1",
            scopeId,
            memberId,
            ownerSubject,
            invocation);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, s_jsonOptions);
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new ScheduleOperationIdentity(
            $"studio-member-workflow-create:{fingerprint}",
            $"studio-member-workflow-schedule:{fingerprint}");
    }

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

    private sealed record ScheduleOperationIdentity(string OperationId, string IdempotencyKey);

    private sealed record ScheduleOperationFingerprint(
        string SchemaVersion,
        string ScopeId,
        string MemberId,
        string OwnerSubject,
        ScheduleToolInvocationIdentity Invocation);

    private sealed record ScheduleToolInvocationIdentity(
        string Kind,
        string IdempotencyKey,
        string RequestId,
        string CallId);

    private sealed record ScheduleStudioMemberWorkflowResultJson(
        bool Success,
        string Status,
        string ScopeId,
        string MemberId,
        string ScheduleId,
        string PublishedServiceId,
        string ObservatoryUrl);

    private sealed record ScheduleStudioMemberWorkflowErrorJson(ScheduleStudioMemberWorkflowErrorBody Error);

    private sealed record ScheduleStudioMemberWorkflowErrorBody(
        string Code,
        string Message,
        string? AuthorizationPlanMismatchReason = null,
        string? ScopeId = null,
        string? TeamId = null,
        string? MemberId = null,
        string? WorkflowId = null,
        string? PublishedServiceId = null,
        string? ServingRevisionId = null,
        string? TargetRevisionId = null,
        string? BindingOperation = null,
        string? BindingStatus = null,
        IReadOnlyList<string>? RequiredUserServiceIds = null);
}

internal static class StudioMemberWorkflowScheduleAuthorizationResolver
{
    public static StudioMemberWorkflowScheduleAuthorizationResolution Resolve()
    {
        var typedAuthority = AgentToolRequestContext.NyxIdAuthority;
        var bindingId = Normalize(AgentToolRequestContext.SenderBindingId);
        if (bindingId is null)
        {
            return StudioMemberWorkflowScheduleAuthorizationResolution.Failure(
                "authenticated_owner_context_unavailable",
                "A verified NyxID binding is required to authorize a Team schedule.");
        }

        var resolvedSubject = ResolveSubject(typedAuthority);
        if (resolvedSubject.Error is { } error)
            return error;

        var subject = resolvedSubject.Subject!;
        var provisioningBearerToken = Normalize(AgentToolRequestContext.NyxIdAccessToken);
        if (provisioningBearerToken is null && !typedAuthority.IsComplete)
        {
            return StudioMemberWorkflowScheduleAuthorizationResolution.Failure(
                "caller_credential_unavailable",
                "A current NyxID credential is required to create the schedule credential.");
        }

        return StudioMemberWorkflowScheduleAuthorizationResolution.Success(
            new StudioMemberWorkflowScheduleAuthorizationContext(
                OwnerSubject: subject.NyxUserId,
                AuthenticatedOwner: new AuthenticatedAuthorizationOwnerContext(
                    new AuthorizationOwnerIdentity
                    {
                        Authority = NyxIdAuthorizationAuthorities.NyxId,
                        OwnerKind = AuthorizationOwnerKind.Personal,
                        OwnerSubject = subject.NyxUserId,
                    },
                    subject.Platform,
                    subject.Tenant,
                    subject.ExternalUserId,
                    bindingId),
                ProvisioningBearerToken: provisioningBearerToken));
    }

    private static StudioMemberWorkflowScheduleSubjectResolution ResolveSubject(
        AgentToolNyxIdAuthorityContext typedAuthority)
    {
        var typedPlatform = Normalize(typedAuthority.Platform);
        var typedExternalUserId = Normalize(typedAuthority.ExternalUserId);
        if (typedAuthority.IsComplete && string.Equals(
                typedPlatform,
                NyxIdAuthorizationAuthorities.NyxId,
                StringComparison.Ordinal))
        {
            return StudioMemberWorkflowScheduleSubjectResolution.Success(
                new StudioMemberWorkflowScheduleSubject(
                    typedExternalUserId!,
                    typedPlatform!,
                    Normalize(typedAuthority.Tenant) ?? string.Empty,
                    typedExternalUserId!));
        }

        var senderNyxUserId = Normalize(AgentToolRequestContext.SenderNyxUserId);
        if (senderNyxUserId is null)
        {
            return StudioMemberWorkflowScheduleSubjectResolution.Failure(
                "caller_subject_unavailable",
                "A caller NyxID user id is required in AgentToolRequestContext so the schedule can re-mint caller NyxID credentials when it fires.");
        }

        if (typedAuthority.IsComplete)
        {
            var typedTenant = Normalize(typedAuthority.Tenant) ??
                              Normalize(AgentToolRequestContext.Current?.SenderBinding.SenderTenant) ??
                              string.Empty;
            return StudioMemberWorkflowScheduleSubjectResolution.Success(
                new StudioMemberWorkflowScheduleSubject(
                    senderNyxUserId,
                    typedPlatform!,
                    typedTenant,
                    typedExternalUserId!));
        }

        var channelPlatform = Normalize(AgentToolRequestContext.ChannelPlatform);
        if (channelPlatform is null)
        {
            return StudioMemberWorkflowScheduleSubjectResolution.Failure(
                "caller_subject_unavailable",
                "A channel platform is required in AgentToolRequestContext so the schedule can re-mint caller NyxID credentials when it fires.");
        }

        var senderTenant = Normalize(AgentToolRequestContext.Current?.SenderBinding.SenderTenant);
        if (senderTenant is null)
        {
            return StudioMemberWorkflowScheduleSubjectResolution.Failure(
                "caller_subject_unavailable",
                "A sender tenant is required in AgentToolRequestContext so the schedule can re-mint caller NyxID credentials when it fires.");
        }

        var channelSenderId = Normalize(AgentToolRequestContext.ChannelSenderId);
        if (channelSenderId is null)
        {
            return StudioMemberWorkflowScheduleSubjectResolution.Failure(
                "caller_subject_unavailable",
                "A channel sender identity is required in AgentToolRequestContext so the schedule can re-mint caller NyxID credentials when it fires.");
        }

        return StudioMemberWorkflowScheduleSubjectResolution.Success(
            new StudioMemberWorkflowScheduleSubject(
                senderNyxUserId,
                channelPlatform,
                senderTenant,
                channelSenderId));
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed record StudioMemberWorkflowScheduleSubject(
    string NyxUserId,
    string Platform,
    string Tenant,
    string ExternalUserId);

internal sealed record StudioMemberWorkflowScheduleSubjectResolution(
    StudioMemberWorkflowScheduleSubject? Subject,
    StudioMemberWorkflowScheduleAuthorizationResolution? Error)
{
    public static StudioMemberWorkflowScheduleSubjectResolution Success(
        StudioMemberWorkflowScheduleSubject subject) =>
        new(subject, null);

    public static StudioMemberWorkflowScheduleSubjectResolution Failure(string code, string message) =>
        new(null, StudioMemberWorkflowScheduleAuthorizationResolution.Failure(code, message));
}

internal sealed record StudioMemberWorkflowScheduleAuthorizationResolution(
    StudioMemberWorkflowScheduleAuthorizationContext? Resolved,
    StudioMemberWorkflowScheduleAuthorizationError? Error)
{
    public static StudioMemberWorkflowScheduleAuthorizationResolution Success(
        StudioMemberWorkflowScheduleAuthorizationContext context) =>
        new(context, null);

    public static StudioMemberWorkflowScheduleAuthorizationResolution Failure(string code, string message) =>
        new(null, new StudioMemberWorkflowScheduleAuthorizationError(code, message));
}

internal sealed record StudioMemberWorkflowScheduleAuthorizationContext(
    string OwnerSubject,
    AuthenticatedAuthorizationOwnerContext AuthenticatedOwner,
    string? ProvisioningBearerToken);

internal sealed record StudioMemberWorkflowScheduleAuthorizationError(string Code, string Message);
