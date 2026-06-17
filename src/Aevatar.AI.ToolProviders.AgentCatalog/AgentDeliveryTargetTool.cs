using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.AI.ToolProviders.AgentCatalog;

/// <summary>
/// Tool for managing agent outbound delivery targets used by workflow human interaction
/// cards. Caller-scoped per issue #466 — every operation operates on the calling
/// NyxID/channel-sender's own delivery targets only. The LLM-overridable
/// <c>owner_nyx_user_id</c> argument is removed (impersonation surface).
///
/// The LLM-facing return shape no longer carries <c>NyxApiKey</c> at all (not even
/// masked) — credentials live behind the internal <see cref="IUserAgentDeliveryTargetReader"/>
/// and are not surfaced through any LLM tool. Issue #466 §D.
///
/// Upsert is rebind-only: rejects when no existing entry exists for the caller. Real
/// agent creation (which mints credentials) lives in <c>scheduled_agent_creator</c>.
/// </summary>
public sealed class AgentDeliveryTargetTool : IAgentTool
{
    // Refactor (iter83/cluster-083-agent-tool-source-root-provider-locator):
    //   Old pattern: tool source captures root IServiceProvider; tools resolve business ports via service locator in ExecuteAsync
    //   New principle: tool source + tools constructor-inject typed contracts; no root provider lookup
    private readonly IUserAgentCatalogQueryPort _queryPort;
    private readonly IUserAgentCatalogCommandPort _commandPort;
    private readonly ICallerScopeResolver _callerScopeResolver;
    private readonly IScheduledAgentApiKeyIssuer? _apiKeyIssuer;

    public AgentDeliveryTargetTool(
        IUserAgentCatalogQueryPort queryPort,
        IUserAgentCatalogCommandPort commandPort,
        ICallerScopeResolver callerScopeResolver,
        IScheduledAgentApiKeyIssuer? apiKeyIssuer = null)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
        _callerScopeResolver = callerScopeResolver ?? throw new ArgumentNullException(nameof(callerScopeResolver));
        _apiKeyIssuer = apiKeyIssuer;
    }

    public string Name => "agent_delivery_targets";

    public string Description =>
        "Manage agent delivery targets for workflow human interaction cards and outbound channel delivery. " +
        "Actions: list, create, upsert (rebind existing only), delete. " +
        "Use create to mint server-side credentials for a stable delivery_target_id without creating a scheduled runner. " +
        "Use upsert to rebind an existing agent_id/delivery_target_id to a different platform conversation or outbound provider slug. " +
        "Operations are scoped to the caller's own delivery targets.";

    // Note (issue #466): no `owner_nyx_user_id` and no `nyx_api_key` parameters. Owner
    // is derived from the caller scope; credentials are minted+stored by the create
    // flow, never accepted as a tool argument here.
    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "create", "upsert", "delete"],
              "description": "Action to perform (default: list)"
            },
            "agent_id": {
              "type": "string",
              "description": "Agent ID / delivery target ID to bind"
            },
            "delivery_target_id": {
              "type": "string",
              "description": "Stable delivery target ID. Required for create; accepted as an alias for agent_id in upsert/delete."
            },
            "platform": {
              "type": "string",
              "description": "Delivery platform for the target"
            },
            "conversation_id": {
              "type": "string",
              "description": "Platform conversation or recipient ID"
            },
            "nyx_provider_slug": {
              "type": "string",
              "description": "Outbound provider service slug for this target"
            },
            "confirm": {
              "type": "boolean",
              "description": "Must be true to execute delete"
            }
          }
        }
        """;

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

        using var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;
        var action = GetStr(root, "action") ?? "list";

        if (action is "create" or "upsert" or "delete")
        {
            return action switch
            {
                "create" => await CreateAsync(_queryPort, _commandPort, _callerScopeResolver, _apiKeyIssuer, token, caller, root, ct),
                "upsert" => await UpsertAsync(_queryPort, _commandPort, caller, root, ct),
                "delete" => await DeleteAsync(_queryPort, _commandPort, caller, root, ct),
                _ => await ListAsync(_queryPort, caller, ct),
            };
        }

        return await ListAsync(_queryPort, caller, ct);
    }

    private static async Task<string> CreateAsync(
        IUserAgentCatalogQueryPort queryPort,
        IUserAgentCatalogCommandPort commandPort,
        ICallerScopeResolver callerScopeResolver,
        IScheduledAgentApiKeyIssuer? apiKeyIssuer,
        string token,
        OwnerScope caller,
        JsonElement args,
        CancellationToken ct)
    {
        if (apiKeyIssuer is null)
        {
            return JsonSerializer.Serialize(new
            {
                error = "delivery_target_creation_unavailable",
                hint = "The host has not registered the delivery-target credential issuer.",
            });
        }

        var deliveryTargetId = GetRequired(args, "'delivery_target_id' is required for create", "delivery_target_id", "agent_id", "deliveryTargetId", "agentId");
        if (deliveryTargetId.error != null)
            return deliveryTargetId.error;

        var conversationId = GetRequired(args, "'conversation_id' is required for create", "conversation_id", "conversationId");
        if (conversationId.error != null)
            return conversationId.error;

        var nyxProviderSlug = GetRequired(args, "'nyx_provider_slug' is required for create", "nyx_provider_slug", "nyxProviderSlug");
        if (nyxProviderSlug.error != null)
            return nyxProviderSlug.error;

        var platform = GetRequired(args, "'platform' is required for create", "platform");
        if (platform.error != null)
            return platform.error;

        var targetPlatform = platform.value!;

        if (await queryPort.ExistsActiveAsync(deliveryTargetId.value!, ct))
        {
            return JsonSerializer.Serialize(new
            {
                error = "delivery_target_already_exists",
                delivery_target_id = deliveryTargetId.value,
                hint = "Use a different delivery_target_id. Existing delivery target ids are globally reserved.",
            });
        }

        OwnerScope keyScope;
        try
        {
            keyScope = await callerScopeResolver.RequireAsync(ct);
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

        var keyScopeId = keyScope.RegistrationScopeId;
        var key = await apiKeyIssuer.IssueAsync(
            token,
            new ScheduledAgentServiceSlugs(
                nyxProviderSlug.value!,
                FailureNotificationSlug: null,
                RequiredServiceSlugs: [],
                RequiresOrnnService: false),
            deliveryTargetId.value!,
            skillName: string.Empty,
            scopeId: keyScopeId,
            ct);
        if (!key.Success)
            return key.ToErrorJson();

        try
        {
            await commandPort.UpsertAsync(
                new UserAgentCatalogUpsertCommand
                {
                    AgentId = deliveryTargetId.value!,
                    ConversationId = conversationId.value!,
                    NyxProviderSlug = nyxProviderSlug.value!,
                    NyxApiKey = key.FullKey ?? string.Empty,
                    ApiKeyId = key.ApiKeyId ?? string.Empty,
                    AgentType = "delivery_target",
                    TemplateName = "explicit_delivery_target",
                    ScopeId = keyScopeId,
                    TargetPlatform = targetPlatform,
                    OwnerScope = caller.Clone(),
                },
                ct);
        }
        catch
        {
            await apiKeyIssuer.TryRevokeAsync(token, key.ApiKeyId ?? string.Empty, CancellationToken.None);
            throw;
        }

        return JsonSerializer.Serialize(new
        {
            status = "accepted",
            agent_id = deliveryTargetId.value,
            delivery_target_id = deliveryTargetId.value,
            platform = targetPlatform,
            conversation_id = conversationId.value,
            nyx_provider_slug = nyxProviderSlug.value,
            api_key_id = key.ApiKeyId,
            note = "Delivery target create accepted. Projection is propagating; try 'list' after a few seconds.",
        });
    }

    private static string? GetStr(JsonElement el, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (el.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }

    private static bool GetBool(JsonElement el, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (!el.TryGetProperty(property, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.True)
                return true;

            if (value.ValueKind == JsonValueKind.String &&
                bool.TryParse(value.GetString(), out var parsed) &&
                parsed)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<string> ListAsync(
        IUserAgentCatalogQueryPort queryPort,
        OwnerScope caller,
        CancellationToken ct)
    {
        var entries = await queryPort.QueryByCallerAsync(caller, ct);
        var result = entries
            .Select(static entry => new
            {
                agent_id = entry.AgentId,
                delivery_target_id = entry.AgentId,
                platform = ResolveDeliveryPlatform(entry),
                conversation_id = entry.ConversationId,
                nyx_provider_slug = entry.NyxProviderSlug,
                created_at = entry.CreatedAt,
                updated_at = entry.UpdatedAt,
            })
            .ToArray();

        return JsonSerializer.Serialize(new { delivery_targets = result, total = result.Length });
    }

    private static async Task<string> UpsertAsync(
        IUserAgentCatalogQueryPort queryPort,
        IUserAgentCatalogCommandPort commandPort,
        OwnerScope caller,
        JsonElement args,
        CancellationToken ct)
    {
        var agentId = GetRequired(args, "'agent_id' is required for upsert", "agent_id", "delivery_target_id", "agentId", "deliveryTargetId");
        if (agentId.error != null)
            return agentId.error;

        var conversationId = GetRequired(args, "'conversation_id' is required for upsert", "conversation_id", "conversationId");
        if (conversationId.error != null)
            return conversationId.error;

        var nyxProviderSlug = GetRequired(args, "'nyx_provider_slug' is required for upsert", "nyx_provider_slug", "nyxProviderSlug");
        if (nyxProviderSlug.error != null)
            return nyxProviderSlug.error;

        // Platform argument is informational only — the canonical platform on the
        // upsert is the caller's platform from OwnerScope. Disregard it to avoid the
        // LLM steering an upsert into a different platform bucket than the surface
        // the request actually came from.
        var platform = caller.Platform;

        // Issue #466 review: this tool no longer accepts NyxApiKey as an argument
        // (avoiding LLM credential exposure). Existing credentials are preserved
        // through the actor's MergeNonEmpty policy — but a *create* with no existing
        // entry would land a credential-less delivery target that can't dispatch
        // outbound. Fail closed instead of silently producing a broken entry. Real
        // creation flows go through AgentBuilderTool which mints the key inline.
        var existingForCaller = await queryPort.GetForCallerAsync(agentId.value!, caller, ct);
        if (existingForCaller is null)
        {
            return JsonSerializer.Serialize(new
            {
                error = "delivery_target_not_found_for_caller",
                hint = "agent_delivery_targets.upsert is a rebind operation only — it preserves the existing API key. Use action=create to create a new delivery target with server-side credentials.",
            });
        }

        // Refactor (iter4/cluster-009):
        //   Old pattern: Upsert mapped command-port Observed to a synchronous upserted status.
        //   New principle: Upsert ACK is accepted-only; projection freshness is observed by explicit list/get queries.
        // Refactor (iter5/cluster-012):
        //   Old pattern: Upsert awaited a result object that only repeated accepted.
        //   New principle: Upsert awaits command completion; accepted status is emitted by this tool boundary.
        // Refactor (iter92/cluster-092):
        //   Old: write path simultaneously emitted deprecated `Platform`/`OwnerNyxUserId`.
        //   New: write path emits only `OwnerScope`; legacy fields are retained only in
        //   the no-`OwnerScope` fallback branch for backwards compatibility.
        await commandPort.UpsertAsync(
            new UserAgentCatalogUpsertCommand
            {
                AgentId = agentId.value!,
                ConversationId = conversationId.value!,
                NyxProviderSlug = nyxProviderSlug.value!,
                // NyxApiKey intentionally not accepted as a tool argument; the LLM
                // should never see / pass plaintext credentials. Existing credentials
                // are preserved through the actor's MergeNonEmpty upsert policy.
                NyxApiKey = string.Empty,
                OwnerScope = caller.Clone(),
            },
            ct);

        return JsonSerializer.Serialize(new
        {
            status = "accepted",
            agent_id = agentId.value,
            delivery_target_id = agentId.value,
            platform,
            conversation_id = conversationId.value,
            nyx_provider_slug = nyxProviderSlug.value,
            note = "Delivery target accepted. Projection is propagating; try 'list' after a few seconds.",
        });
    }

    private static async Task<string> DeleteAsync(
        IUserAgentCatalogQueryPort queryPort,
        IUserAgentCatalogCommandPort commandPort,
        OwnerScope caller,
        JsonElement args,
        CancellationToken ct)
    {
        var agentId = GetStr(args, "agent_id", "delivery_target_id", "id", "agentId", "deliveryTargetId");
        if (string.IsNullOrWhiteSpace(agentId))
            return """{"error":"'agent_id' is required for delete"}""";

        // Caller-scoped existence check: non-owned ids collapse to "not found"
        // (no existence disclosure). Issue #466 acceptance.
        var exists = await queryPort.GetForCallerAsync(agentId, caller, ct);
        if (exists is null)
            return JsonSerializer.Serialize(new { error = $"Delivery target '{agentId}' not found" });

        if (!GetBool(args, "confirm"))
        {
            return JsonSerializer.Serialize(new
            {
                status = "confirm_required",
                agent_id = exists.AgentId,
                delivery_target_id = exists.AgentId,
                platform = exists.OwnerScope?.Platform ?? string.Empty,
                conversation_id = exists.ConversationId,
                nyx_provider_slug = exists.NyxProviderSlug,
                note = "Call again with confirm=true to delete this delivery target mapping.",
            });
        }

        // Refactor (iter4/cluster-009):
        //   Old pattern: Tombstone mapped command-port Observed/NotFound to synchronous delete outcomes.
        //   New principle: Caller-scoped pre-check owns existence; tombstone ACK is accepted-only.
        // Refactor (iter5/cluster-012):
        //   Old pattern: Delete awaited a tombstone result object that only repeated accepted.
        //   New principle: Delete awaits command completion; accepted status is emitted by this tool boundary.
        await commandPort.TombstoneAsync(agentId, ct);
        return JsonSerializer.Serialize(new
        {
            status = "accepted",
            agent_id = agentId,
            delivery_target_id = agentId,
            note = "Tombstone accepted. Projection is propagating; try 'list' in a few seconds to confirm the delivery target is gone.",
        });
    }

    private static (string? value, string? error) GetRequired(JsonElement args, string errorMessage, params string[] keys)
    {
        var value = GetStr(args, keys)?.Trim();
        if (!string.IsNullOrWhiteSpace(value))
            return (value, null);

        return (null, JsonSerializer.Serialize(new { error = errorMessage }));
    }

    private static string ResolveDeliveryPlatform(UserAgentCatalogReadModelEntry entry) =>
        entry.TargetPlatform ?? string.Empty;
}
