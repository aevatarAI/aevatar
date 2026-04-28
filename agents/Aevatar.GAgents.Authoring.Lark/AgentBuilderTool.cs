using System.Net;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Scheduled;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Authoring.Lark;

public sealed class AgentBuilderTool : IAgentTool
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AgentBuilderTool>? _logger;
    // Per-instance polling budget for actor -> projector -> document store
    // propagation. Defaults to ProjectionWaitDefaults (15 s); tests inject
    // shrunk values via the constructor instead of mutating a process-global,
    // which would race other tests if the test surface ever parallelizes.
    private readonly int _projectionWaitAttempts;
    private readonly int _projectionWaitDelayMilliseconds;

    public AgentBuilderTool(
        IServiceProvider serviceProvider,
        ILogger<AgentBuilderTool>? logger = null,
        int projectionWaitAttempts = ProjectionWaitDefaults.Attempts,
        int projectionWaitDelayMilliseconds = ProjectionWaitDefaults.DelayMilliseconds)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _projectionWaitAttempts = projectionWaitAttempts;
        _projectionWaitDelayMilliseconds = projectionWaitDelayMilliseconds;
    }

    public string Name => "agent_builder";

    public string Description =>
        "Create and manage persistent user-facing automation agents for the current channel context. " +
        "Actions: list_templates, create_agent, list_agents, agent_status, run_agent, disable_agent, enable_agent, delete_agent.";

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
            "save_github_username_preference": {
              "type": "boolean",
              "description": "When true, save github_username as the owner-scoped default preference after a successful daily_report creation"
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

        var queryPort = _serviceProvider.GetService<IUserAgentCatalogQueryPort>();
        var nyxClient = _serviceProvider.GetService<NyxIdApiClient>();
        var skillRunnerPort = _serviceProvider.GetService<ISkillRunnerCommandPort>();
        var workflowAgentPort = _serviceProvider.GetService<IWorkflowAgentCommandPort>();
        var catalogCommandPort = _serviceProvider.GetService<IUserAgentCatalogCommandPort>();
        var callerScopeResolver = _serviceProvider.GetService<ICallerScopeResolver>();
        if (queryPort is null || nyxClient is null ||
            skillRunnerPort is null || workflowAgentPort is null || catalogCommandPort is null ||
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

        return action switch
        {
            "create_agent" => await CreateAgentAsync(args, queryPort, skillRunnerPort, workflowAgentPort, nyxClient, token, caller, ct),
            "list_agents" => await ListAgentsAsync(queryPort, caller, ct),
            "agent_status" => await GetAgentStatusAsync(args, queryPort, caller, ct),
            "run_agent" => await RunAgentAsync(args, queryPort, skillRunnerPort, workflowAgentPort, caller, ct),
            "disable_agent" => await DisableAgentAsync(args, queryPort, skillRunnerPort, workflowAgentPort, caller, ct),
            "enable_agent" => await EnableAgentAsync(args, queryPort, skillRunnerPort, workflowAgentPort, caller, ct),
            "delete_agent" => await DeleteAgentAsync(args, queryPort, catalogCommandPort, skillRunnerPort, workflowAgentPort, nyxClient, token, caller, ct),
            _ => JsonSerializer.Serialize(new { error = $"Unsupported action '{action}'" }),
        };
    }

    private async Task<string> CreateAgentAsync(
        BuilderArgs args,
        IUserAgentCatalogQueryPort queryPort,
        ISkillRunnerCommandPort skillRunnerPort,
        IWorkflowAgentCommandPort workflowAgentPort,
        NyxIdApiClient nyxClient,
        string token,
        OwnerScope caller,
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
            "daily_report" => await CreateDailyReportAgentAsync(args, queryPort, skillRunnerPort, nyxClient, token, caller, ct),
            "social_media" => await CreateSocialMediaAgentAsync(args, queryPort, workflowAgentPort, nyxClient, token, caller, ct),
            _ => JsonSerializer.Serialize(new { error = $"Unsupported template '{template}'. Supported templates: daily_report, social_media." }),
        };
    }

    private async Task<string> CreateDailyReportAgentAsync(
        BuilderArgs args,
        IUserAgentCatalogQueryPort queryPort,
        ISkillRunnerCommandPort skillRunnerPort,
        NyxIdApiClient nyxClient,
        string token,
        OwnerScope caller,
        CancellationToken ct)
    {
        var rawScopeId = NormalizeOptional(AgentToolRequestContext.TryGet(ChannelMetadataKeys.RegistrationScopeId));
        var configScopeId = NormalizeScopeId(rawScopeId);
        // Bot's RegistrationScopeId is per-NyxID-account (one bot = one scope), so multiple
        // Lark users sharing one bot would otherwise share a single UserConfigGAgent and
        // overwrite each other's saved github_username (issue #436). Compose a per-end-user
        // scope from the channel sender for personal-preference reads/writes only;
        // SkillRunner.ScopeId stays bot-scoped for downstream NyxID-tenant tools.
        var userConfigScopeId = ChannelUserConfigScope.FromMetadata(AgentToolRequestContext.CurrentMetadata);
        var githubUsernameResolution = await ResolveDailyReportGithubUsernameAsync(
            args,
            nyxClient,
            token,
            userConfigScopeId,
            ct);
        if (githubUsernameResolution.ErrorResponse is not null)
            return githubUsernameResolution.ErrorResponse;

        if (!AgentBuilderTemplates.TryBuildDailyReportSpec(
                githubUsernameResolution.GithubUsername ?? string.Empty,
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

        var ownerNyxUserId = caller.NyxUserId;

        var gitHubAuthorizationResponse = await BuildGitHubAuthorizationResponseAsync(nyxClient, token, ct);
        if (!string.IsNullOrWhiteSpace(gitHubAuthorizationResponse))
            return gitHubAuthorizationResponse;

        var providerSlug = (args.Str("nyx_provider_slug") ?? "api-lark-bot").Trim();
        var requiredServiceIds = await ResolveProxyServiceIdsAsync(nyxClient, token, templateSpec!.RequiredServiceSlugs, ct);
        if (requiredServiceIds.errorJson != null)
            return requiredServiceIds.errorJson;

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

        // Issue aevatarAI/aevatar#411 / #417 follow-up: catch in-flight GitHub-side issues.
        // The earlier `BuildGitHubAuthorizationResponseAsync` check covers the "no provider
        // token at all" case; this preflight catches misconfigurations that only surface at
        // request time (the original case under #421 was a missing `User-Agent` header that
        // GitHub rejects with 403; OAuth grant revocation is the other one).
        //
        // PR #418 review r3141846175: revoke the freshly-minted key on preflight failure so
        // each `/daily` retry doesn't leave another orphan proxy-scoped key behind in the
        // user's NyxID account. The revoke is best-effort cleanup, not a safety claim about
        // the key's correctness.
        var preflight = await PreflightGitHubProxyAsync(nyxClient, apiKeyValue!, providerSlug, ct);
        if (preflight is not null)
        {
            await BestEffortRevokeApiKeyAsync(nyxClient, token, apiKeyId!, "github_preflight_failed", ct);
            return preflight;
        }

        // Pre-create version baseline. Use the caller-scoped version probe — for an agent
        // the caller is about to own (not yet existing), the probe returns null so
        // versionBefore stays at -1, which is what the create-confirmation wait expects.
        var versionBefore = await queryPort.GetStateVersionForCallerAsync(agentId, caller, ct) ?? -1;

        var deliveryTarget = ResolveDeliveryTarget(conversationId, agentId);
#pragma warning disable CS0612 // legacy fields written for rollback safety during owner_scope migration
        var outboundConfig = new SkillRunnerOutboundConfig
        {
            ConversationId = conversationId.Trim(),
            NyxProviderSlug = providerSlug,
            NyxApiKey = apiKeyValue!,
            OwnerNyxUserId = ownerNyxUserId!,
            Platform = caller.Platform,
            ApiKeyId = apiKeyId!,
            LarkReceiveId = deliveryTarget.Primary.ReceiveId,
            LarkReceiveIdType = deliveryTarget.Primary.ReceiveIdType,
            LarkReceiveIdFallback = deliveryTarget.Fallback?.ReceiveId ?? string.Empty,
            LarkReceiveIdTypeFallback = deliveryTarget.Fallback?.ReceiveIdType ?? string.Empty,
            OwnerScope = caller.Clone(),
        };
#pragma warning restore CS0612

        var initialize = new InitializeSkillRunnerCommand
        {
            SkillName = templateSpec.SkillName,
            TemplateName = templateSpec.TemplateName,
            SkillContent = templateSpec.SkillContent,
            ExecutionPrompt = templateSpec.ExecutionPrompt,
            ScheduleCron = scheduleCron.Trim(),
            ScheduleTimezone = scheduleTimezone.Trim(),
            Enabled = true,
            ScopeId = configScopeId,
            ProviderName = SkillRunnerDefaults.DefaultProviderName,
            MaxToolRounds = SkillRunnerDefaults.DefaultMaxToolRounds,
            MaxHistoryMessages = SkillRunnerDefaults.DefaultMaxHistoryMessages,
            OutboundConfig = outboundConfig,
        };

        var runImmediatelyRequested = args.Bool("run_immediately") == true;
        await skillRunnerPort.InitializeAsync(agentId, initialize, runImmediatelyRequested, ct);

        var confirmed = await WaitForCreatedAgentAsync(
            queryPort,
            agentId,
            caller,
            versionBefore,
            entry => string.Equals(entry.AgentType, SkillRunnerDefaults.AgentType, StringComparison.Ordinal) &&
                     string.Equals(entry.TemplateName, templateSpec.TemplateName, StringComparison.Ordinal),
            ct,
            maxAttempts: runImmediatelyRequested ? 20 : 10);

        var savePreferenceRequested = args.Bool("save_github_username_preference") == true;
        var preferenceSaved = await SaveGithubUsernamePreferenceIfRequestedAsync(
            userConfigScopeId,
            githubUsernameResolution.GithubUsername ?? string.Empty,
            savePreferenceRequested,
            ct);

        return JsonSerializer.Serialize(new
        {
            status = confirmed ? "created" : "accepted",
            agent_id = agentId,
            agent_type = SkillRunnerDefaults.AgentType,
            template = templateSpec.TemplateName,
            github_username = githubUsernameResolution.GithubUsername,
            github_username_preference_saved = preferenceSaved,
            run_immediately_requested = runImmediatelyRequested,
            next_scheduled_run = nextRunAtUtc,
            conversation_id = conversationId,
            api_key_id = apiKeyId,
            note = confirmed ? "" : "Agent initialization accepted but registry projection is not yet confirmed.",
        });
    }

    private async Task<string> CreateSocialMediaAgentAsync(
        BuilderArgs args,
        IUserAgentCatalogQueryPort queryPort,
        IWorkflowAgentCommandPort workflowAgentPort,
        NyxIdApiClient nyxClient,
        string token,
        OwnerScope caller,
        CancellationToken ct)
    {
        var scopeId = AgentToolRequestContext.TryGet(ChannelMetadataKeys.RegistrationScopeId);
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

        var ownerNyxUserId = caller.NyxUserId;

        var providerSlug = (args.Str("nyx_provider_slug") ?? "api-lark-bot").Trim();
        var requiredServiceIds = await ResolveProxyServiceIdsAsync(nyxClient, token, [providerSlug], ct);
        if (requiredServiceIds.errorJson != null)
            return requiredServiceIds.errorJson;

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

        var versionBefore = await queryPort.GetStateVersionForCallerAsync(agentId, caller, ct) ?? -1;

        var deliveryTarget = ResolveDeliveryTarget(conversationId, agentId);
#pragma warning disable CS0612 // legacy fields written for rollback safety during owner_scope migration
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
            Platform = caller.Platform,
            ApiKeyId = apiKeyId!,
            Enabled = true,
            ScopeId = scopeId.Trim(),
            LarkReceiveId = deliveryTarget.Primary.ReceiveId,
            LarkReceiveIdType = deliveryTarget.Primary.ReceiveIdType,
            LarkReceiveIdFallback = deliveryTarget.Fallback?.ReceiveId ?? string.Empty,
            LarkReceiveIdTypeFallback = deliveryTarget.Fallback?.ReceiveIdType ?? string.Empty,
            OwnerScope = caller.Clone(),
        };
#pragma warning restore CS0612

        // Initialize via the workflow-agent command port; observation lives in
        // the polling loop below since it crosses actors (Workflow → catalog).
        // We split run-immediately into a follow-up TriggerAsync so the trigger
        // fires only after the catalog projection confirms creation.
        await workflowAgentPort.InitializeAsync(agentId, initialize, runImmediately: false, ct);

        var confirmed = await WaitForCreatedAgentAsync(
            queryPort,
            agentId,
            caller,
            versionBefore,
            entry => string.Equals(entry.AgentType, WorkflowAgentDefaults.AgentType, StringComparison.Ordinal) &&
                     string.Equals(entry.TemplateName, WorkflowAgentDefaults.TemplateName, StringComparison.Ordinal),
            ct,
            maxAttempts: args.Bool("run_immediately") == true ? 20 : 10);

        if (args.Bool("run_immediately") == true && confirmed)
        {
            await workflowAgentPort.TriggerAsync(agentId, "create_agent", revisionFeedback: null, ct);
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
        IWorkflowAgentCommandPort workflowAgentPort,
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

        // Disable via the typed lifecycle port (dispatch + projection priming happen there);
        // skip if the agent type isn't managed.
        var disableResult = await TryDispatchLifecycleAsync(
            entry, "delete_agent", LifecycleAction.Disable, revisionFeedback: null,
            skillRunnerPort, workflowAgentPort, ct);
        if (disableResult.error != null)
            return disableResult.error;

        if (!string.IsNullOrWhiteSpace(entry.ApiKeyId))
            await nyxClient.DeleteApiKeyAsync(token, entry.ApiKeyId, ct);

        // Tombstone via UserAgentCatalogCommandPort; port owns priming +
        // version observation and returns an honest accepted/observed status.
        var tombstoneResult = await catalogCommandPort.TombstoneAsync(entry.AgentId, ct);
        var deleted = tombstoneResult.Outcome == CatalogCommandOutcome.Observed;

        var agents = await QueryAgentsForCallerAsync(queryPort, caller, ct);

        if (deleted)
        {
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
        IWorkflowAgentCommandPort workflowAgentPort,
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

        if (string.Equals(entry.Status, SkillRunnerDefaults.StatusDisabled, StringComparison.Ordinal) ||
            string.Equals(entry.Status, WorkflowAgentDefaults.StatusDisabled, StringComparison.Ordinal))
            return JsonSerializer.Serialize(new { error = $"Agent '{entry.AgentId}' is disabled. Enable it before running." });

        var revisionFeedback = NormalizeOptional(args.Str("revision_feedback"));
        var dispatch = await TryDispatchLifecycleAsync(entry, "run_agent", LifecycleAction.Run, revisionFeedback, skillRunnerPort, workflowAgentPort, ct);
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
        IWorkflowAgentCommandPort workflowAgentPort,
        OwnerScope caller,
        CancellationToken ct)
    {
        var entry = await RequireManagedAgentAsync(args, queryPort, caller, "disable_agent", ct);
        if (entry.error != null)
            return entry.error;

        if (string.Equals(entry.value!.Status, SkillRunnerDefaults.StatusDisabled, StringComparison.Ordinal) ||
            string.Equals(entry.value.Status, WorkflowAgentDefaults.StatusDisabled, StringComparison.Ordinal))
            return SerializeAgentStatus(entry.value, "Agent is already disabled.");

        // Capture baseline version BEFORE dispatch so the wait can distinguish
        // "projection has materialized this disable" from "stale read replica
        // happens to surface a historical disabled status". Capture must
        // precede dispatch — capturing inside the wait helper would race
        // against a fast projection that already advanced the version.
        var versionBefore = await queryPort.GetStateVersionForCallerAsync(entry.value.AgentId, caller, ct) ?? -1;

        var dispatch = await TryDispatchLifecycleAsync(entry.value, "disable_agent", LifecycleAction.Disable, null, skillRunnerPort, workflowAgentPort, ct);
        if (dispatch.error != null)
            return dispatch.error;

        var observation = await WaitForAgentStatusAsync(queryPort, entry.value.AgentId, caller, versionBefore, SkillRunnerDefaults.StatusDisabled, ct);
        if (observation.Confirmed)
            return SerializeAgentStatus(observation.Entry!, "Agent disabled. Scheduling paused.");

        // Dual gate never passed — the disable was dispatched but the read
        // model has not confirmed the lifecycle change within the wait
        // budget. Surface the pre-dispatch entry with an honest propagating
        // note so the caller (LLM/user) does not assume the agent is paused.
        return SerializeAgentStatus(entry.value, "Disable submitted. Run /agent-status in a few seconds to confirm the agent is paused.");
    }

    private async Task<string> EnableAgentAsync(
        BuilderArgs args,
        IUserAgentCatalogQueryPort queryPort,
        ISkillRunnerCommandPort skillRunnerPort,
        IWorkflowAgentCommandPort workflowAgentPort,
        OwnerScope caller,
        CancellationToken ct)
    {
        var entry = await RequireManagedAgentAsync(args, queryPort, caller, "enable_agent", ct);
        if (entry.error != null)
            return entry.error;

        if (string.Equals(entry.value!.Status, SkillRunnerDefaults.StatusRunning, StringComparison.Ordinal) ||
            string.Equals(entry.value.Status, WorkflowAgentDefaults.StatusRunning, StringComparison.Ordinal))
            return SerializeAgentStatus(entry.value, "Agent is already enabled.");

        // See DisableAgentAsync for why versionBefore is captured here (before
        // any dispatch) and not inside WaitForAgentStatusAsync.
        var versionBefore = await queryPort.GetStateVersionForCallerAsync(entry.value.AgentId, caller, ct) ?? -1;

        var dispatch = await TryDispatchLifecycleAsync(entry.value, "enable_agent", LifecycleAction.Enable, null, skillRunnerPort, workflowAgentPort, ct);
        if (dispatch.error != null)
            return dispatch.error;

        var observation = await WaitForAgentStatusAsync(queryPort, entry.value.AgentId, caller, versionBefore, SkillRunnerDefaults.StatusRunning, ct);
        if (observation.Confirmed)
            return SerializeAgentStatus(observation.Entry!, "Agent enabled. Scheduling resumed.");

        // See DisableAgentAsync for the rationale on the un-confirmed branch.
        return SerializeAgentStatus(entry.value, "Enable submitted. Run /agent-status in a few seconds to confirm the agent is running.");
    }

    /// <summary>
    /// Builds the JSON body for <c>POST /api/v1/api-keys</c> when the agent-builder mints a
    /// scoped child key for a new agent. Pins <c>allow_all_services = false</c> alongside the
    /// resolved <c>allowed_service_ids</c> so the agent's proxy reach is bounded to exactly the
    /// catalog slugs the template requires.
    /// </summary>
    /// <remarks>
    /// PR #418 review (4175529548): NyxID's <c>CreateApiKeyRequest.allow_all_services</c>
    /// (<c>backend/src/handlers/api_keys.rs:105</c>) is <c>#[serde(default = "default_true")]</c>,
    /// and proxy enforcement (<c>backend/src/handlers/proxy.rs:1030</c>) only checks
    /// <c>allowed_service_ids</c> when <c>!auth_user.allow_all_services</c>. Omitting the field
    /// means NyxID stores <c>true</c>, the resolved <c>UserService.id</c> list is persisted but
    /// never consulted, and the key has broad proxy reach across every service the parent token
    /// can see. Setting <c>false</c> explicitly:
    /// <list type="bullet">
    ///   <item>activates the enforcement path #417 was written to satisfy,</item>
    ///   <item>makes the narrow-scope intent first-class instead of relying on the parent
    ///   delegation token's setting (which is what surfaced the bug in production), and</item>
    ///   <item>triggers <c>validate_service_ids</c> at create-time
    ///   (<c>backend/src/services/key_service.rs:183</c>), so a malformed
    ///   <c>UserService.id</c> fails fast at <c>POST /api-keys</c> instead of silently passing
    ///   through and 403'ing on every later proxy call.</item>
    /// </list>
    /// <c>allow_all_nodes</c> stays at the NyxID default — this flow does not restrict node
    /// routing, and pinning it would surface a separate boundary that has nothing to do with
    /// the agent's service reach.
    /// </remarks>
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
            ["allow_all_services"] = false,
        };

        return JsonSerializer.Serialize(payload);
    }

    private static string SerializeAgentStatus(UserAgentCatalogEntry entry, string? note = null)
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

    private async Task<(UserAgentCatalogEntry? value, string? error)> RequireManagedAgentAsync(
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

    private async Task<bool> WaitForCreatedAgentAsync(
        IUserAgentCatalogQueryPort queryPort,
        string agentId,
        OwnerScope caller,
        long versionBefore,
        Func<UserAgentCatalogEntry, bool> predicate,
        CancellationToken ct,
        int maxAttempts = 10,
        int delayMilliseconds = 500)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(delayMilliseconds, ct);

            var versionAfter = await queryPort.GetStateVersionForCallerAsync(agentId, caller, ct) ?? -1;
            if (versionAfter <= versionBefore)
                continue;

            var entry = await queryPort.GetForCallerAsync(agentId, caller, ct);
            if (entry != null && predicate(entry))
                return true;
        }

        return false;
    }


    private async Task<(bool Confirmed, UserAgentCatalogEntry? Entry)> WaitForAgentStatusAsync(
        IUserAgentCatalogQueryPort queryPort,
        string agentId,
        OwnerScope caller,
        long versionBefore,
        string expectedStatus,
        CancellationToken ct)
    {
        // Status + version dual-condition (mirrors WaitForCreatedAgentAsync):
        // wait until the read model both advances past the caller-captured
        // baseline AND surfaces the expected status. Status alone is not
        // enough — a stale replica can hold an expected-looking historical
        // status (e.g., a previous disable→enable→disable cycle) and pass a
        // status-only check while the actor has not yet processed *this*
        // dispatch. Conversely, version alone is not enough either — an
        // unrelated state event could advance the version without changing
        // status. Both conditions together pin "this specific lifecycle
        // event has materialized in the read model". Caller must capture
        // versionBefore *before* dispatch, otherwise a fast projection that
        // already advanced the version would make versionAfter == versionBefore
        // and burn the entire budget. Projection scope priming also happens
        // in the caller before dispatch (see DisableAgentAsync /
        // EnableAgentAsync) — a late prime here cannot recover an event the
        // projector already missed.
        for (var attempt = 0; attempt < _projectionWaitAttempts; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(_projectionWaitDelayMilliseconds, ct);

            var versionAfter = await queryPort.GetStateVersionForCallerAsync(agentId, caller, ct) ?? -1;
            if (versionAfter <= versionBefore)
                continue;

            var entry = await queryPort.GetForCallerAsync(agentId, caller, ct);
            if (entry != null && string.Equals(entry.Status, expectedStatus, StringComparison.Ordinal))
                return (Confirmed: true, Entry: entry);
        }

        // Budget exhausted: the dual gate never passed. Do NOT fall back to an
        // un-gated GetAsync read — that would surface a stale-but-expected-
        // looking entry and let callers report success despite the contract
        // not being satisfied. Callers must surface honest "submitted /
        // propagating" copy when Confirmed is false.
        return (Confirmed: false, Entry: null);
    }

    private static async Task<(bool success, string? error)> TryDispatchLifecycleAsync(
        UserAgentCatalogEntry entry,
        string reason,
        LifecycleAction action,
        string? revisionFeedback,
        ISkillRunnerCommandPort skillRunnerPort,
        IWorkflowAgentCommandPort workflowAgentPort,
        CancellationToken ct)
    {
        if (string.Equals(entry.AgentType, SkillRunnerDefaults.AgentType, StringComparison.Ordinal))
        {
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
            return (true, null);
        }

        if (string.Equals(entry.AgentType, WorkflowAgentDefaults.AgentType, StringComparison.Ordinal))
        {
            switch (action)
            {
                case LifecycleAction.Run:
                    await workflowAgentPort.TriggerAsync(entry.AgentId, reason, revisionFeedback?.Trim(), ct);
                    break;
                case LifecycleAction.Disable:
                    await workflowAgentPort.DisableAsync(entry.AgentId, reason, ct);
                    break;
                case LifecycleAction.Enable:
                    await workflowAgentPort.EnableAsync(entry.AgentId, reason, ct);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action, null);
            }
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

    /// <summary>
    /// Resolves the per-user <c>UserService.id</c> values that the new agent's API key needs in
    /// <c>allowed_service_ids</c> to reach each required catalog slug through the NyxID proxy.
    /// </summary>
    /// <remarks>
    /// <para>Issue aevatarAI/aevatar#417. The previous implementation called
    /// <c>GET /api/v1/proxy/services</c> (the <em>catalog</em> list) and pulled out each row's
    /// <c>id</c>, which is a <c>DownstreamService.id</c> — a global catalog UUID shared across
    /// all users. NyxID's proxy enforcement (<c>backend/src/handlers/proxy.rs:1030</c>) checks the
    /// API key's <c>allowed_service_ids</c> against the per-user <c>UserService.id</c>, not the
    /// catalog id. The mismatch silently passed at <c>POST /api-keys</c> creation time, then
    /// surfaced as <c>403 ApiKeyScopeForbidden</c> on every proxy call.</para>
    /// <para>Why the old code looked correct in development: <c>allow_all_services=true</c>
    /// short-circuits the enforcement check (NyxID <c>proxy.rs:1030</c>). Session-token-minted
    /// API keys default to <c>true</c>, so a developer reproducing the create-key + proxy-call
    /// dance from a CLI never tripped the bug. The agent path mints child keys via the
    /// channel-relay delegation token; NyxID forces those children to inherit
    /// <c>allow_all_services=false</c> from the parent, which is when enforcement kicks in.
    /// The <c>BuildCreateApiKeyPayload</c> change in PR #418 (review 4175529548) makes the
    /// narrow-scope intent first-class by setting <c>allow_all_services=false</c> explicitly,
    /// so this resolver's output is consulted regardless of the parent's setting.</para>
    /// <para>The fix: use <c>GET /api/v1/user-services</c>, which lists this user's
    /// <c>UserService</c> instances. For each instance the response carries the per-user
    /// <c>id</c> (what enforcement actually checks) plus <c>slug</c>, <c>is_active</c>, and a
    /// <c>credential_source</c> envelope. We filter to active rows whose slug matches a required
    /// slug, and skip org-shared rows the caller cannot use as a proxy target — those would later
    /// surface as a less-actionable <c>org_role_insufficient</c> error.</para>
    /// </remarks>
    private async Task<(IReadOnlyList<string>? value, string? errorJson)> ResolveProxyServiceIdsAsync(
        NyxIdApiClient client,
        string token,
        IReadOnlyList<string> requiredSlugs,
        CancellationToken ct)
    {
        if (requiredSlugs.Count == 0)
        {
            return (null, JsonSerializer.Serialize(new
            {
                error = "no_required_slugs",
                hint = "At least one required Nyx proxy service slug must be provided.",
            }));
        }

        var response = await client.ListUserServicesAsync(token, ct);
        if (IsErrorPayload(response))
        {
            return (null, JsonSerializer.Serialize(new
            {
                error = "user_services_unavailable",
                hint = "Could not list connected Nyx user-services. Try again or check NyxID availability.",
            }));
        }

        try
        {
            using var doc = JsonDocument.Parse(response);
            // List response shape: { "services": [ {id, slug, is_active, credential_source: {...}}, ... ] }
            // The catalog response also nests under "services" (and additionally "custom_services"),
            // so reusing EnumerateProxyServiceItems is safe — but we accept *only* rows that look
            // like UserService instances by checking presence of `slug`.
            //
            // Codex review (PR #418 r3141846173): users with mixed bindings can have multiple
            // rows for the same slug (e.g. an org-shared `allowed:false` row alongside a personal
            // active row). NyxID does not guarantee any ordering, so the resolver must keep the
            // *most eligible* row per slug rather than the first one seen. We track the first
            // ineligible row anyway so that when no eligible row exists we can still emit a
            // specific error (`service_inactive` / `service_org_viewer_only`) instead of a
            // generic miss.
            var bestBySlug = new Dictionary<string, ServiceResolution>(StringComparer.OrdinalIgnoreCase);
            foreach (var svc in EnumerateProxyServiceItems(doc.RootElement))
            {
                var slug = ReadString(svc, "slug");
                if (string.IsNullOrWhiteSpace(slug))
                    continue;

                var id = ReadString(svc, "id");
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var isActive = TryReadBool(svc, "is_active") ?? true;
                var credentialSource = svc.TryGetProperty("credential_source", out var cs) ? cs : default;
                var sourceType = credentialSource.ValueKind == JsonValueKind.Object
                    ? ReadString(credentialSource, "type")
                    : null;
                var orgAllowed = credentialSource.ValueKind == JsonValueKind.Object
                    ? TryReadBool(credentialSource, "allowed")
                    : null;

                var candidate = new ServiceResolution(
                    Id: id!,
                    IsActive: isActive,
                    CredentialSourceType: sourceType,
                    OrgAllowed: orgAllowed);

                if (bestBySlug.TryGetValue(slug, out var existing))
                {
                    // Already have an eligible row → never downgrade.
                    if (existing.IsEligible)
                        continue;
                    // Existing is ineligible; only replace with another ineligible row if we
                    // would otherwise lose information. Replace iff candidate is eligible.
                    if (!candidate.IsEligible)
                        continue;
                }

                bestBySlug[slug] = candidate;
            }

            var ids = new List<string>(requiredSlugs.Count);
            foreach (var slug in requiredSlugs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!bestBySlug.TryGetValue(slug, out var resolution))
                {
                    return (null, JsonSerializer.Serialize(new
                    {
                        error = "service_not_connected",
                        slug,
                        hint = $"NyxID has no connected user-service for slug `{slug}`. Connect the provider at NyxID before creating this agent.",
                    }));
                }

                if (resolution.IsEligible)
                {
                    ids.Add(resolution.Id);
                    continue;
                }

                if (string.Equals(resolution.CredentialSourceType, "org", StringComparison.OrdinalIgnoreCase) &&
                    resolution.OrgAllowed != true)
                {
                    return (null, JsonSerializer.Serialize(new
                    {
                        error = "service_org_viewer_only",
                        slug,
                        hint = $"NyxID user-service for slug `{slug}` is shared by your org but your role does not permit using it as a proxy target. Ask an admin to widen the org role scope, or connect a personal credential.",
                    }));
                }

                // Remaining ineligible reason: !is_active.
                return (null, JsonSerializer.Serialize(new
                {
                    error = "service_inactive",
                    slug,
                    hint = $"NyxID user-service for slug `{slug}` is inactive. Re-activate it at NyxID before creating this agent.",
                }));
            }

            return (ids.Distinct(StringComparer.Ordinal).ToArray(), null);
        }
        catch (JsonException)
        {
            return (null, JsonSerializer.Serialize(new
            {
                error = "user_services_parse_failed",
                hint = "NyxID user-services response was not valid JSON.",
            }));
        }
    }

    private readonly record struct ServiceResolution(
        string Id,
        bool IsActive,
        string? CredentialSourceType,
        bool? OrgAllowed)
    {
        public bool IsEligible =>
            IsActive &&
            !(string.Equals(CredentialSourceType, "org", StringComparison.OrdinalIgnoreCase) && OrgAllowed != true);
    }

    private async Task<string?> BuildGitHubAuthorizationResponseAsync(
        NyxIdApiClient client,
        string token,
        CancellationToken ct,
        bool preferCredentialsRequiredStatus = false)
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
            status = preferCredentialsRequiredStatus ? "credentials_required" : "oauth_required",
            template = "daily_report",
            provider = "GitHub",
            provider_id = providerId,
            authorization_url = authorizationUrl,
            documentation_url = documentationUrl,
            note = preferCredentialsRequiredStatus
                ? "Connect GitHub in NyxID, then run /daily again."
                : "Connect GitHub in NyxID, then return to Feishu and submit the daily report form again.",
        });
    }

    private async Task<(string? GithubUsername, string? ErrorResponse)> ResolveDailyReportGithubUsernameAsync(
        BuilderArgs args,
        NyxIdApiClient nyxClient,
        string token,
        string scopeId,
        CancellationToken ct)
    {
        var explicitGithubUsername = NormalizeOptional(args.Str("github_username"));
        if (explicitGithubUsername is not null)
            return (explicitGithubUsername, null);

        var preferredGithubUsername = await TryResolvePreferredGithubUsernameAsync(scopeId, ct);
        if (preferredGithubUsername is not null)
            return (preferredGithubUsername, null);

        var derivedGithubUsername = await TryResolveGitHubUsernameFromNyxAsync(nyxClient, token, ct);
        if (derivedGithubUsername is not null)
            return (derivedGithubUsername, null);

        var authorizationResponse = await BuildGitHubAuthorizationResponseAsync(
            nyxClient,
            token,
            ct,
            preferCredentialsRequiredStatus: true);
        if (authorizationResponse is not null)
            return (null, authorizationResponse);

        return (null, JsonSerializer.Serialize(new
        {
            status = "credentials_required",
            template = "daily_report",
            provider = "GitHub",
            note = "Could not resolve github_username. Provide github_username explicitly, save a default preference, or reconnect GitHub in NyxID.",
        }));
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

    private async Task<string?> TryResolvePreferredGithubUsernameAsync(string scopeId, CancellationToken ct)
    {
        var queryPort = _serviceProvider.GetService<IUserConfigQueryPort>();
        if (queryPort is null)
            return null;

        try
        {
            var config = await queryPort.GetAsync(scopeId, ct);
            return NormalizeOptional(config.GithubUsername);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> TryResolveGitHubUsernameFromNyxAsync(
        NyxIdApiClient client,
        string token,
        CancellationToken ct)
    {
        try
        {
            var response = await client.ProxyRequestAsync(
                token,
                "api-github",
                "user",
                "GET",
                null,
                null,
                ct);
            if (IsErrorPayload(response))
                return null;

            return TryParseGitHubUserLogin(response, out var login)
                ? login
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> SaveGithubUsernamePreferenceIfRequestedAsync(
        string scopeId,
        string githubUsername,
        bool shouldSave,
        CancellationToken ct)
    {
        if (!shouldSave || string.IsNullOrWhiteSpace(githubUsername))
            return false;

        var commandService = _serviceProvider.GetService<IUserConfigCommandService>();
        if (commandService is null)
            return false;

        try
        {
            await commandService.SaveGithubUsernameAsync(scopeId, githubUsername, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseGitHubUserLogin(
        string response,
        out string? login)
    {
        login = null;

        try
        {
            using var doc = JsonDocument.Parse(response);
            login = NormalizeOptional(ReadStringDeep(doc.RootElement, 2, "login", "username"));
            return login is not null;
        }
        catch (JsonException)
        {
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

    private static IEnumerable<JsonElement> EnumerateProxyServiceItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                yield return item;
            yield break;
        }

        if (root.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var propertyName in new[] { "services", "custom_services", "data" })
        {
            if (!root.TryGetProperty(propertyName, out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in items.EnumerateArray())
                yield return item;
        }
    }

    private static string NormalizeScopeId(string? value) =>
        NormalizeOptional(value) ?? "default";

    private static string? NormalizeOptional(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    /// <summary>
    /// Builds the typed Lark delivery target (primary + optional fallback) from the current
    /// AgentToolRequestContext, and emits a LogDebug breadcrumb when the primary fell back from
    /// the cross-app safe pair (chat_id / union_id) to the legacy open_id / conversation_id
    /// path. The primary is what <see cref="LarkConversationTargets.BuildFromInbound"/>
    /// returns; the fallback (when the primary is a DM chat_id and we also have a union_id at
    /// ingress) is captured so the runtime can retry once on a Lark
    /// <c>230002 bot not in chat</c> rejection — the failure mode for cross-app same-tenant
    /// deployments where the outbound app is not in the inbound DM. Operators correlating Lark
    /// <c>99992361 open_id cross app</c> rejections need the log line to confirm whether the
    /// relay surfaced <c>union_id</c> at agent-create time.
    /// </summary>
    /// <summary>
    /// Preflights GitHub proxy access using the newly created agent API key against GitHub's
    /// <c>/rate_limit</c> — a cheap read-only endpoint that returns 401/403 when NyxID's stored
    /// OAuth token was revoked or had its scopes downgraded at GitHub between connect-time and
    /// agent-create-time. Returns a structured error JSON suitable for returning verbatim from
    /// the tool when access is denied; returns <c>null</c> on success or on probe shapes we
    /// don't classify as "fundamentally broken" (rate limits, 5xx).
    /// </summary>
    /// <remarks>
    /// Issue aevatarAI/aevatar#411 added this preflight to fail fast on a misdiagnosed root
    /// cause (we thought the api-key was missing a GitHub binding). Issue #417 fixed that real
    /// cause — the api-key now carries the right per-user <c>UserService.id</c>s. The probe is
    /// retained because the OAuth grant can still be revoked outside our control (user clicks
    /// "Revoke access" in GitHub Settings → Applications, GitHub temp-bans the account, etc.),
    /// and surfacing that at create-time avoids persisting a daily-report agent that would
    /// produce empty output on every run. The freshly minted api-key is best-effort revoked at
    /// the call site so retries don't accumulate orphan proxy-scoped keys.
    /// </remarks>
    private async Task<string?> PreflightGitHubProxyAsync(
        NyxIdApiClient nyxClient,
        string apiKey,
        string nyxProviderSlug,
        CancellationToken ct)
    {
        // Cheap read-only endpoint; succeeds even with a rate-limited token, fails with 401/403
        // when the proxy can't resolve a bound GitHub credential.
        var probe = await nyxClient.ProxyRequestAsync(
            apiKey,
            "api-github",
            "/rate_limit",
            "GET",
            body: null,
            extraHeaders: null,
            ct);

        if (string.IsNullOrWhiteSpace(probe))
            return null;

        // `NyxIdApiClient.SendAsync` (NyxIdApiClient.cs:680) wraps HTTP non-2xx as
        // `{"error": true, "status": <http>, "body": "<raw downstream body>"}` — `status`,
        // not `code`. Reviewer (PR #412 r3141699476): the previous parser only read `code`,
        // so for the actual #411 production failures (HTTP 403 from /api/v1/proxy/s/api-github
        // /rate_limit) it set status=0, returned null, and persisted a daily_report agent
        // that would fail at runtime. Read both `status` (the SendAsync envelope) AND `code`
        // (any future inverted-naming envelope or top-level Lark code). Treat 401/403 as the
        // signal to fail-fast; let other shapes flow through (rate limits, 5xx etc are
        // operational and not "agent fundamentally broken").
        try
        {
            using var doc = JsonDocument.Parse(probe);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (!root.TryGetProperty("error", out var errorProp))
                return null;
            if (errorProp.ValueKind != JsonValueKind.True && errorProp.ValueKind != JsonValueKind.String)
                return null;

            var status = TryReadInt32Property(root, "status")
                         ?? TryReadInt32Property(root, "code")
                         ?? 0;
            if (status != (int)HttpStatusCode.Unauthorized && status != (int)HttpStatusCode.Forbidden)
                return null;

            var detail = root.TryGetProperty("message", out var msgProp) && msgProp.ValueKind == JsonValueKind.String
                ? msgProp.GetString()
                : null;
            var body = root.TryGetProperty("body", out var bodyProp) && bodyProp.ValueKind == JsonValueKind.String
                ? bodyProp.GetString()
                : null;

            return JsonSerializer.Serialize(new
            {
                error = "github_proxy_access_denied",
                detail = string.IsNullOrWhiteSpace(detail) ? "GitHub proxy returned 401/403 for the new agent API key." : detail,
                http_status = status,
                proxy_body = string.IsNullOrWhiteSpace(body) ? null : body,
                hint = "GitHub returned 401/403 through the NyxID proxy. Common causes: (a) the OAuth grant for GitHub was revoked at github.com/settings/applications or its scopes were downgraded — re-authorize the GitHub provider at NyxID; (b) the request reached GitHub without a User-Agent header (NyxIdApiClient now sends a default; if you see this, check that the deployed binary includes that fix). The agent will not produce a useful daily report until proxy access succeeds.",
                nyx_provider_slug = nyxProviderSlug,
            });
        }
        catch (JsonException)
        {
            // Non-JSON probe response: don't pretend we know what's going on; let creation
            // proceed so the agent can at least be created (operator can debug from logs).
            return null;
        }
    }

    private static int? TryReadInt32Property(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out var value))
        {
            return null;
        }
        return value;
    }

    private static bool? TryReadBool(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    /// <summary>
    /// Best-effort revoke of an API key minted earlier in the create flow. Used when GitHub
    /// preflight fails so retries of <c>/daily</c> don't accumulate orphan proxy-scoped keys
    /// in the user's NyxID account (codex review #418 r3141846175). Failures here are logged
    /// at Warning but do NOT propagate — the structured create-time error is the user-facing
    /// signal; an orphan key is an ops cleanup concern, not a hard failure.
    /// </summary>
    private async Task BestEffortRevokeApiKeyAsync(
        NyxIdApiClient nyxClient,
        string sessionToken,
        string apiKeyId,
        string reason,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKeyId))
            return;

        try
        {
            var response = await nyxClient.DeleteApiKeyAsync(sessionToken, apiKeyId, ct);
            if (LarkProxyResponse.TryGetError(response, out _, out var detail))
            {
                _logger?.LogWarning(
                    "Failed to revoke orphan agent API key {ApiKeyId} after {Reason}: {Detail}",
                    apiKeyId,
                    reason,
                    detail);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "Exception revoking orphan agent API key {ApiKeyId} after {Reason}",
                apiKeyId,
                reason);
        }
    }

    private LarkReceiveTargetWithFallback ResolveDeliveryTarget(string conversationId, string agentId)
    {
        var chatType = AgentToolRequestContext.TryGet(ChannelMetadataKeys.ChatType);
        var senderId = AgentToolRequestContext.TryGet(ChannelMetadataKeys.SenderId);
        var unionId = AgentToolRequestContext.TryGet(ChannelMetadataKeys.LarkUnionId);
        var chatId = AgentToolRequestContext.TryGet(ChannelMetadataKeys.LarkChatId);

        var target = LarkConversationTargets.BuildFromInboundWithFallback(
            chatType,
            conversationId,
            senderId,
            unionId,
            chatId);

        if (target.Primary.FellBackToPrefixInference)
        {
            _logger?.LogDebug(
                "Agent builder fell back to legacy delivery target inference for {AgentId}: chatType={ChatType}, hasUnionId={HasUnionId}, hasLarkChatId={HasLarkChatId}, hasSenderId={HasSenderId}, resolvedReceiveIdType={ReceiveIdType}. Cross-app outbound (e.g. customer api-lark-bot) may surface Lark `99992361 open_id cross app` until the relay propagates union_id.",
                agentId,
                chatType ?? string.Empty,
                !string.IsNullOrWhiteSpace(unionId),
                !string.IsNullOrWhiteSpace(chatId),
                !string.IsNullOrWhiteSpace(senderId),
                target.Primary.ReceiveIdType);
        }

        return target;
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
