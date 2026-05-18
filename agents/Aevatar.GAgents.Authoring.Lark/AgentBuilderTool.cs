using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Scheduled;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Authoring.Lark;

public sealed class AgentBuilderTool : IAgentTool
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AgentBuilderTool>? _logger;

    // Refactor (iter1/cluster-002):
    //   Old pattern: Tool construction carried readmodel polling budget for lifecycle command paths.
    //   New principle: Lifecycle commands return accepted; freshness is observed by follow-up query or push event.
    public AgentBuilderTool(
        IServiceProvider serviceProvider,
        ILogger<AgentBuilderTool>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public string Name => "agent_builder";

    public string Description =>
        "List and manage the caller's persistent automation agents. " +
        "Actions: list_agents, agent_status, run_agent, disable_agent, enable_agent, delete_agent. " +
        "Agent creation is not handled here — recipes for new agents live as Ornn skills.";

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
              "enum": ["list_agents", "agent_status", "run_agent", "disable_agent", "enable_agent", "delete_agent"]
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
            }
          },
          "required": ["action"]
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        var args = BuilderArgs.Parse(argumentsJson);
        if (args.HasParseError)
            return JsonSerializer.Serialize(new { error = args.ParseError });

        var queryPort = _serviceProvider.GetService<IUserAgentCatalogQueryPort>();
        var nyxClient = _serviceProvider.GetService<NyxIdApiClient>();
        var skillRunnerPort = _serviceProvider.GetService<ISkillRunnerCommandPort>();
        var catalogCommandPort = _serviceProvider.GetService<IUserAgentCatalogCommandPort>();
        var callerScopeResolver = _serviceProvider.GetService<ICallerScopeResolver>();
        if (queryPort is null || nyxClient is null ||
            skillRunnerPort is null || catalogCommandPort is null ||
            callerScopeResolver is null)
        {
            return """{"error":"Agent builder runtime not available. Required services are not registered in DI."}""";
        }

        // Resolve once per request and pass to every method below. Failure to resolve
        // is fail-closed: never fall through to "all agents". (Issue #466 acceptance.)
        OwnerScope caller;
        try
        {
            caller = await callerScopeResolver.RequireAsync(ct);
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
        return action switch
        {
            "list_agents" => await ListAgentsAsync(queryPort, caller, ct),
            "agent_status" => await GetAgentStatusAsync(args, queryPort, caller, ct),
            "run_agent" => await RunAgentAsync(args, queryPort, skillRunnerPort, caller, ct),
            "disable_agent" => await DisableAgentAsync(args, queryPort, skillRunnerPort, caller, ct),
            "enable_agent" => await EnableAgentAsync(args, queryPort, skillRunnerPort, caller, ct),
            "delete_agent" => await DeleteAgentAsync(args, queryPort, catalogCommandPort, skillRunnerPort, nyxClient, token, caller, ct),
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

        var entry = await queryPort.GetForCallerAsync(agentId.Trim(), caller, ct);
        if (entry is null)
            return JsonSerializer.Serialize(new { error = $"Agent '{agentId}' not found" });

        return SerializeAgentStatus(entry);
    }

    private async Task<string> DeleteAgentAsync(
        BuilderArgs args,
        IUserAgentCatalogQueryPort queryPort,
        IUserAgentCatalogCommandPort catalogCommandPort,
        ISkillRunnerCommandPort skillRunnerPort,
        NyxIdApiClient nyxClient,
        string token,
        OwnerScope caller,
        CancellationToken ct)
    {
        var agentId = args.Str("agent_id");
        if (string.IsNullOrWhiteSpace(agentId))
            return """{"error":"agent_id is required for delete_agent"}""";

        var entry = await queryPort.GetForCallerAsync(agentId.Trim(), caller, ct);
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

        var disableResult = await TryDispatchLifecycleAsync(
            entry, "delete_agent", LifecycleAction.Disable, revisionFeedback: null,
            skillRunnerPort, ct);
        if (disableResult.error != null)
            return disableResult.error;

        if (!string.IsNullOrWhiteSpace(entry.ApiKeyId))
            await nyxClient.DeleteApiKeyAsync(token, entry.ApiKeyId, ct);

        // Refactor (iter4/cluster-009):
        //   Old pattern: Delete mapped command-port Observed to a synchronous deleted status.
        //   New principle: Tombstone ACK is accepted-only; deletion visibility is confirmed by the catalog query path.
        await catalogCommandPort.TombstoneAsync(entry.AgentId, ct);

        var agents = await QueryAgentsForCallerAsync(queryPort, caller, ct);

        return JsonSerializer.Serialize(new
        {
            status = "accepted",
            agent_id = entry.AgentId,
            revoked_api_key_id = entry.ApiKeyId,
            delete_notice = $"Delete submitted for `{entry.AgentId}`. Revoked API key: `{entry.ApiKeyId ?? "n/a"}`.",
            agents,
            total = agents.Length,
            note = "Tombstone is propagating. Run /agents in a few seconds to confirm the agent is gone.",
        });
    }

    private async Task<string> RunAgentAsync(
        BuilderArgs args,
        IUserAgentCatalogQueryPort queryPort,
        ISkillRunnerCommandPort skillRunnerPort,
        OwnerScope caller,
        CancellationToken ct)
    {
        var agentId = args.Str("agent_id");
        if (string.IsNullOrWhiteSpace(agentId))
            return """{"error":"agent_id is required for run_agent"}""";

        var entry = await queryPort.GetForCallerAsync(agentId.Trim(), caller, ct);
        if (entry is null)
            return JsonSerializer.Serialize(new { error = $"Agent '{agentId}' not found" });

        if (!SupportsManagedLifecycle(entry.AgentType))
            return JsonSerializer.Serialize(new { error = $"Agent '{entry.AgentId}' does not support run_agent" });

        if (string.Equals(entry.Status, SkillRunnerDefaults.StatusDisabled, StringComparison.Ordinal))
            return JsonSerializer.Serialize(new { error = $"Agent '{entry.AgentId}' is disabled. Enable it before running." });

        var revisionFeedback = NormalizeOptional(args.Str("revision_feedback"));
        var dispatch = await TryDispatchLifecycleAsync(entry, "run_agent", LifecycleAction.Run, revisionFeedback, skillRunnerPort, ct);
        if (dispatch.error != null)
            return dispatch.error;

        return JsonSerializer.Serialize(new
        {
            status = "accepted",
            agent_id = entry.AgentId,
            template = entry.TemplateName,
            note = revisionFeedback is null
                ? "Manual run dispatched."
                : "Manual run dispatched with revision feedback.",
        });
    }

    private async Task<string> DisableAgentAsync(
        BuilderArgs args,
        IUserAgentCatalogQueryPort queryPort,
        ISkillRunnerCommandPort skillRunnerPort,
        OwnerScope caller,
        CancellationToken ct)
    {
        var entry = await RequireManagedAgentAsync(args, queryPort, caller, "disable_agent", ct);
        if (entry.error != null)
            return entry.error;

        if (string.Equals(entry.value!.Status, SkillRunnerDefaults.StatusDisabled, StringComparison.Ordinal))
            return SerializeAgentStatus(entry.value, "Agent is already disabled.");

        // Refactor (iter1/cluster-002):
        //   Old pattern: Captured readmodel version, dispatched lifecycle, then delayed-looped for projected status.
        //   New principle: Lifecycle commands return accepted; freshness is observed by follow-up query or push event.
        var dispatch = await TryDispatchLifecycleAsync(entry.value, "disable_agent", LifecycleAction.Disable, null, skillRunnerPort, ct);
        if (dispatch.error != null)
            return dispatch.error;

        return SerializeAgentStatus(entry.value, "Disable accepted. Status update is propagating; run /agent-status to confirm the agent is paused.");
    }

    private async Task<string> EnableAgentAsync(
        BuilderArgs args,
        IUserAgentCatalogQueryPort queryPort,
        ISkillRunnerCommandPort skillRunnerPort,
        OwnerScope caller,
        CancellationToken ct)
    {
        var entry = await RequireManagedAgentAsync(args, queryPort, caller, "enable_agent", ct);
        if (entry.error != null)
            return entry.error;

        if (string.Equals(entry.value!.Status, SkillRunnerDefaults.StatusRunning, StringComparison.Ordinal))
            return SerializeAgentStatus(entry.value, "Agent is already enabled.");

        // Refactor (iter1/cluster-002):
        //   Old pattern: Captured readmodel version, dispatched lifecycle, then delayed-looped for projected status.
        //   New principle: Lifecycle commands return accepted; freshness is observed by follow-up query or push event.
        var dispatch = await TryDispatchLifecycleAsync(entry.value, "enable_agent", LifecycleAction.Enable, null, skillRunnerPort, ct);
        if (dispatch.error != null)
            return dispatch.error;

        return SerializeAgentStatus(entry.value, "Enable accepted. Status update is propagating; run /agent-status to confirm the agent is running.");
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
        var entries = await queryPort.QueryByCallerAsync(caller, ct);
        return entries
            .Select(static x => new
            {
                agent_id = x.AgentId,
                agent_type = x.AgentType,
                template = x.TemplateName,
                status = x.Status,
                schedule_cron = x.ScheduleCron,
                schedule_timezone = x.ScheduleTimezone,
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

        var entry = await queryPort.GetForCallerAsync(agentId.Trim(), caller, ct);
        if (entry is null)
            return (null, JsonSerializer.Serialize(new { error = $"Agent '{agentId}' not found" }));

        if (!SupportsManagedLifecycle(entry.AgentType))
            return (null, JsonSerializer.Serialize(new { error = $"Agent '{entry.AgentId}' does not support {actionName}" }));

        return (entry, null);
    }

    private static async Task<(bool success, string? error)> TryDispatchLifecycleAsync(
        UserAgentCatalogReadModelEntry entry,
        string reason,
        LifecycleAction action,
        string? revisionFeedback,
        ISkillRunnerCommandPort skillRunnerPort,
        CancellationToken ct)
    {
        if (!string.Equals(entry.AgentType, SkillRunnerDefaults.AgentType, StringComparison.Ordinal))
        {
            return (false, JsonSerializer.Serialize(new { error = $"Agent '{entry.AgentId}' does not support {action.ToString().ToLowerInvariant()}." }));
        }

        switch (action)
        {
            case LifecycleAction.Run:
                await skillRunnerPort.TriggerAsync(entry.AgentId, reason, ct);
                break;
            case LifecycleAction.Disable:
                await skillRunnerPort.DisableAsync(entry.AgentId, reason, ct);
                break;
            case LifecycleAction.Enable:
                await skillRunnerPort.EnableAsync(entry.AgentId, reason, ct);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
        _ = revisionFeedback; // SkillRunner doesn't accept revision feedback today; reserved for future surfaces.
        return (true, null);
    }

    private static bool SupportsManagedLifecycle(string? agentType) =>
        string.Equals(agentType, SkillRunnerDefaults.AgentType, StringComparison.Ordinal);

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
