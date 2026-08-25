using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Scheduled;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Scheduled;

public sealed class AgentBuilderTool : IAgentTool
{
    // Refactor (iter83/cluster-083-agent-tool-source-root-provider-locator):
    //   Old pattern: tool source captures root IServiceProvider; tools resolve business ports via service locator in ExecuteAsync
    //   New principle: tool source + tools constructor-inject typed contracts; no root provider lookup
    private readonly IUserAgentCatalogQueryPort _queryPort;
    private readonly IScheduledDispatchApplicationService _scheduledDispatchService;
    private readonly IUserAgentCatalogCommandPort _catalogCommandPort;
    private readonly ICallerScopeResolver _callerScopeResolver;
    private readonly ILogger<AgentBuilderTool>? _logger;

    // Refactor (iter1/cluster-002):
    //   Old pattern: Tool construction carried readmodel polling budget for lifecycle command paths.
    //   New principle: Lifecycle commands return accepted; freshness is observed by follow-up query or push event.
    public AgentBuilderTool(
        IUserAgentCatalogQueryPort queryPort,
        IScheduledDispatchApplicationService scheduledDispatchService,
        IUserAgentCatalogCommandPort catalogCommandPort,
        ICallerScopeResolver callerScopeResolver,
        ILogger<AgentBuilderTool>? logger = null)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _scheduledDispatchService = scheduledDispatchService ?? throw new ArgumentNullException(nameof(scheduledDispatchService));
        _catalogCommandPort = catalogCommandPort ?? throw new ArgumentNullException(nameof(catalogCommandPort));
        _callerScopeResolver = callerScopeResolver ?? throw new ArgumentNullException(nameof(callerScopeResolver));
        _logger = logger;
    }

    public string Name => "agent_builder";

    public string Description =>
        "List and manage the caller's persistent automation agents. " +
        "Actions: list_agents, agent_status, run_agent, share_agent, unshare_agent, disable_agent, enable_agent, delete_agent. " +
        "Agent creation is handled by scheduled_agent_creator.";

    // Note (issue #466): no `owner_nyx_user_id` parameter is exposed. The tool always
    // operates on the caller's own agents; the resolver derives ownership from the
    // request context (NyxID `/me` for native cli/web, channel sender_id+platform for
    // lark/telegram). Allowing an LLM-overridable owner field would re-introduce the
    // impersonation surface that #466 removes.
    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list_agents", "agent_status", "run_agent", "share_agent", "unshare_agent", "disable_agent", "enable_agent", "delete_agent"]
            },
            "agent_id": {
              "type": "string",
              "description": "Stable actor ID. Required for every action except list_agents."
            },
            "confirm": {
              "type": "boolean",
              "description": "Must be true to execute delete_agent."
            },
            "revision_feedback": {
              "type": "string",
              "description": "Optional revision guidance to include in the next run."
            },
            "allow_trigger": {
              "type": "boolean",
              "description": "When true, shared channel members can also run the agent. Used only by share_agent."
            }
          },
          "required": ["action"]
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        // Refactor (iter24/cluster-002-agent-tool-context-generic-metadata-bag):
        //   Old pattern: agent-builder auth read NyxID credentials from generic Metadata keys.
        //   New principle: credentials are typed request context fields, not internal Metadata.
        var token = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        var args = BuilderArgs.Parse(argumentsJson);
        if (args.HasParseError)
            return JsonSerializer.Serialize(new { error = args.ParseError });

        // Resolve once per request and pass to every method below. Failure to resolve
        // is fail-closed: never fall through to "all agents". (Issue #466 acceptance.)
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

        var action = args.Str("action", "list_agents");
        _logger?.LogInformation(
            "AgentBuilder caller scope resolved: action={Action} platform={Platform} nyxUser={NyxUserId} scope={RegistrationScopeId} sender={SenderId}",
            action,
            caller.Platform,
            caller.NyxUserId,
            caller.RegistrationScopeId,
            caller.SenderId);
        return action switch
        {
            "list_agents" => await ListAgentsAsync(_queryPort, caller, ct),
            "agent_status" => await GetAgentStatusAsync(args, _queryPort, caller, ct),
            "run_agent" => await RunAgentAsync(args, _queryPort, _scheduledDispatchService, caller, ct),
            "share_agent" => await ShareAgentAsync(args, _queryPort, _catalogCommandPort, caller, ct),
            "unshare_agent" => await UnshareAgentAsync(args, _queryPort, _catalogCommandPort, caller, ct),
            "disable_agent" => await DisableAgentAsync(args, _queryPort, _scheduledDispatchService, caller, ct),
            "enable_agent" => await EnableAgentAsync(args, _queryPort, _scheduledDispatchService, caller, ct),
            "delete_agent" => await DeleteAgentAsync(args, _queryPort, _catalogCommandPort, _scheduledDispatchService, token, caller, ct),
            _ => JsonSerializer.Serialize(new { error = $"Unsupported action '{action}'" }),
        };
    }

    private async Task<string> ListAgentsAsync(
        IUserAgentCatalogQueryPort queryPort,
        OwnerScope caller,
        CancellationToken ct)
    {
        var agents = await QueryAgentsForCallerAsync(queryPort, caller, ct);
        return JsonSerializer.Serialize(new { agents, total = agents.Length });
    }

    private async Task<string> GetAgentStatusAsync(
        BuilderArgs args,
        IUserAgentCatalogQueryPort queryPort,
        OwnerScope caller,
        CancellationToken ct)
    {
        var agentId = args.Str("agent_id");
        if (string.IsNullOrWhiteSpace(agentId))
            return """{"error":"agent_id is required for agent_status"}""";

        var entry = await queryPort.GetVisibleForCallerAsync(agentId.Trim(), caller, ct);
        if (entry is null)
            return JsonSerializer.Serialize(new { error = $"Agent '{agentId}' not found" });

        return SerializeAgentStatus(entry);
    }

    private async Task<string> ShareAgentAsync(
        BuilderArgs args,
        IUserAgentCatalogQueryPort queryPort,
        IUserAgentCatalogCommandPort catalogCommandPort,
        OwnerScope caller,
        CancellationToken ct)
    {
        var agentId = args.Str("agent_id");
        if (string.IsNullOrWhiteSpace(agentId))
            return """{"error":"agent_id is required for share_agent"}""";

        if (!UserAgentCatalogSharingAudience.TryBuildKey(caller, out var audienceKey))
            return JsonSerializer.Serialize(new { error = "share_agent requires a channel registration scope" });

        var entry = await QueryCatalogAgentForCallerAsync(queryPort, agentId.Trim(), caller, ct);
        if (entry is null)
            return JsonSerializer.Serialize(new { error = $"Agent '{agentId}' not found" });

        var allowTrigger = args.Bool("allow_trigger") == true;
        await catalogCommandPort.ShareAsync(entry.AgentId, caller, allowTrigger, ct);

        return JsonSerializer.Serialize(new
        {
            status = "accepted",
            agent_id = entry.AgentId,
            shared_with_registration_scope = caller.RegistrationScopeId,
            sharing_audience_key = audienceKey,
            allow_trigger = allowTrigger,
            note = "Share update accepted. Shared visibility is available after the catalog projection catches up.",
        });
    }

    private async Task<string> UnshareAgentAsync(
        BuilderArgs args,
        IUserAgentCatalogQueryPort queryPort,
        IUserAgentCatalogCommandPort catalogCommandPort,
        OwnerScope caller,
        CancellationToken ct)
    {
        var agentId = args.Str("agent_id");
        if (string.IsNullOrWhiteSpace(agentId))
            return """{"error":"agent_id is required for unshare_agent"}""";

        var entry = await QueryCatalogAgentForCallerAsync(queryPort, agentId.Trim(), caller, ct);
        if (entry is null)
            return JsonSerializer.Serialize(new { error = $"Agent '{agentId}' not found" });

        await catalogCommandPort.UnshareAsync(entry.AgentId, caller, ct);

        return JsonSerializer.Serialize(new
        {
            status = "accepted",
            agent_id = entry.AgentId,
            note = "Unshare update accepted. Shared visibility is removed after the catalog projection catches up.",
        });
    }

    private async Task<string> DeleteAgentAsync(
        BuilderArgs args,
        IUserAgentCatalogQueryPort queryPort,
        IUserAgentCatalogCommandPort catalogCommandPort,
        IScheduledDispatchApplicationService scheduledDispatchService,
        string token,
        OwnerScope caller,
        CancellationToken ct)
    {
        var agentId = args.Str("agent_id");
        if (string.IsNullOrWhiteSpace(agentId))
            return """{"error":"agent_id is required for delete_agent"}""";

        var entry = await QueryCatalogAgentForCallerAsync(queryPort, agentId.Trim(), caller, ct);
        if (entry is null)
            return JsonSerializer.Serialize(new { error = $"Agent '{agentId}' not found" });

        if (args.Bool("confirm") != true)
        {
            return JsonSerializer.Serialize(new
            {
                status = "confirm_required",
                agent_id = entry.AgentId,
                template = entry.TemplateName,
                hint = "Re-run with confirm=true to delete this agent.",
            });
        }

        if (!IsScheduledWorkflowAgent(entry.AgentType))
            return JsonSerializer.Serialize(new { error = $"Agent '{entry.AgentId}' does not support delete_agent" });

        await scheduledDispatchService.DeleteAsync(entry.AgentId, "delete_agent", ct: ct);

        await catalogCommandPort.RetryCredentialRevocationsAsync(caller, token, ct);
        await catalogCommandPort.TombstoneAsync(entry.AgentId, ct, token);
        var agents = await QueryAgentsForCallerAsync(queryPort, caller, ct);

        return JsonSerializer.Serialize(new
        {
            status = "accepted",
            agent_id = entry.AgentId,
            revoked_api_key_id = entry.ApiKeyId,
            api_key_revocation_status = string.IsNullOrWhiteSpace(entry.ApiKeyId) ? "not_applicable" : "pending",
            api_key_revocation_retry_status = "accepted",
            delete_notice = $"Delete submitted for `{entry.AgentId}`. Credential revocation is pending committed-intent processing.",
            agents,
            total = agents.Length,
            note = "Tombstone is propagating. Run /agents in a few seconds to confirm the agent is gone.",
        });
    }

    private async Task<string> RunAgentAsync(
        BuilderArgs args,
        IUserAgentCatalogQueryPort queryPort,
        IScheduledDispatchApplicationService scheduledDispatchService,
        OwnerScope caller,
        CancellationToken ct)
    {
        var agentId = args.Str("agent_id");
        if (string.IsNullOrWhiteSpace(agentId))
            return """{"error":"agent_id is required for run_agent"}""";

        var entry = await QueryTriggerableCatalogAgentForCallerAsync(queryPort, agentId.Trim(), caller, ct);
        if (entry is null)
            return JsonSerializer.Serialize(new { error = $"Agent '{agentId}' not found" });

        if (!SupportsManagedLifecycle(entry.AgentType))
            return JsonSerializer.Serialize(new { error = $"Agent '{entry.AgentId}' does not support run_agent" });

        var revisionFeedback = NormalizeOptional(args.Str("revision_feedback"));
        var dispatch = await TryDispatchLifecycleAsync(entry, "run_agent", LifecycleAction.Run, revisionFeedback, scheduledDispatchService, ct);
        if (dispatch.error != null)
            return dispatch.error;

        return JsonSerializer.Serialize(new
        {
            status = "accepted",
            agent_id = entry.AgentId,
            template = entry.TemplateName,
            note = revisionFeedback is null
                ? "Manual run accepted for dispatch. Runner state decides whether execution proceeds; use /agent-status to observe the result."
                : "Manual run accepted for dispatch with revision feedback. Runner state decides whether execution proceeds; use /agent-status to observe the result.",
        });
    }

    private async Task<string> DisableAgentAsync(
        BuilderArgs args,
        IUserAgentCatalogQueryPort queryPort,
        IScheduledDispatchApplicationService scheduledDispatchService,
        OwnerScope caller,
        CancellationToken ct)
    {
        var entry = await RequireManagedAgentAsync(args, queryPort, caller, "disable_agent", ct);
        if (entry.error != null)
            return entry.error;

        // Refactor (iter1/cluster-002):
        //   Old pattern: Captured readmodel version, dispatched lifecycle, then delayed-looped for projected status.
        //   New principle: Lifecycle commands return accepted; freshness is observed by follow-up query or push event.
        var dispatch = await TryDispatchLifecycleAsync(entry.value!, "disable_agent", LifecycleAction.Disable, null, scheduledDispatchService, ct);
        if (dispatch.error != null)
            return dispatch.error;

        return SerializeAgentStatus(entry.value!, "Disable accepted. Status update is propagating; run /agent-status to confirm the agent is paused.");
    }

    private async Task<string> EnableAgentAsync(
        BuilderArgs args,
        IUserAgentCatalogQueryPort queryPort,
        IScheduledDispatchApplicationService scheduledDispatchService,
        OwnerScope caller,
        CancellationToken ct)
    {
        var entry = await RequireManagedAgentAsync(args, queryPort, caller, "enable_agent", ct);
        if (entry.error != null)
            return entry.error;

        // Refactor (iter1/cluster-002):
        //   Old pattern: Captured readmodel version, dispatched lifecycle, then delayed-looped for projected status.
        //   New principle: Lifecycle commands return accepted; freshness is observed by follow-up query or push event.
        var dispatch = await TryDispatchLifecycleAsync(entry.value!, "enable_agent", LifecycleAction.Enable, null, scheduledDispatchService, ct);
        if (dispatch.error != null)
            return dispatch.error;

        return SerializeAgentStatus(entry.value!, "Enable accepted. Status update is propagating; run /agent-status to confirm the agent is running.");
    }

    private static string SerializeAgentStatus(UserAgentCatalogReadModelEntry entry, string? note = null)
    {
        return JsonSerializer.Serialize(new
        {
            agent_id = entry.AgentId,
            agent_type = entry.AgentType,
            template = entry.TemplateName,
            status = entry.Status,
            scope_id = entry.ScopeId,
            schedule_cron = entry.ScheduleCron,
            schedule_timezone = entry.ScheduleTimezone,
            schedule_mode = ToScheduleModeJsonValue(entry.ScheduleMode),
            run_at_utc = entry.RunAt,
            retired_at_utc = entry.RetiredAt,
            retirement_reason = entry.RetirementReason,
            output_format = ToOutputFormatJsonValue(entry.OutputFormat),
            last_run_at = entry.LastRunAt,
            next_scheduled_run = entry.NextRunAt,
            error_count = entry.ErrorCount,
            last_error = entry.LastError,
            conversation_id = entry.ConversationId,
            note = note ?? string.Empty,
        });
    }

    private async Task<object[]> QueryAgentsForCallerAsync(
        IUserAgentCatalogQueryPort queryPort,
        OwnerScope caller,
        CancellationToken ct)
    {
        var entries = await queryPort.QueryVisibleByCallerAsync(caller, ct);
        return entries
            .Select(static x => new
            {
                agent_id = x.AgentId,
                agent_type = x.AgentType,
                template = x.TemplateName,
                status = x.Status,
                schedule_cron = x.ScheduleCron,
                schedule_timezone = x.ScheduleTimezone,
                schedule_mode = ToScheduleModeJsonValue(x.ScheduleMode),
                run_at_utc = x.RunAt,
                retired_at_utc = x.RetiredAt,
                output_format = ToOutputFormatJsonValue(x.OutputFormat),
                last_run_at = x.LastRunAt,
                next_scheduled_run = x.NextRunAt,
                error_count = x.ErrorCount,
            })
            .Cast<object>()
            .ToArray();
    }

    private async Task<(UserAgentCatalogReadModelEntry? value, string? error)> RequireManagedAgentAsync(
        BuilderArgs args,
        IUserAgentCatalogQueryPort queryPort,
        OwnerScope caller,
        string actionName,
        CancellationToken ct)
    {
        var agentId = args.Str("agent_id");
        if (string.IsNullOrWhiteSpace(agentId))
            return (null, $$"""{"error":"agent_id is required for {{actionName}}"}""");

        var entry = await QueryCatalogAgentForCallerAsync(queryPort, agentId.Trim(), caller, ct);
        if (entry is null)
            return (null, JsonSerializer.Serialize(new { error = $"Agent '{agentId}' not found" }));

        if (!SupportsManagedLifecycle(entry.AgentType))
            return (null, JsonSerializer.Serialize(new { error = $"Agent '{entry.AgentId}' does not support {actionName}" }));

        return (entry, null);
    }

    private static Task<UserAgentCatalogReadModelEntry?> QueryCatalogAgentForCallerAsync(
        IUserAgentCatalogQueryPort queryPort,
        string agentId,
        OwnerScope caller,
        CancellationToken ct) =>
        queryPort.GetForCallerAsync(agentId, caller, ct);

    private static Task<UserAgentCatalogReadModelEntry?> QueryTriggerableCatalogAgentForCallerAsync(
        IUserAgentCatalogQueryPort queryPort,
        string agentId,
        OwnerScope caller,
        CancellationToken ct) =>
        queryPort.GetTriggerableForCallerAsync(agentId, caller, ct);

    private static string ToOutputFormatJsonValue(ScheduledAgentOutputFormat outputFormat) =>
        outputFormat switch
        {
            ScheduledAgentOutputFormat.Text => "text",
            ScheduledAgentOutputFormat.FeishuDoc => "feishu_doc",
            _ => "auto",
        };

    private static string ToScheduleModeJsonValue(ScheduledAgentScheduleMode scheduleMode) =>
        scheduleMode == ScheduledAgentScheduleMode.OneShot ? "one_shot" : "cron";

    private static async Task<(bool success, string? error)> TryDispatchLifecycleAsync(
        UserAgentCatalogReadModelEntry entry,
        string reason,
        LifecycleAction action,
        string? revisionFeedback,
        IScheduledDispatchApplicationService scheduledDispatchService,
        CancellationToken ct)
    {
        if (IsScheduledWorkflowAgent(entry.AgentType))
        {
            switch (action)
            {
                case LifecycleAction.Run:
                    await scheduledDispatchService.RunNowAsync(entry.AgentId, ct: ct);
                    break;
                case LifecycleAction.Disable:
                    await scheduledDispatchService.DisableAsync(entry.AgentId, reason, ct: ct);
                    break;
                case LifecycleAction.Enable:
                    await scheduledDispatchService.EnableAsync(entry.AgentId, reason, ct: ct);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action, null);
            }
            _ = revisionFeedback; // Schedule dispatch doesn't accept revision feedback today; reserved for future workflow surfaces.
            return (true, null);
        }

        return (false, JsonSerializer.Serialize(new { error = $"Agent '{entry.AgentId}' does not support {action.ToString().ToLowerInvariant()}." }));
    }

    private static bool SupportsManagedLifecycle(string? agentType) =>
        IsScheduledWorkflowAgent(agentType);

    private static bool IsScheduledWorkflowAgent(string? agentType) =>
        string.Equals(agentType, ScheduledWorkflowAgentDefaults.AgentType, StringComparison.Ordinal);

    private static string? NormalizeOptional(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private sealed class BuilderArgs
    {
        private readonly Dictionary<string, JsonElement> _properties;

        private BuilderArgs(Dictionary<string, JsonElement> properties, string? parseError)
        {
            _properties = properties;
            ParseError = parseError;
        }

        public bool HasParseError => ParseError != null;

        public string? ParseError { get; }

        public static BuilderArgs Parse(string? json)
        {
            var raw = string.IsNullOrWhiteSpace(json) ? "{}" : json!;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var properties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in doc.RootElement.EnumerateObject())
                    properties[property.Name] = property.Value.Clone();

                return new BuilderArgs(properties, null);
            }
            catch (JsonException ex)
            {
                return new BuilderArgs([], ex.Message);
            }
        }

        public string? Str(string name)
        {
            if (!_properties.TryGetValue(name, out var value))
                return null;

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };
        }

        public string Str(string name, string defaultValue) => Str(name) ?? defaultValue;

        public bool? Bool(string name)
        {
            if (!_properties.TryGetValue(name, out var value))
                return null;

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
                _ => null,
            };
        }
    }

    private enum LifecycleAction
    {
        Run,
        Disable,
        Enable,
    }
}
