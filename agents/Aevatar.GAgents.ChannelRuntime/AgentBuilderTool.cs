using System.Net;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed class AgentBuilderTool : IAgentTool
{
    private readonly IServiceProvider _serviceProvider;

    public AgentBuilderTool(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public string Name => "agent_builder";

    public string Description =>
        "Create and manage persistent user-facing automation agents for the current channel context. " +
        "Actions: list_templates, create_agent, list_agents, agent_status, run_agent, delete_agent.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list_templates", "create_agent", "list_agents", "agent_status", "run_agent", "delete_agent"]
            },
            "template": {
              "type": "string",
              "description": "Template name, currently supports daily_report"
            },
            "agent_id": {
              "type": "string",
              "description": "Optional stable actor ID. Auto-generated when omitted."
            },
            "github_username": {
              "type": "string",
              "description": "GitHub username for the daily_report template"
            },
            "repositories": {
              "type": "string",
              "description": "Optional comma-separated repositories to prioritize"
            },
            "schedule_cron": {
              "type": "string",
              "description": "Cron expression for future executions"
            },
            "schedule_timezone": {
              "type": "string",
              "description": "IANA or system timezone ID (default: UTC)"
            },
            "conversation_id": {
              "type": "string",
              "description": "Override outbound conversation/chat ID. Defaults to current channel context."
            },
            "nyx_provider_slug": {
              "type": "string",
              "description": "Outbound Nyx proxy slug (default: api-lark-bot)"
            },
            "run_immediately": {
              "type": "boolean",
              "description": "When true, trigger one execution right after creation"
            },
            "confirm": {
              "type": "boolean",
              "description": "Must be true to execute delete_agent"
            }
          }
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

        var action = args.Str("action", "list_templates");
        if (string.Equals(action, "list_templates", StringComparison.Ordinal))
            return JsonSerializer.Serialize(new { templates = SkillRunnerTemplates.ListTemplates() });

        var queryPort = _serviceProvider.GetService<IAgentRegistryQueryPort>();
        var actorRuntime = _serviceProvider.GetService<IActorRuntime>();
        var nyxClient = _serviceProvider.GetService<NyxIdApiClient>();
        if (queryPort is null || actorRuntime is null || nyxClient is null)
        {
            return """{"error":"Agent builder runtime not available. Required services are not registered in DI."}""";
        }

        return action switch
        {
            "create_agent" => await CreateAgentAsync(args, queryPort, actorRuntime, nyxClient, token, ct),
            "list_agents" => await ListAgentsAsync(args, queryPort, nyxClient, token, ct),
            "agent_status" => await GetAgentStatusAsync(args, queryPort, ct),
            "run_agent" => await RunAgentAsync(args, queryPort, actorRuntime, ct),
            "delete_agent" => await DeleteAgentAsync(args, queryPort, actorRuntime, nyxClient, token, ct),
            _ => JsonSerializer.Serialize(new { error = $"Unsupported action '{action}'" }),
        };
    }

    private async Task<string> CreateAgentAsync(
        BuilderArgs args,
        IAgentRegistryQueryPort queryPort,
        IActorRuntime actorRuntime,
        NyxIdApiClient nyxClient,
        string token,
        CancellationToken ct)
    {
        var chatType = AgentToolRequestContext.TryGet(ChannelMetadataKeys.ChatType);
        if (!string.IsNullOrWhiteSpace(chatType) &&
            !string.Equals(chatType, "p2p", StringComparison.OrdinalIgnoreCase))
        {
            return """{"error":"Day One agent creation only supports private chat (chat_type=p2p)."}""";
        }

        var template = (args.Str("template") ?? string.Empty).Trim();
        if (!string.Equals(template, "daily_report", StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(new { error = $"Unsupported template '{template}'. Only daily_report is implemented in this PR." });

        if (!SkillRunnerTemplates.TryBuildDailyReportSpec(
                args.Str("github_username") ?? string.Empty,
                args.Str("repositories"),
                out var templateSpec,
                out var templateError))
        {
            return JsonSerializer.Serialize(new { error = templateError });
        }

        var scheduleCron = args.Str("schedule_cron");
        if (string.IsNullOrWhiteSpace(scheduleCron))
            return """{"error":"schedule_cron is required for create_agent"}""";

        var scheduleTimezone = args.Str("schedule_timezone") ?? SkillRunnerDefaults.DefaultTimezone;
        if (!SkillRunnerScheduleCalculator.TryGetNextOccurrence(scheduleCron, scheduleTimezone, DateTimeOffset.UtcNow, out var nextRunAtUtc, out var cronError))
            return JsonSerializer.Serialize(new { error = $"Invalid schedule: {cronError}" });

        var conversationId = args.Str("conversation_id")
            ?? AgentToolRequestContext.TryGet(ChannelMetadataKeys.ConversationId);
        if (string.IsNullOrWhiteSpace(conversationId))
            return """{"error":"conversation_id is required when no current channel conversation is available"}""";

        var ownerNyxUserId = await ResolveCurrentUserIdAsync(nyxClient, token, ct);
        if (string.IsNullOrWhiteSpace(ownerNyxUserId))
            return """{"error":"Could not resolve current NyxID user id"}""";

        var providerSlug = (args.Str("nyx_provider_slug") ?? "api-lark-bot").Trim();
        var requiredServiceIds = await ResolveProxyServiceIdsAsync(nyxClient, token, templateSpec!.RequiredServiceSlugs, ct);
        var agentId = string.IsNullOrWhiteSpace(args.Str("agent_id"))
            ? SkillRunnerDefaults.GenerateActorId()
            : args.Str("agent_id")!.Trim();

        var createKeyResponse = await nyxClient.CreateApiKeyAsync(
            token,
            BuildCreateApiKeyPayload(agentId, requiredServiceIds),
            ct);

        if (IsErrorPayload(createKeyResponse))
            return createKeyResponse;

        if (!TryParseApiKeyCreateResponse(createKeyResponse, out var apiKeyId, out var apiKeyValue, out var apiKeyError))
            return JsonSerializer.Serialize(new { error = apiKeyError });

        var actor = await actorRuntime.GetAsync(agentId)
                    ?? await actorRuntime.CreateAsync<SkillRunnerGAgent>(agentId, ct);

        var versionBefore = await queryPort.GetStateVersionAsync(agentId, ct) ?? -1;
        var initialize = new InitializeSkillRunnerCommand
        {
            SkillName = templateSpec.SkillName,
            TemplateName = templateSpec.TemplateName,
            SkillContent = templateSpec.SkillContent,
            ExecutionPrompt = templateSpec.ExecutionPrompt,
            ScheduleCron = scheduleCron.Trim(),
            ScheduleTimezone = scheduleTimezone.Trim(),
            Enabled = true,
            ScopeId = AgentToolRequestContext.TryGet("scope_id") ?? string.Empty,
            ProviderName = SkillRunnerDefaults.DefaultProviderName,
            MaxToolRounds = SkillRunnerDefaults.DefaultMaxToolRounds,
            MaxHistoryMessages = SkillRunnerDefaults.DefaultMaxHistoryMessages,
            OutboundConfig = new SkillRunnerOutboundConfig
            {
                ConversationId = conversationId.Trim(),
                NyxProviderSlug = providerSlug,
                NyxApiKey = apiKeyValue!,
                OwnerNyxUserId = ownerNyxUserId!,
                ApiKeyId = apiKeyId!,
            },
        };

        await actor.HandleEventAsync(BuildDirectEnvelope(actor.Id, initialize), ct);

        if (args.Bool("run_immediately") == true)
            await actor.HandleEventAsync(
                BuildDirectEnvelope(actor.Id, new TriggerSkillRunnerExecutionCommand { Reason = "create_agent" }),
                ct);

        var confirmed = false;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(500, ct);

            var versionAfter = await queryPort.GetStateVersionAsync(agentId, ct) ?? -1;
            if (versionAfter <= versionBefore)
                continue;

            var after = await queryPort.GetAsync(agentId, ct);
            if (after == null)
                continue;

            if (string.Equals(after.AgentType, SkillRunnerDefaults.AgentType, StringComparison.Ordinal) &&
                string.Equals(after.TemplateName, templateSpec.TemplateName, StringComparison.Ordinal))
            {
                confirmed = true;
                break;
            }
        }

        return JsonSerializer.Serialize(new
        {
            status = confirmed ? "created" : "accepted",
            agent_id = agentId,
            agent_type = SkillRunnerDefaults.AgentType,
            template = templateSpec.TemplateName,
            next_scheduled_run = nextRunAtUtc,
            conversation_id = conversationId,
            api_key_id = apiKeyId,
            note = confirmed ? "" : "Agent initialization accepted but registry projection is not yet confirmed.",
        });
    }

    private async Task<string> ListAgentsAsync(
        BuilderArgs args,
        IAgentRegistryQueryPort queryPort,
        NyxIdApiClient nyxClient,
        string token,
        CancellationToken ct)
    {
        var ownerFilter = args.Str("owner_nyx_user_id") ?? await ResolveCurrentUserIdAsync(nyxClient, token, ct);
        var entries = await queryPort.QueryAllAsync(ct);
        var agents = entries
            .Where(x => string.IsNullOrWhiteSpace(ownerFilter) || string.Equals(x.OwnerNyxUserId, ownerFilter, StringComparison.Ordinal))
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
            .ToArray();

        return JsonSerializer.Serialize(new { agents, total = agents.Length });
    }

    private async Task<string> GetAgentStatusAsync(
        BuilderArgs args,
        IAgentRegistryQueryPort queryPort,
        CancellationToken ct)
    {
        var agentId = args.Str("agent_id");
        if (string.IsNullOrWhiteSpace(agentId))
            return """{"error":"agent_id is required for agent_status"}""";

        var entry = await queryPort.GetAsync(agentId.Trim(), ct);
        if (entry is null)
            return JsonSerializer.Serialize(new { error = $"Agent '{agentId}' not found" });

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
        });
    }

    private async Task<string> DeleteAgentAsync(
        BuilderArgs args,
        IAgentRegistryQueryPort queryPort,
        IActorRuntime actorRuntime,
        NyxIdApiClient nyxClient,
        string token,
        CancellationToken ct)
    {
        var agentId = args.Str("agent_id");
        if (string.IsNullOrWhiteSpace(agentId))
            return """{"error":"agent_id is required for delete_agent"}""";

        var entry = await queryPort.GetAsync(agentId.Trim(), ct);
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

        if (string.Equals(entry.AgentType, SkillRunnerDefaults.AgentType, StringComparison.Ordinal))
        {
            var actor = await actorRuntime.GetAsync(entry.AgentId)
                        ?? await actorRuntime.CreateAsync<SkillRunnerGAgent>(entry.AgentId, ct);
            await actor.HandleEventAsync(
                BuildDirectEnvelope(actor.Id, new DisableSkillRunnerCommand { Reason = "delete_agent" }),
                ct);
        }

        if (!string.IsNullOrWhiteSpace(entry.ApiKeyId))
            await nyxClient.DeleteApiKeyAsync(token, entry.ApiKeyId, ct);

        var registryActor = await actorRuntime.GetAsync(AgentRegistryGAgent.WellKnownId)
                           ?? await actorRuntime.CreateAsync<AgentRegistryGAgent>(AgentRegistryGAgent.WellKnownId, ct);
        await registryActor.HandleEventAsync(
            BuildDirectEnvelope(registryActor.Id, new AgentRegistryTombstoneCommand { AgentId = entry.AgentId }),
            ct);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(500, ct);

            if (await queryPort.GetAsync(entry.AgentId, ct) == null)
            {
                return JsonSerializer.Serialize(new
                {
                    status = "deleted",
                    agent_id = entry.AgentId,
                    revoked_api_key_id = entry.ApiKeyId,
                });
            }
        }

        return JsonSerializer.Serialize(new
        {
            status = "accepted",
            agent_id = entry.AgentId,
            revoked_api_key_id = entry.ApiKeyId,
            note = "Delete was submitted but registry tombstone is not yet reflected.",
        });
    }

    private async Task<string> RunAgentAsync(
        BuilderArgs args,
        IAgentRegistryQueryPort queryPort,
        IActorRuntime actorRuntime,
        CancellationToken ct)
    {
        var agentId = args.Str("agent_id");
        if (string.IsNullOrWhiteSpace(agentId))
            return """{"error":"agent_id is required for run_agent"}""";

        var entry = await queryPort.GetAsync(agentId.Trim(), ct);
        if (entry is null)
            return JsonSerializer.Serialize(new { error = $"Agent '{agentId}' not found" });

        if (!string.Equals(entry.AgentType, SkillRunnerDefaults.AgentType, StringComparison.Ordinal))
            return JsonSerializer.Serialize(new { error = $"Agent '{entry.AgentId}' does not support run_agent" });

        var actor = await actorRuntime.GetAsync(entry.AgentId)
                    ?? await actorRuntime.CreateAsync<SkillRunnerGAgent>(entry.AgentId, ct);
        await actor.HandleEventAsync(
            BuildDirectEnvelope(actor.Id, new TriggerSkillRunnerExecutionCommand { Reason = "run_agent" }),
            ct);

        return JsonSerializer.Serialize(new
        {
            status = "accepted",
            agent_id = entry.AgentId,
            template = entry.TemplateName,
            note = "Manual run dispatched.",
        });
    }

    private static EventEnvelope BuildDirectEnvelope(string targetActorId, IMessage payload)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(payload),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = targetActorId },
            },
        };
    }

    private static string BuildCreateApiKeyPayload(string agentId, IReadOnlyList<string> requiredServiceIds)
    {
        var payload = new Dictionary<string, object?>
        {
            ["name"] = $"aevatar-agent-{agentId}",
            ["scopes"] = "proxy",
            ["platform"] = "generic",
        };

        if (requiredServiceIds.Count > 0)
        {
            payload["allowed_service_ids"] = requiredServiceIds;
        }
        else
        {
            payload["allow_all_services"] = true;
        }

        return JsonSerializer.Serialize(payload);
    }

    private async Task<string?> ResolveCurrentUserIdAsync(NyxIdApiClient client, string token, CancellationToken ct)
    {
        var response = await client.GetCurrentUserAsync(token, ct);
        if (IsErrorPayload(response))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.TryGetProperty("user", out var user))
                return ReadString(user, "id", "user_id", "sub");

            return ReadString(doc.RootElement, "id", "user_id", "sub");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<string>> ResolveProxyServiceIdsAsync(
        NyxIdApiClient client,
        string token,
        IReadOnlyList<string> requiredSlugs,
        CancellationToken ct)
    {
        if (requiredSlugs.Count == 0)
            return [];

        var response = await client.DiscoverProxyServicesAsync(token, ct);
        if (IsErrorPayload(response))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            var ids = new List<string>();
            foreach (var svc in doc.RootElement.EnumerateArray())
            {
                var slug = ReadString(svc, "slug");
                if (string.IsNullOrWhiteSpace(slug) ||
                    !requiredSlugs.Contains(slug, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var id = ReadString(svc, "id", "service_id");
                if (!string.IsNullOrWhiteSpace(id))
                    ids.Add(id);
            }

            return ids.Distinct(StringComparer.Ordinal).ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool TryParseApiKeyCreateResponse(
        string response,
        out string? apiKeyId,
        out string? apiKeyValue,
        out string? error)
    {
        apiKeyId = null;
        apiKeyValue = null;
        error = null;

        try
        {
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            apiKeyId = ReadString(root, "id", "api_key_id");
            apiKeyValue = ReadString(root, "full_key", "api_key", "token");

            if ((string.IsNullOrWhiteSpace(apiKeyId) || string.IsNullOrWhiteSpace(apiKeyValue)) &&
                root.TryGetProperty("api_key", out var nested))
            {
                apiKeyId ??= ReadString(nested, "id", "api_key_id");
                apiKeyValue ??= ReadString(nested, "full_key", "token", "value");
            }

            if (string.IsNullOrWhiteSpace(apiKeyId) || string.IsNullOrWhiteSpace(apiKeyValue))
            {
                error = "NyxID API key response did not include both id and full_key.";
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool IsErrorPayload(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            return doc.RootElement.TryGetProperty("error", out var errorProp) &&
                   errorProp.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.String)
                return property.GetString();

            if (property.ValueKind == JsonValueKind.Number)
                return property.GetRawText();
        }

        return null;
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
}
