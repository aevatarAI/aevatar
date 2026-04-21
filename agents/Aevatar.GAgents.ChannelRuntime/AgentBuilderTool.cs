using System.Net;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
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
        "Actions: list_templates, create_agent, list_agents, agent_status, run_agent, disable_agent, enable_agent, delete_agent.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list_templates", "create_agent", "list_agents", "agent_status", "run_agent", "disable_agent", "enable_agent", "delete_agent"]
            },
            "template": {
              "type": "string",
              "description": "Template name, currently supports daily_report and social_media"
            },
            "agent_id": {
              "type": "string",
              "description": "Optional stable actor ID. Auto-generated when omitted."
            },
            "github_username": {
              "type": "string",
              "description": "GitHub username for the daily_report template"
            },
            "topic": {
              "type": "string",
              "description": "Primary topic or campaign focus for the social_media template"
            },
            "audience": {
              "type": "string",
              "description": "Optional audience descriptor for the social_media template"
            },
            "style": {
              "type": "string",
              "description": "Optional tone/style instruction for the social_media template"
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
            },
            "revision_feedback": {
              "type": "string",
              "description": "Optional revision guidance to include in the next workflow-backed run"
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
            return JsonSerializer.Serialize(new { templates = AgentBuilderTemplates.ListTemplates() });

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
            "disable_agent" => await DisableAgentAsync(args, queryPort, actorRuntime, ct),
            "enable_agent" => await EnableAgentAsync(args, queryPort, actorRuntime, ct),
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
        return template.ToLowerInvariant() switch
        {
            "daily_report" => await CreateDailyReportAgentAsync(args, queryPort, actorRuntime, nyxClient, token, ct),
            "social_media" => await CreateSocialMediaAgentAsync(args, queryPort, actorRuntime, nyxClient, token, ct),
            _ => JsonSerializer.Serialize(new { error = $"Unsupported template '{template}'. Supported templates: daily_report, social_media." }),
        };
    }

    private async Task<string> CreateDailyReportAgentAsync(
        BuilderArgs args,
        IAgentRegistryQueryPort queryPort,
        IActorRuntime actorRuntime,
        NyxIdApiClient nyxClient,
        string token,
        CancellationToken ct)
    {
        if (!AgentBuilderTemplates.TryBuildDailyReportSpec(
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
        if (!ChannelScheduleCalculator.TryGetNextOccurrence(scheduleCron, scheduleTimezone, DateTimeOffset.UtcNow, out var nextRunAtUtc, out var cronError))
            return JsonSerializer.Serialize(new { error = $"Invalid schedule: {cronError}" });

        var conversationId = args.Str("conversation_id")
            ?? AgentToolRequestContext.TryGet(ChannelMetadataKeys.ConversationId);
        if (string.IsNullOrWhiteSpace(conversationId))
            return """{"error":"conversation_id is required when no current channel conversation is available"}""";

        var ownerNyxUserId = await ResolveCurrentUserIdAsync(nyxClient, token, ct);
        if (string.IsNullOrWhiteSpace(ownerNyxUserId))
            return """{"error":"Could not resolve current NyxID user id"}""";

        var gitHubAuthorizationResponse = await BuildGitHubAuthorizationResponseAsync(nyxClient, token, ct);
        if (!string.IsNullOrWhiteSpace(gitHubAuthorizationResponse))
            return gitHubAuthorizationResponse;

        var providerSlug = (args.Str("nyx_provider_slug") ?? "api-lark-bot").Trim();
        var requiredServiceIds = await ResolveProxyServiceIdsAsync(nyxClient, token, templateSpec!.RequiredServiceSlugs, ct);
        if (requiredServiceIds.error != null)
            return JsonSerializer.Serialize(new { error = requiredServiceIds.error });

        var agentId = string.IsNullOrWhiteSpace(args.Str("agent_id"))
            ? SkillRunnerDefaults.GenerateActorId()
            : args.Str("agent_id")!.Trim();

        var createKeyResponse = await nyxClient.CreateApiKeyAsync(
            token,
            BuildCreateApiKeyPayload(agentId, requiredServiceIds.value!),
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

        await EnsureAgentRegistryProjectionAsync(ct);
        var confirmed = await WaitForCreatedAgentAsync(
            queryPort,
            agentId,
            versionBefore,
            entry => string.Equals(entry.AgentType, SkillRunnerDefaults.AgentType, StringComparison.Ordinal) &&
                     string.Equals(entry.TemplateName, templateSpec.TemplateName, StringComparison.Ordinal),
            ct,
            maxAttempts: args.Bool("run_immediately") == true ? 20 : 10);

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

    private async Task<string> CreateSocialMediaAgentAsync(
        BuilderArgs args,
        IAgentRegistryQueryPort queryPort,
        IActorRuntime actorRuntime,
        NyxIdApiClient nyxClient,
        string token,
        CancellationToken ct)
    {
        var scopeId = AgentToolRequestContext.TryGet("scope_id");
        if (string.IsNullOrWhiteSpace(scopeId))
            return """{"error":"scope_id is required for the social_media template"}""";

        var workflowCommandPort = _serviceProvider.GetService<IScopeWorkflowCommandPort>();
        if (workflowCommandPort is null)
            return """{"error":"Scope workflow command port is not registered."}""";

        var scheduleCron = args.Str("schedule_cron");
        if (string.IsNullOrWhiteSpace(scheduleCron))
            return """{"error":"schedule_cron is required for create_agent"}""";

        var scheduleTimezone = args.Str("schedule_timezone") ?? WorkflowAgentDefaults.DefaultTimezone;
        if (!ChannelScheduleCalculator.TryGetNextOccurrence(scheduleCron, scheduleTimezone, DateTimeOffset.UtcNow, out var nextRunAtUtc, out var cronError))
            return JsonSerializer.Serialize(new { error = $"Invalid schedule: {cronError}" });

        var conversationId = args.Str("conversation_id")
            ?? AgentToolRequestContext.TryGet(ChannelMetadataKeys.ConversationId);
        if (string.IsNullOrWhiteSpace(conversationId))
            return """{"error":"conversation_id is required when no current channel conversation is available"}""";

        var ownerNyxUserId = await ResolveCurrentUserIdAsync(nyxClient, token, ct);
        if (string.IsNullOrWhiteSpace(ownerNyxUserId))
            return """{"error":"Could not resolve current NyxID user id"}""";

        var providerSlug = (args.Str("nyx_provider_slug") ?? "api-lark-bot").Trim();
        var requiredServiceIds = await ResolveProxyServiceIdsAsync(nyxClient, token, [providerSlug], ct);
        if (requiredServiceIds.error != null)
            return JsonSerializer.Serialize(new { error = requiredServiceIds.error });

        var agentId = string.IsNullOrWhiteSpace(args.Str("agent_id"))
            ? WorkflowAgentDefaults.GenerateActorId()
            : args.Str("agent_id")!.Trim();

        if (!AgentBuilderTemplates.TryBuildSocialMediaSpec(
                agentId,
                args.Str("topic") ?? string.Empty,
                args.Str("audience"),
                args.Str("style"),
                out var templateSpec,
                out var templateError))
        {
            return JsonSerializer.Serialize(new { error = templateError });
        }

        var createKeyResponse = await nyxClient.CreateApiKeyAsync(
            token,
            BuildCreateApiKeyPayload(agentId, requiredServiceIds.value!),
            ct);

        if (IsErrorPayload(createKeyResponse))
            return createKeyResponse;

        if (!TryParseApiKeyCreateResponse(createKeyResponse, out var apiKeyId, out var apiKeyValue, out var apiKeyError))
            return JsonSerializer.Serialize(new { error = apiKeyError });

        var workflowUpsert = await workflowCommandPort.UpsertAsync(
            new ScopeWorkflowUpsertRequest(
                scopeId.Trim(),
                templateSpec!.WorkflowId,
                templateSpec.WorkflowYaml,
                templateSpec.WorkflowName,
                templateSpec.DisplayName),
            ct);

        var actor = await actorRuntime.GetAsync(agentId)
                    ?? await actorRuntime.CreateAsync<WorkflowAgentGAgent>(agentId, ct);

        var versionBefore = await queryPort.GetStateVersionAsync(agentId, ct) ?? -1;
        var initialize = new InitializeWorkflowAgentCommand
        {
            WorkflowId = workflowUpsert.Workflow.WorkflowId,
            WorkflowName = templateSpec.WorkflowName,
            WorkflowActorId = workflowUpsert.Workflow.ActorId,
            ExecutionPrompt = templateSpec.ExecutionPrompt,
            ScheduleCron = scheduleCron.Trim(),
            ScheduleTimezone = scheduleTimezone.Trim(),
            ConversationId = conversationId.Trim(),
            NyxProviderSlug = providerSlug,
            NyxApiKey = apiKeyValue!,
            OwnerNyxUserId = ownerNyxUserId!,
            ApiKeyId = apiKeyId!,
            Enabled = true,
            ScopeId = scopeId.Trim(),
        };

        await actor.HandleEventAsync(BuildDirectEnvelope(actor.Id, initialize), ct);

        await EnsureAgentRegistryProjectionAsync(ct);
        var confirmed = await WaitForCreatedAgentAsync(
            queryPort,
            agentId,
            versionBefore,
            entry => string.Equals(entry.AgentType, WorkflowAgentDefaults.AgentType, StringComparison.Ordinal) &&
                     string.Equals(entry.TemplateName, WorkflowAgentDefaults.TemplateName, StringComparison.Ordinal),
            ct,
            maxAttempts: args.Bool("run_immediately") == true ? 20 : 10);

        if (args.Bool("run_immediately") == true && confirmed)
        {
            await actor.HandleEventAsync(
                BuildDirectEnvelope(actor.Id, new TriggerWorkflowAgentExecutionCommand { Reason = "create_agent" }),
                ct);
        }

        return JsonSerializer.Serialize(new
        {
            status = confirmed ? "created" : "accepted",
            agent_id = agentId,
            agent_type = WorkflowAgentDefaults.AgentType,
            template = WorkflowAgentDefaults.TemplateName,
            next_scheduled_run = nextRunAtUtc,
            conversation_id = conversationId,
            workflow_id = workflowUpsert.Workflow.WorkflowId,
            workflow_actor_id = workflowUpsert.Workflow.ActorId,
            api_key_id = apiKeyId,
            note = confirmed
                ? string.Empty
                : args.Bool("run_immediately") == true
                    ? "Agent initialization accepted but registry projection is not yet confirmed, so the immediate run was not triggered. Use Run Now after the agent appears."
                    : "Agent initialization accepted but registry projection is not yet confirmed.",
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
        var agents = await QueryAgentsForOwnerAsync(queryPort, ownerFilter, ct);

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

        return SerializeAgentStatus(entry);
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

        var disableResult = await DispatchAgentLifecycleAsync(entry, actorRuntime, "delete_agent", LifecycleAction.Disable, null, ct);
        if (disableResult.error != null)
            return disableResult.error;

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
                var ownerFilter = !string.IsNullOrWhiteSpace(entry.OwnerNyxUserId)
                    ? entry.OwnerNyxUserId
                    : await ResolveCurrentUserIdAsync(nyxClient, token, ct);
                var agents = await QueryAgentsForOwnerAsync(queryPort, ownerFilter, ct);
                return JsonSerializer.Serialize(new
                {
                    status = "deleted",
                    agent_id = entry.AgentId,
                    revoked_api_key_id = entry.ApiKeyId,
                    delete_notice = $"Deleted agent `{entry.AgentId}`. Revoked API key: `{entry.ApiKeyId ?? "n/a"}`.",
                    agents,
                    total = agents.Length,
                });
            }
        }

        var acceptedOwnerFilter = !string.IsNullOrWhiteSpace(entry.OwnerNyxUserId)
            ? entry.OwnerNyxUserId
            : await ResolveCurrentUserIdAsync(nyxClient, token, ct);
        var acceptedAgents = await QueryAgentsForOwnerAsync(queryPort, acceptedOwnerFilter, ct);
        return JsonSerializer.Serialize(new
        {
            status = "accepted",
            agent_id = entry.AgentId,
            revoked_api_key_id = entry.ApiKeyId,
            delete_notice = $"Delete submitted for `{entry.AgentId}`. Revoked API key: `{entry.ApiKeyId ?? "n/a"}`.",
            agents = acceptedAgents,
            total = acceptedAgents.Length,
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

        if (!SupportsManagedLifecycle(entry.AgentType))
            return JsonSerializer.Serialize(new { error = $"Agent '{entry.AgentId}' does not support run_agent" });

        if (string.Equals(entry.Status, SkillRunnerDefaults.StatusDisabled, StringComparison.Ordinal) ||
            string.Equals(entry.Status, WorkflowAgentDefaults.StatusDisabled, StringComparison.Ordinal))
            return JsonSerializer.Serialize(new { error = $"Agent '{entry.AgentId}' is disabled. Enable it before running." });

        var revisionFeedback = NormalizeOptional(args.Str("revision_feedback"));
        var dispatch = await DispatchAgentLifecycleAsync(entry, actorRuntime, "run_agent", LifecycleAction.Run, revisionFeedback, ct);
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
        IAgentRegistryQueryPort queryPort,
        IActorRuntime actorRuntime,
        CancellationToken ct)
    {
        var entry = await RequireManagedAgentAsync(args, queryPort, "disable_agent", ct);
        if (entry.error != null)
            return entry.error;

        if (string.Equals(entry.value!.Status, SkillRunnerDefaults.StatusDisabled, StringComparison.Ordinal) ||
            string.Equals(entry.value.Status, WorkflowAgentDefaults.StatusDisabled, StringComparison.Ordinal))
            return SerializeAgentStatus(entry.value, "Agent is already disabled.");

        var dispatch = await DispatchAgentLifecycleAsync(entry.value, actorRuntime, "disable_agent", LifecycleAction.Disable, null, ct);
        if (dispatch.error != null)
            return dispatch.error;

        var after = await WaitForAgentStatusAsync(queryPort, entry.value.AgentId, SkillRunnerDefaults.StatusDisabled, ct) ?? entry.value;
        return SerializeAgentStatus(after, "Agent disabled. Scheduling paused.");
    }

    private async Task<string> EnableAgentAsync(
        BuilderArgs args,
        IAgentRegistryQueryPort queryPort,
        IActorRuntime actorRuntime,
        CancellationToken ct)
    {
        var entry = await RequireManagedAgentAsync(args, queryPort, "enable_agent", ct);
        if (entry.error != null)
            return entry.error;

        if (string.Equals(entry.value!.Status, SkillRunnerDefaults.StatusRunning, StringComparison.Ordinal) ||
            string.Equals(entry.value.Status, WorkflowAgentDefaults.StatusRunning, StringComparison.Ordinal))
            return SerializeAgentStatus(entry.value, "Agent is already enabled.");

        var dispatch = await DispatchAgentLifecycleAsync(entry.value, actorRuntime, "enable_agent", LifecycleAction.Enable, null, ct);
        if (dispatch.error != null)
            return dispatch.error;

        var after = await WaitForAgentStatusAsync(queryPort, entry.value.AgentId, SkillRunnerDefaults.StatusRunning, ct) ?? entry.value;
        return SerializeAgentStatus(after, "Agent enabled. Scheduling resumed.");
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
        if (requiredServiceIds.Count == 0)
            throw new InvalidOperationException("requiredServiceIds must not be empty.");

        var payload = new Dictionary<string, object?>
        {
            ["name"] = $"aevatar-agent-{agentId}",
            ["scopes"] = "proxy",
            ["platform"] = "generic",
            ["allowed_service_ids"] = requiredServiceIds,
        };

        return JsonSerializer.Serialize(payload);
    }

    private static string SerializeAgentStatus(AgentRegistryEntry entry, string? note = null)
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

    private async Task<object[]> QueryAgentsForOwnerAsync(
        IAgentRegistryQueryPort queryPort,
        string? ownerFilter,
        CancellationToken ct)
    {
        var entries = await queryPort.QueryAllAsync(ct);
        return entries
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
            .Cast<object>()
            .ToArray();
    }

    private async Task<(AgentRegistryEntry? value, string? error)> RequireManagedAgentAsync(
        BuilderArgs args,
        IAgentRegistryQueryPort queryPort,
        string actionName,
        CancellationToken ct)
    {
        var agentId = args.Str("agent_id");
        if (string.IsNullOrWhiteSpace(agentId))
            return (null, $$"""{"error":"agent_id is required for {{actionName}}"}""");

        var entry = await queryPort.GetAsync(agentId.Trim(), ct);
        if (entry is null)
            return (null, JsonSerializer.Serialize(new { error = $"Agent '{agentId}' not found" }));

        if (!SupportsManagedLifecycle(entry.AgentType))
            return (null, JsonSerializer.Serialize(new { error = $"Agent '{entry.AgentId}' does not support {actionName}" }));

        return (entry, null);
    }

    private async Task<bool> WaitForCreatedAgentAsync(
        IAgentRegistryQueryPort queryPort,
        string agentId,
        long versionBefore,
        Func<AgentRegistryEntry, bool> predicate,
        CancellationToken ct,
        int maxAttempts = 10,
        int delayMilliseconds = 500)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(delayMilliseconds, ct);

            var versionAfter = await queryPort.GetStateVersionAsync(agentId, ct) ?? -1;
            if (versionAfter <= versionBefore)
                continue;

            var entry = await queryPort.GetAsync(agentId, ct);
            if (entry != null && predicate(entry))
                return true;
        }

        return false;
    }

    private async Task EnsureAgentRegistryProjectionAsync(CancellationToken ct)
    {
        var projectionPort = _serviceProvider.GetService<AgentRegistryProjectionPort>();
        if (projectionPort is null)
            return;

        await projectionPort.EnsureProjectionForActorAsync(AgentRegistryGAgent.WellKnownId, ct);
    }

    private async Task<AgentRegistryEntry?> WaitForAgentStatusAsync(
        IAgentRegistryQueryPort queryPort,
        string agentId,
        string expectedStatus,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(500, ct);

            var entry = await queryPort.GetAsync(agentId, ct);
            if (entry != null && string.Equals(entry.Status, expectedStatus, StringComparison.Ordinal))
                return entry;
        }

        return await queryPort.GetAsync(agentId, ct);
    }

    private async Task<(bool success, string? error)> DispatchAgentLifecycleAsync(
        AgentRegistryEntry entry,
        IActorRuntime actorRuntime,
        string reason,
        LifecycleAction action,
        string? revisionFeedback,
        CancellationToken ct)
    {
        if (string.Equals(entry.AgentType, SkillRunnerDefaults.AgentType, StringComparison.Ordinal))
        {
            var actor = await actorRuntime.GetAsync(entry.AgentId)
                        ?? await actorRuntime.CreateAsync<SkillRunnerGAgent>(entry.AgentId, ct);

            IMessage payload = action switch
            {
                LifecycleAction.Run => new TriggerSkillRunnerExecutionCommand { Reason = reason },
                LifecycleAction.Disable => new DisableSkillRunnerCommand { Reason = reason },
                LifecycleAction.Enable => new EnableSkillRunnerCommand { Reason = reason },
                _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
            };

            await actor.HandleEventAsync(BuildDirectEnvelope(actor.Id, payload), ct);
            return (true, null);
        }

        if (string.Equals(entry.AgentType, WorkflowAgentDefaults.AgentType, StringComparison.Ordinal))
        {
            var actor = await actorRuntime.GetAsync(entry.AgentId)
                        ?? await actorRuntime.CreateAsync<WorkflowAgentGAgent>(entry.AgentId, ct);

            IMessage payload = action switch
            {
                LifecycleAction.Run => new TriggerWorkflowAgentExecutionCommand
                {
                    Reason = reason,
                    RevisionFeedback = revisionFeedback?.Trim() ?? string.Empty,
                },
                LifecycleAction.Disable => new DisableWorkflowAgentCommand { Reason = reason },
                LifecycleAction.Enable => new EnableWorkflowAgentCommand { Reason = reason },
                _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
            };

            await actor.HandleEventAsync(BuildDirectEnvelope(actor.Id, payload), ct);
            return (true, null);
        }

        return (false, JsonSerializer.Serialize(new { error = $"Agent '{entry.AgentId}' does not support {action.ToString().ToLowerInvariant()}." }));
    }

    private static bool SupportsManagedLifecycle(string? agentType) =>
        string.Equals(agentType, SkillRunnerDefaults.AgentType, StringComparison.Ordinal) ||
        string.Equals(agentType, WorkflowAgentDefaults.AgentType, StringComparison.Ordinal);

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

    private async Task<(IReadOnlyList<string>? value, string? error)> ResolveProxyServiceIdsAsync(
        NyxIdApiClient client,
        string token,
        IReadOnlyList<string> requiredSlugs,
        CancellationToken ct)
    {
        if (requiredSlugs.Count == 0)
            return (null, "At least one required Nyx proxy service slug must be provided.");

        var response = await client.DiscoverProxyServicesAsync(token, ct);
        if (IsErrorPayload(response))
            return (null, "Could not discover required Nyx proxy services.");

        try
        {
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return (null, "Nyx proxy service discovery did not return an array.");

            var serviceIdsBySlug = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var svc in doc.RootElement.EnumerateArray())
            {
                var slug = ReadString(svc, "slug");
                if (string.IsNullOrWhiteSpace(slug))
                    continue;

                var id = ReadString(svc, "id", "service_id");
                if (!string.IsNullOrWhiteSpace(id))
                    serviceIdsBySlug[slug] = id;
            }

            var missingSlugs = requiredSlugs
                .Where(slug => !serviceIdsBySlug.ContainsKey(slug))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (missingSlugs.Length > 0)
            {
                return (null,
                    $"Missing required Nyx proxy services: {string.Join(", ", missingSlugs)}. API key creation was rejected to avoid broad proxy access.");
            }

            var ids = requiredSlugs
                .Select(slug => serviceIdsBySlug[slug])
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return (ids, null);
        }
        catch (JsonException)
        {
            return (null, "Could not parse Nyx proxy service discovery response.");
        }
    }

    private async Task<string?> BuildGitHubAuthorizationResponseAsync(
        NyxIdApiClient client,
        string token,
        CancellationToken ct)
    {
        var providerTokensResponse = await client.ListProviderTokensAsync(token, ct);
        if (IsErrorPayload(providerTokensResponse))
        {
            return JsonSerializer.Serialize(new
            {
                error = "Could not verify GitHub authorization status from NyxID providers.",
            });
        }

        if (HasConnectedGitHubProvider(providerTokensResponse))
            return null;

        var catalogResponse = await client.GetCatalogEntryAsync(token, "api-github", ct);
        if (IsErrorPayload(catalogResponse))
        {
            return JsonSerializer.Serialize(new
            {
                error = "GitHub provider configuration is not available in the NyxID catalog.",
            });
        }

        if (!TryParseGitHubCatalogEntry(
                catalogResponse,
                out var providerId,
                out var providerType,
                out var credentialMode,
                out var documentationUrl,
                out var catalogError))
            return JsonSerializer.Serialize(new { error = catalogError });

        if (!string.Equals(providerType, "oauth2", StringComparison.OrdinalIgnoreCase))
        {
            return JsonSerializer.Serialize(new
            {
                error = $"GitHub provider requires unsupported connection mode '{providerType ?? "unknown"}'.",
            });
        }

        if (string.Equals(credentialMode, "user", StringComparison.OrdinalIgnoreCase))
        {
            var credentialsResponse = await client.GetUserCredentialsAsync(token, providerId!, ct);
            if (IsErrorPayload(credentialsResponse))
                return credentialsResponse;

            if (!TryParseUserCredentialsStatus(credentialsResponse, out var hasCredentials, out var credentialsError))
                return JsonSerializer.Serialize(new { error = credentialsError });

            if (!hasCredentials)
            {
                return JsonSerializer.Serialize(new
                {
                    status = "credentials_required",
                    template = "daily_report",
                    provider = "GitHub",
                    provider_id = providerId,
                    documentation_url = documentationUrl,
                    note = "GitHub in NyxID uses user-managed OAuth app credentials. Set your GitHub OAuth app client_id/client_secret in NyxID first, then submit the daily report form again.",
                });
            }
        }

        var connectResponse = await client.InitiateOAuthConnectAsync(token, providerId!, ct);
        if (IsErrorPayload(connectResponse))
        {
            return JsonSerializer.Serialize(new
            {
                error = "Could not initiate GitHub OAuth connect in NyxID.",
            });
        }

        if (!TryParseAuthorizationUrl(connectResponse, out var authorizationUrl, out var authError))
            return JsonSerializer.Serialize(new { error = authError });

        return JsonSerializer.Serialize(new
        {
            status = "oauth_required",
            template = "daily_report",
            provider = "GitHub",
            provider_id = providerId,
            authorization_url = authorizationUrl,
            documentation_url = documentationUrl,
            note = "Connect GitHub in NyxID, then return to Feishu and submit the daily report form again.",
        });
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

    private static bool HasConnectedGitHubProvider(string response)
    {
        try
        {
            using var doc = JsonDocument.Parse(response);
            if (!doc.RootElement.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var element in tokens.EnumerateArray())
            {
                if (!LooksLikeGitHubProvider(element))
                    continue;

                return string.Equals(
                    NormalizeOptional(ReadString(element, "status")),
                    "active",
                    StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    private static bool TryParseGitHubCatalogEntry(
        string response,
        out string? providerId,
        out string? providerType,
        out string? credentialMode,
        out string? documentationUrl,
        out string? error)
    {
        providerId = null;
        providerType = null;
        credentialMode = null;
        documentationUrl = null;
        error = null;

        try
        {
            using var doc = JsonDocument.Parse(response);
            providerId = ReadStringDeep(doc.RootElement, 3, "provider_config_id", "provider_id");
            providerType = ReadStringDeep(doc.RootElement, 3, "provider_type");
            credentialMode = ReadStringDeep(doc.RootElement, 3, "credential_mode");
            documentationUrl = ReadStringDeep(doc.RootElement, 3, "documentation_url");

            if (string.IsNullOrWhiteSpace(providerId))
            {
                error = "GitHub catalog entry did not include provider_config_id.";
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

    private static bool TryParseUserCredentialsStatus(
        string response,
        out bool hasCredentials,
        out string? error)
    {
        hasCredentials = false;
        error = null;

        try
        {
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.TryGetProperty("has_credentials", out var property))
            {
                if (property.ValueKind == JsonValueKind.True)
                {
                    hasCredentials = true;
                    return true;
                }

                if (property.ValueKind == JsonValueKind.False)
                {
                    hasCredentials = false;
                    return true;
                }
            }

            error = "NyxID user credentials response did not include has_credentials.";
            return false;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryParseAuthorizationUrl(
        string response,
        out string? authorizationUrl,
        out string? error)
    {
        authorizationUrl = null;
        error = null;

        try
        {
            using var doc = JsonDocument.Parse(response);
            authorizationUrl = ReadStringDeep(doc.RootElement, 3, "authorization_url", "auth_url", "url");
            if (string.IsNullOrWhiteSpace(authorizationUrl))
            {
                error = "NyxID OAuth connect response did not include an authorization URL.";
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

    private static string? ReadString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

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

    private static string? ReadStringDeep(JsonElement element, int maxDepth, params string[] names)
    {
        var direct = ReadString(element, names);
        if (!string.IsNullOrWhiteSpace(direct) || maxDepth <= 0)
            return direct;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var nested = ReadStringDeep(property.Value, maxDepth - 1, names);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = ReadStringDeep(item, maxDepth - 1, names);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }

        return null;
    }

    private static bool LooksLikeGitHubProvider(JsonElement element)
    {
        foreach (var value in EnumerateStrings(
                     ReadStringDeep(element, 2, "provider_name", "name", "display_name", "slug", "provider", "service_slug")))
        {
            if (value.Contains("github", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> EnumerateStrings(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                yield return value;
        }
    }

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
