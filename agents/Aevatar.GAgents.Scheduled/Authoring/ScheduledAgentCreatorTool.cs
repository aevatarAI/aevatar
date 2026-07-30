using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgents.Scheduled;
using Aevatar.Workflow.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Scheduled;

public sealed class ScheduledAgentCreatorTool : IAgentTool
{
    private readonly IScheduledWorkflowAgentCreationPort _scheduledWorkflowAgentCreationPort;
    private readonly ICallerScopeResolver _callerScopeResolver;
    private readonly ScheduledAgentCreateRequestMapper _mapper;
    private readonly IScheduledAgentCredentialLifecycle _credentialLifecycle;
    private readonly IScheduledInvocationAuthorizationPlanner _authorizationPlanner;
    private readonly IScheduledInvocationAuthorizationRevalidator _authorizationRevalidator;
    private readonly ScheduledAgentCreatorOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ScheduledAgentCreatorTool>? _logger;

    internal ScheduledAgentCreatorTool(
        IScheduledWorkflowAgentCreationPort scheduledWorkflowAgentCreationPort,
        ICallerScopeResolver callerScopeResolver,
        ScheduledAgentCreateRequestMapper mapper,
        IScheduledAgentCredentialLifecycle credentialLifecycle,
        IScheduledInvocationAuthorizationPlanner authorizationPlanner,
        IScheduledInvocationAuthorizationRevalidator authorizationRevalidator,
        ScheduledAgentCreatorOptions? options = null,
        ILogger<ScheduledAgentCreatorTool>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _scheduledWorkflowAgentCreationPort = scheduledWorkflowAgentCreationPort ?? throw new ArgumentNullException(nameof(scheduledWorkflowAgentCreationPort));
        _callerScopeResolver = callerScopeResolver ?? throw new ArgumentNullException(nameof(callerScopeResolver));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _credentialLifecycle = credentialLifecycle ?? throw new ArgumentNullException(nameof(credentialLifecycle));
        _authorizationPlanner = authorizationPlanner ?? throw new ArgumentNullException(nameof(authorizationPlanner));
        _authorizationRevalidator = authorizationRevalidator ?? throw new ArgumentNullException(nameof(authorizationRevalidator));
        _options = options ?? new ScheduledAgentCreatorOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public string Name => "scheduled_agent_creator";

    public string Description =>
        "Create a caller-owned scheduled automation agent or one-shot reminder. " +
        "Recurring mode requires skill_ref, schedule_cron, and schedule_timezone. " +
        "One-shot mode requires delay_seconds or run_at_utc; one_shot_message can send a reminder without an Ornn skill. " +
        "Creation mints a scoped NyxID API key and returns an accepted dispatch receipt only.";

    public string ParametersSchema => """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "skill_ref": {
              "type": "string",
              "description": "Unversioned Ornn skill name. Required for recurring cron schedules. Optional for one-shot reminders. name@version is not supported yet."
            },
            "schedule_mode": {
              "type": "string",
              "enum": ["cron", "one_shot"],
              "description": "cron for recurring scheduled skill agents; one_shot for a single delayed reminder or one-time skill run."
            },
            "schedule_cron": {
              "type": "string",
              "description": "Standard 5-field cron expression (minute hour day-of-month month day-of-week). Required only when schedule_mode is cron. Seconds fields are not supported."
            },
            "schedule_timezone": {
              "type": "string",
              "description": "IANA timezone name for cron schedule evaluation. Required only when schedule_mode is cron."
            },
            "delay_seconds": {
              "type": "integer",
              "description": "One-shot delay in seconds. Use this instead of calculating cron or sleeping in code_execute. Mutually exclusive with run_at_utc."
            },
            "run_at_utc": {
              "type": "string",
              "description": "One-shot UTC instant as ISO-8601 with Z or +00:00. Mutually exclusive with delay_seconds."
            },
            "one_shot_message": {
              "type": "string",
              "description": "Message to send when a one-shot reminder fires. Required for one-shot reminders that do not use skill_ref."
            },
            "display_name": {
              "type": "string",
              "description": "Optional display name for the created agent."
            },
            "execution_prompt": {
              "type": "string",
              "description": "Optional extra execution instruction for the runner."
            },
            "provider_name": {
              "type": "string",
              "description": "Optional LLM provider route name."
            },
            "model": {
              "type": "string",
              "description": "Optional model name."
            },
            "temperature": {
              "type": "number",
              "description": "Optional model temperature."
            },
            "max_tokens": {
              "type": "integer",
              "description": "Optional max output tokens."
            },
            "max_tool_rounds": {
              "type": "integer",
              "description": "Optional max tool rounds."
            },
            "max_history_messages": {
              "type": "integer",
              "description": "Optional max retained history messages."
            },
            "requires_nyxid_proxy_success": {
              "type": "boolean",
              "description": "When true, the run must observe a successful NyxID proxy call."
            },
            "required_nyx_services": {
              "type": "array",
              "description": "Exact NyxID UserService identities and route snapshots required by Ornn, failure delivery, or the scheduled skill body.",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "user_service_id": { "type": "string" },
                  "service_slug_snapshot": { "type": "string" }
                },
                "required": ["user_service_id", "service_slug_snapshot"]
              }
            },
            "nyx_user_service_id": {
              "type": "string",
              "description": "Exact NyxID UserService identity for the effective outbound delivery provider."
            },
            "nyx_provider_slug": {
              "type": "string",
              "description": "Optional one-shot reminder outbound delivery provider slug, such as api-lark-bot-2. Use to select a connected provider for reminder delivery; this does not apply to cron schedules."
            },
            "output_format": {
              "type": "string",
              "enum": ["auto", "text", "feishu_doc"],
              "description": "Optional scheduled-run output format. auto keeps length-based delivery, text forces chat text chunks, feishu_doc forces Feishu cloud document delivery."
            },
            "run_immediately": {
              "type": "boolean",
              "description": "When true, trigger the first run after initialization is accepted."
            }
          },
          "required": []
        }
        """;

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;
    public bool IsReadOnly => false;
    public bool IsDestructive => false;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        OwnerScope caller;
        try
        {
            caller = await _callerScopeResolver.RequireAsync(ct);
        }
        catch (CallerScopeUnavailableException ex)
        {
            return JsonSerializer.Serialize(new
            {
                error = "caller_scope_unavailable",
                detail = ex.Message,
                hint = "Re-authenticate (cli/web) or ensure the channel relay propagates platform/sender_id metadata.",
            });
        }

        var agentId = ScheduledWorkflowAgentDefaults.GenerateActorId();
        var plan = _mapper.Plan(argumentsJson, caller, agentId);
        if (!plan.Success)
            return plan.ErrorJson ?? """{"error":"validation_error"}""";

        var authorizationRequest = BuildAuthorizationRequest(plan, caller, agentId);
        if (authorizationRequest is null)
            return """{"error":"authenticated_owner_context_unavailable"}""";

        var authorization = await _authorizationPlanner.PlanAsync(authorizationRequest, ct);
        if (!authorization.Success)
        {
            return JsonSerializer.Serialize(new
            {
                error = authorization.FailureCode.ToString(),
                detail = authorization.Detail,
            });
        }
        var validation = await _authorizationRevalidator.RevalidateAsync(
            authorizationRequest,
            ScheduledInvocationAuthorizationConfirmations.FromPlan(authorization.Plan!),
            ct);
        if (!validation.Success)
        {
            return JsonSerializer.Serialize(new
            {
                error = validation.FailureCode.ToString(),
                detail = validation.Detail,
            });
        }

        ScheduledAgentCredentialProvisionResult provisioned;
        try
        {
            provisioned = await _credentialLifecycle.ProvisionAsync(
                token,
                validation.ValidatedPlan!,
                $"aevatar-scheduled-agent-{agentId}",
                agentId,
                caller,
                Aevatar.Foundation.Abstractions.Credentials.CredentialSecretPurposes.ScheduledInvocationAgentKey,
                ScheduledAgentCreateRequestMapper.BuildScheduledNyxApiKeyOwnerScopeKey(
                    plan.Request!.Caller,
                    plan.Request.ScopeId,
                    plan.Request.ConversationId,
                    plan.Request.ChannelTarget.PrimaryAddressId),
                "scheduled-agent-create",
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Scheduled credential vault provisioning failed: agentId={AgentId}", agentId);
            return JsonSerializer.Serialize(new { error = "secret_vault_put_failed", detail = ex.Message });
        }

        if (!provisioned.Success)
            return provisioned.IssuedKey.ToErrorJson();

        var key = provisioned.IssuedKey;
        var mapped = _mapper.Map(plan.Request!, key, provisioned.SecretReference!);
        if (!mapped.Success)
        {
            await _credentialLifecycle.RequestRevocationAsync(
                token, agentId, key.ApiKeyId!, caller, provisioned.SecretReference!, ct);
            return mapped.ErrorJson ?? """{"error":"validation_error"}""";
        }

        ScheduledWorkflowAgentCreationReceipt receipt;
        try
        {
            receipt = await _scheduledWorkflowAgentCreationPort.CreateAsync(mapped.Request!, ct);
        }
        catch (Exception ex)
        {
            await _credentialLifecycle.RequestRevocationAsync(
                token, agentId, key.ApiKeyId!, caller, provisioned.SecretReference!, CancellationToken.None);
            _logger?.LogWarning(ex, "Scheduled agent create dispatch failed after key issue: agentId={AgentId}", agentId);
            return JsonSerializer.Serialize(new { error = "initialize_failed", detail = ex.Message });
        }

        return JsonSerializer.Serialize(new
        {
            status = receipt.Accepted ? "accepted" : "rejected",
            agent_id = receipt.AgentId,
            api_key_id = key.ApiKeyId,
            note = "Scheduled agent create accepted for dispatch. Use agent_builder agent_status to observe projection state.",
        });
    }

    private ScheduledInvocationAuthorizationRequest? BuildAuthorizationRequest(
        ScheduledAgentCreatePlanResult plan,
        OwnerScope caller,
        string agentId)
    {
        var request = plan.Request!;
        var serviceRequirements = plan.ServiceRequirements!;
        var bindingId = Normalize(AgentToolRequestContext.SenderBindingId);
        var ownerSubject = Normalize(AgentToolRequestContext.SenderNyxUserId) ?? Normalize(caller.NyxUserId);
        var subjectPlatform = Normalize(AgentToolRequestContext.NyxIdAuthority.Platform) ?? Normalize(caller.Platform);
        var subjectExternalUserId = Normalize(AgentToolRequestContext.NyxIdAuthority.ExternalUserId) ??
                                    Normalize(caller.SenderId) ?? ownerSubject;
        if (bindingId is null && string.Equals(subjectPlatform, "nyxid", StringComparison.Ordinal))
            bindingId = $"nyxid:{ownerSubject}";
        if (bindingId is null || ownerSubject is null || subjectPlatform is null || subjectExternalUserId is null ||
            _options.ApiKeyLifetimeDays <= 0)
        {
            return null;
        }

        var requiredServices = BuildRequiredServices(serviceRequirements);

        var authority = Normalize(_options.NyxIdAuthority);
        if (authority is null)
            return null;
        var now = _timeProvider.GetUtcNow();
        return new ScheduledInvocationAuthorizationRequest(
            new ScheduledInvocationTarget
            {
                ScheduledAgent = new ScheduledAgentInvocationTarget
                {
                    RegistrationScopeId = Normalize(caller.RegistrationScopeId) ?? request.ScopeId,
                    ExecutionScopeId = request.ScopeId,
                    ScheduledAgentId = agentId,
                },
            },
            new AuthenticatedAuthorizationOwnerContext(
                new AuthorizationOwnerIdentity
                {
                    Authority = authority,
                    OwnerKind = AuthorizationOwnerKind.Personal,
                    OwnerSubject = ownerSubject,
                },
                subjectPlatform,
                Normalize(caller.RegistrationScopeId) ?? string.Empty,
                subjectExternalUserId,
                bindingId),
            requiredServices,
            requiredServices.Count == 0
                ? AuthorizationGrantRequirement.NotRequired
                : AuthorizationGrantRequirement.Required,
            now.AddDays(_options.ApiKeyLifetimeDays),
            now,
            [new AuthorizationSourceStamp
            {
                SourceKind = AuthorizationSourceKind.ScheduledAgentRegistration,
                SourceId = agentId,
            }]);
    }

    private IReadOnlyList<NyxIdUserServiceCapabilityRef> BuildRequiredServices(
        ScheduledAgentServiceRequirements requirements)
    {
        var services = new List<NyxIdUserServiceCapabilityRef>();
        var identities = new HashSet<(string Id, string Slug)>();

        if (requirements.RequiresOrnnService)
        {
            AddExactRoute(
                services,
                identities,
                requirements.RequiredNyxServices,
                Normalize(_options.OrnnServiceSlug) ?? ScheduledAgentCreatorOptions.DefaultOrnnServiceSlug);
        }

        AddService(services, identities, new NyxIdUserServiceCapabilityRef
        {
            UserServiceId = requirements.PrimaryOutboundUserServiceId,
            ServiceSlugSnapshot = requirements.PrimaryOutboundSlug,
        });

        if (!string.IsNullOrWhiteSpace(requirements.FailureNotificationSlug))
        {
            AddExactRoute(
                services,
                identities,
                requirements.RequiredNyxServices,
                requirements.FailureNotificationSlug);
        }

        foreach (var service in requirements.RequiredNyxServices)
            AddService(services, identities, service);
        return services;
    }

    private static void AddExactRoute(
        ICollection<NyxIdUserServiceCapabilityRef> destination,
        ISet<(string Id, string Slug)> identities,
        IReadOnlyList<NyxIdUserServiceCapabilityRef> candidates,
        string slugSnapshot)
    {
        var matches = candidates
            .Where(candidate => string.Equals(
                candidate.ServiceSlugSnapshot.Trim(),
                slugSnapshot.Trim(),
                StringComparison.Ordinal))
            .ToArray();
        AddService(destination, identities, matches.Length == 1
            ? matches[0]
            : new NyxIdUserServiceCapabilityRef { ServiceSlugSnapshot = slugSnapshot.Trim() });
    }

    private static void AddService(
        ICollection<NyxIdUserServiceCapabilityRef> destination,
        ISet<(string Id, string Slug)> identities,
        NyxIdUserServiceCapabilityRef service)
    {
        var clone = service.Clone();
        clone.UserServiceId = clone.UserServiceId.Trim();
        clone.ServiceSlugSnapshot = clone.ServiceSlugSnapshot.Trim();
        if (identities.Add((clone.UserServiceId, clone.ServiceSlugSnapshot)))
            destination.Add(clone);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

}
