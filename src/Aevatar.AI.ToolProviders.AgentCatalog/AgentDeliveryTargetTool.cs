using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Scheduled;
using Aevatar.Studio.Application.Authorization;

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
    private readonly ISecretVault _secretVault;
    private readonly IScheduledAgentApiKeyIssuer? _apiKeyIssuer;
    private readonly IScheduledInvocationAuthorizationPlanner? _authorizationPlanner;

    public AgentDeliveryTargetTool(
        IUserAgentCatalogQueryPort queryPort,
        IUserAgentCatalogCommandPort commandPort,
        ICallerScopeResolver callerScopeResolver,
        ISecretVault secretVault,
        IScheduledAgentApiKeyIssuer? apiKeyIssuer = null,
        IScheduledInvocationAuthorizationPlanner? authorizationPlanner = null)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
        _callerScopeResolver = callerScopeResolver ?? throw new ArgumentNullException(nameof(callerScopeResolver));
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _apiKeyIssuer = apiKeyIssuer;
        _authorizationPlanner = authorizationPlanner;
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
                "create" => await CreateAsync(_queryPort, _commandPort, _callerScopeResolver, _secretVault, _apiKeyIssuer, _authorizationPlanner, token, caller, root, ct),
                "upsert" => await UpsertAsync(_queryPort, _commandPort, caller, root, ct),
                "delete" => await DeleteAsync(_queryPort, _commandPort, _apiKeyIssuer, token, caller, root, ct),
                _ => await ListAsync(_queryPort, caller, ct),
            };
        }

        return await ListAsync(_queryPort, caller, ct);
    }

    private static async Task<string> CreateAsync(
        IUserAgentCatalogQueryPort queryPort,
        IUserAgentCatalogCommandPort commandPort,
        ICallerScopeResolver callerScopeResolver,
        ISecretVault secretVault,
        IScheduledAgentApiKeyIssuer? apiKeyIssuer,
        IScheduledInvocationAuthorizationPlanner? authorizationPlanner,
        string token,
        OwnerScope caller,
        JsonElement args,
        CancellationToken ct)
    {
        if (apiKeyIssuer is null || authorizationPlanner is null)
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
        var bindingId = Normalize(AgentToolRequestContext.SenderBindingId);
        var ownerSubject = Normalize(AgentToolRequestContext.SenderNyxUserId) ?? Normalize(keyScope.NyxUserId);
        var subjectPlatform = Normalize(AgentToolRequestContext.Current?.Channel.Platform) ?? Normalize(keyScope.Platform);
        var subjectExternalUserId = Normalize(AgentToolRequestContext.Current?.Channel.SenderId) ??
                                    Normalize(keyScope.SenderId) ?? ownerSubject;
        if (bindingId is null || ownerSubject is null || subjectPlatform is null || subjectExternalUserId is null)
        {
            return JsonSerializer.Serialize(new { error = "authenticated_owner_context_unavailable" });
        }

        var now = DateTimeOffset.UtcNow;
        var authorization = await authorizationPlanner.PlanAsync(
            new ScheduledInvocationAuthorizationRequest(
                new ScheduledInvocationTarget
                {
                    Lark = new LarkScheduledInvocationTarget
                    {
                        ScopeId = keyScopeId,
                        ScheduledAgentId = deliveryTargetId.value!,
                        ConversationId = conversationId.value!,
                    },
                },
                new AuthenticatedNyxIdOwnerContext
                {
                    Owner = new NyxIdCatalogOwnerIdentity
                    {
                        Authority = "nyxid",
                        OwnerKind = NyxIdCatalogOwnerKind.Personal,
                        OwnerSubject = ownerSubject,
                    },
                    SubjectPlatform = subjectPlatform,
                    SubjectTenant = keyScopeId,
                    SubjectExternalUserId = subjectExternalUserId,
                    VerifiedBindingId = bindingId,
                },
                [],
                new ScheduledInvocationAuthorizationAuthority(),
                now.AddDays(90),
                now)
            {
                RequiredNyxIdServiceSlugs = [nyxProviderSlug.value!],
            },
            ct);
        if (!authorization.Success)
            return JsonSerializer.Serialize(new { error = authorization.FailureCode.ToString(), detail = authorization.Detail });

        var key = await apiKeyIssuer.IssueAsync(
            token,
            authorization.Plan!,
            $"aevatar-delivery-target-{deliveryTargetId.value}",
            ct);
        if (!key.Success)
            return key.ToErrorJson();

        var storedKey = await secretVault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ScheduledNyxApiKey,
            BuildScheduledNyxApiKeyOwnerScopeKey(caller, keyScopeId, conversationId.value!, deliveryTargetId.value!),
            key.ApiKeyId ?? string.Empty,
            key.FullKey ?? string.Empty,
            "delivery-target-create"),
            ct);

        try
        {
            await commandPort.UpsertAsync(
                new UserAgentCatalogUpsertCommand
                {
                    AgentId = deliveryTargetId.value!,
                    ConversationId = conversationId.value!,
                    NyxProviderSlug = nyxProviderSlug.value!,
                    NyxApiKey = string.Empty,
                    NyxApiKeyReference = storedKey.Reference,
                    ApiKeyId = key.ApiKeyId ?? string.Empty,
                    AgentType = "delivery_target",
                    TemplateName = "explicit_delivery_target",
                    ScopeId = keyScopeId,
                    TargetPlatform = targetPlatform,
                    ChannelAddress = UserAgentCatalogChannelAddress.FromParts(
                        targetPlatform,
                        nyxProviderSlug.value!,
                        conversationId.value!,
                        conversationId.value!,
                        string.Empty,
                        null,
                        null),
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

    private static string BuildScheduledNyxApiKeyOwnerScopeKey(
        OwnerScope caller,
        string scopeId,
        string conversationId,
        string deliveryTargetId)
    {
        var platform = Normalize(caller.Platform) ?? OwnerScope.NyxIdPlatform;
        var nyxUserId = Normalize(caller.NyxUserId) ?? string.Empty;
        var registrationScopeId = Normalize(caller.RegistrationScopeId) ?? string.Empty;
        var senderId = Normalize(caller.SenderId) ?? string.Empty;
        return string.Join(
            ":",
            "scheduled",
            Escape(platform),
            Escape(nyxUserId),
            Escape(registrationScopeId),
            Escape(senderId),
            Escape(Normalize(scopeId) ?? string.Empty),
            Escape(Normalize(conversationId) ?? string.Empty),
            Escape(Normalize(deliveryTargetId) ?? string.Empty));
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal);

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
                conversation_id = ResolveConversationId(entry),
                nyx_provider_slug = ResolveProviderSlug(entry),
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

        var requestedPlatform = Normalize(GetStr(args, "platform"));
        var platform = requestedPlatform ?? Normalize(ResolveDeliveryPlatform(existingForCaller)) ?? caller.Platform;

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
                TargetPlatform = platform,
                ChannelAddress = UserAgentCatalogChannelAddress.FromParts(
                    platform,
                    nyxProviderSlug.value!,
                    conversationId.value!,
                    conversationId.value!,
                    string.Empty,
                    null,
                    null),
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
        IScheduledAgentApiKeyIssuer? apiKeyIssuer,
        string token,
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
                platform = ResolveDeliveryPlatform(exists),
                conversation_id = ResolveConversationId(exists),
                nyx_provider_slug = ResolveProviderSlug(exists),
                note = "Call again with confirm=true to delete this delivery target mapping.",
            });
        }

        await commandPort.TombstoneAsync(agentId, ct);

        var revocationResult = apiKeyIssuer is null || string.IsNullOrWhiteSpace(exists.ApiKeyId)
            ? null
            : await apiKeyIssuer.RevokeAsync(token, exists.ApiKeyId, ct);
        if (revocationResult is not null)
        {
            await commandPort.RecordApiKeyRevocationAttemptAsync(
                new UserAgentCatalogRecordApiKeyRevocationAttemptCommand
                {
                    AgentId = exists.AgentId,
                    ApiKeyId = exists.ApiKeyId,
                    Completed = revocationResult.Completed,
                    HttpStatus = revocationResult.HttpStatus,
                    Error = revocationResult.Error,
                    FailureKind = revocationResult.FailureKind,
                },
                ct);
        }

        return JsonSerializer.Serialize(new
        {
            status = "accepted",
            agent_id = agentId,
            delivery_target_id = agentId,
            api_key_revocation_status = ResolveRevocationStatus(revocationResult),
            note = "Tombstone accepted. Projection is propagating; try 'list' in a few seconds to confirm the delivery target is gone.",
        });
    }

    private static string ResolveRevocationStatus(ScheduledAgentApiKeyRevokeResult? result) =>
        result is null ? "not_applicable" : result.Completed ? "completed" : "pending";

    private static (string? value, string? error) GetRequired(JsonElement args, string errorMessage, params string[] keys)
    {
        var value = Normalize(GetStr(args, keys));
        if (!string.IsNullOrWhiteSpace(value))
            return (value, null);

        return (null, JsonSerializer.Serialize(new { error = errorMessage }));
    }

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string ResolveDeliveryPlatform(UserAgentCatalogReadModelEntry entry) =>
        Normalize(entry.ChannelAddress.Platform) ?? entry.TargetPlatform ?? string.Empty;

    private static string ResolveConversationId(UserAgentCatalogReadModelEntry entry) =>
        Normalize(entry.ChannelAddress.ConversationId) ?? entry.ConversationId;

    private static string ResolveProviderSlug(UserAgentCatalogReadModelEntry entry) =>
        Normalize(entry.ChannelAddress.ProviderSlug) ?? entry.NyxProviderSlug;
}
