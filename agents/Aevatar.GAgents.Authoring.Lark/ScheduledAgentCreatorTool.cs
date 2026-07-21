using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Scheduled;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Authoring.Lark;

public sealed class ScheduledAgentCreatorTool : IAgentTool, IAgentToolContinuationCapability
{
    private readonly IScheduledWorkflowAgentCreationPort _scheduledWorkflowAgentCreationPort;
    private readonly ICallerScopeResolver _callerScopeResolver;
    private readonly ScheduledAgentCreateRequestMapper _mapper;
    private readonly IScheduledAgentCredentialLifecycle _credentialLifecycle;
    private readonly ILogger<ScheduledAgentCreatorTool>? _logger;

    internal ScheduledAgentCreatorTool(
        IScheduledWorkflowAgentCreationPort scheduledWorkflowAgentCreationPort,
        ICallerScopeResolver callerScopeResolver,
        ScheduledAgentCreateRequestMapper mapper,
        IScheduledAgentCredentialLifecycle credentialLifecycle,
        ILogger<ScheduledAgentCreatorTool>? logger = null)
    {
        _scheduledWorkflowAgentCreationPort = scheduledWorkflowAgentCreationPort ?? throw new ArgumentNullException(nameof(scheduledWorkflowAgentCreationPort));
        _callerScopeResolver = callerScopeResolver ?? throw new ArgumentNullException(nameof(callerScopeResolver));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _credentialLifecycle = credentialLifecycle ?? throw new ArgumentNullException(nameof(credentialLifecycle));
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
            "required_service_slugs": {
              "type": "array",
              "description": "Optional NyxID service slugs the scheduled skill body will call through nyxid_proxy, such as tavily-search or api-github. The creator resolves these to service IDs for the scoped key; callers must not provide service IDs.",
              "items": {
                "type": "string"
              }
            },
            "output_format": {
              "type": "string",
              "enum": ["auto", "text", "feishu_doc"],
              "description": "Optional scheduled-run output format. auto keeps length-based delivery, text forces chat text chunks, feishu_doc forces Feishu cloud document delivery."
            },
            "external_trigger_sources": {
              "type": "array",
              "description": "Optional external trigger source declarations. For channel run_agent, use source_id channel:<platform>:<registration_scope_id> and kind channel_inbound.",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "source_id": {
                    "type": "string"
                  },
                  "kind": {
                    "type": "string",
                    "enum": ["webhook", "channel_inbound"]
                  },
                  "enabled": {
                    "type": "boolean"
                  },
                  "display_name": {
                    "type": "string"
                  }
                },
                "required": ["source_id", "kind"]
              }
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

    public Any CaptureContinuationCapability() => Any.Pack(new FixedAgentToolContinuationCapability
    {
        ContractId = "scheduled_agent_creator",
        ContractVersion = 1,
    });

    public bool MatchesContinuationCapability(Any capability) =>
        capability.Equals(CaptureContinuationCapability());

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
        ScheduledAgentCredentialProvisionResult provisioned;
        try
        {
            provisioned = await _credentialLifecycle.ProvisionAsync(
                token,
                plan.ServiceSlugs!,
                agentId,
                caller,
                plan.Request!.Reference.Name,
                plan.Request.ScopeId,
                CredentialSecretPurposes.ScheduledInvocationAgentKey,
                ScheduledAgentCreateRequestMapper.BuildScheduledNyxApiKeyOwnerScopeKey(
                    plan.Request.Caller,
                    plan.Request.ScopeId,
                    plan.Request.ConversationId,
                    plan.Request.ReceiveTarget.Primary.ReceiveId),
                "scheduled-agent-create",
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Scheduled credential vault provisioning failed: agentId={AgentId}", agentId);
            return JsonSerializer.Serialize(new { error = "secret_vault_put_failed", detail = ex.Message });
        }
        if (!provisioned.IssuedKey.Success)
            return provisioned.IssuedKey.ToErrorJson();

        var key = provisioned.IssuedKey;
        var secretReference = provisioned.SecretReference!;
        var mapped = _mapper.Map(plan.Request, key, secretReference);

        ScheduledWorkflowAgentCreationReceipt receipt;
        try
        {
            receipt = await _scheduledWorkflowAgentCreationPort.CreateAsync(mapped.Request!, ct);
        }
        catch (Exception ex)
        {
            await _credentialLifecycle.RequestRevocationAsync(
                token,
                agentId,
                key.ApiKeyId ?? string.Empty,
                caller,
                secretReference,
                CancellationToken.None);
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

}
