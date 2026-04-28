using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Tool for managing agent outbound delivery targets used by workflow human interaction
/// cards. Caller-scoped per issue #466 — every operation operates on the calling
/// NyxID/channel-sender's own delivery targets only. The LLM-overridable
/// <c>owner_nyx_user_id</c> argument is removed (impersonation surface).
///
/// The LLM-facing return shape no longer carries <c>NyxApiKey</c> at all (not even
/// masked) — credentials live behind the internal <see cref="IUserAgentDeliveryTargetReader"/>
/// and are not surfaced through any LLM tool. Issue #466 §D.
/// </summary>
public sealed class AgentDeliveryTargetTool : IAgentTool
{
    private readonly IServiceProvider _serviceProvider;
    // Per-instance polling budget (see ProjectionWaitDefaults). Tests inject
    // shrunk values via the constructor to exercise the budget-exhausted
    // branch without burning the production 15 s.
    private readonly int _projectionWaitAttempts;
    private readonly int _projectionWaitDelayMilliseconds;

    public AgentDeliveryTargetTool(
        IServiceProvider serviceProvider,
        int projectionWaitAttempts = ProjectionWaitDefaults.Attempts,
        int projectionWaitDelayMilliseconds = ProjectionWaitDefaults.DelayMilliseconds)
    {
        _serviceProvider = serviceProvider;
        _projectionWaitAttempts = projectionWaitAttempts;
        _projectionWaitDelayMilliseconds = projectionWaitDelayMilliseconds;
    }

    public string Name => "agent_delivery_targets";

    public string Description =>
        "Manage agent delivery targets for workflow human interaction cards and outbound channel delivery. " +
        "Actions: list, upsert (rebind existing only), delete. " +
        "Use this to rebind an agent_id/delivery_target_id to a different Lark conversation or Nyx provider slug; " +
        "creating new delivery targets (which mints credentials) is the agent_builder tool's job. " +
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
              "enum": ["list", "upsert", "delete"],
              "description": "Action to perform (default: list)"
            },
            "agent_id": {
              "type": "string",
              "description": "Agent ID / delivery target ID to bind"
            },
            "platform": {
              "type": "string",
              "description": "Target platform (default: lark)"
            },
            "conversation_id": {
              "type": "string",
              "description": "Conversation/chat ID on the target platform"
            },
            "nyx_provider_slug": {
              "type": "string",
              "description": "Nyx proxy service slug, e.g. api-lark-bot"
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
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        var queryPort = _serviceProvider.GetService<IUserAgentCatalogQueryPort>();
        var actorRuntime = _serviceProvider.GetService<IActorRuntime>();
        var callerScopeResolver = _serviceProvider.GetService<ICallerScopeResolver>();
        if (queryPort is null || actorRuntime is null || callerScopeResolver is null)
            return """{"error":"Agent delivery target runtime not available. IUserAgentCatalogQueryPort, IActorRuntime, or ICallerScopeResolver not registered in DI."}""";

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

        using var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;
        var action = GetStr(root, "action") ?? "list";

        return action switch
        {
            "list" => await ListAsync(queryPort, caller, ct),
            "upsert" => await UpsertAsync(queryPort, actorRuntime, caller, root, ct),
            "delete" => await DeleteAsync(queryPort, actorRuntime, caller, root, ct),
            _ => await ListAsync(queryPort, caller, ct),
        };
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

    private async Task<string> ListAsync(IUserAgentCatalogQueryPort queryPort, OwnerScope caller, CancellationToken ct)
    {
        var entries = await queryPort.QueryByCallerAsync(caller, ct);
        var result = entries
            .Select(static entry => new
            {
                agent_id = entry.AgentId,
                delivery_target_id = entry.AgentId,
                platform = entry.OwnerScope?.Platform ?? string.Empty,
                conversation_id = entry.ConversationId,
                nyx_provider_slug = entry.NyxProviderSlug,
                created_at = entry.CreatedAt,
                updated_at = entry.UpdatedAt,
            })
            .ToArray();

        return JsonSerializer.Serialize(new { delivery_targets = result, total = result.Length });
    }

    private async Task<string> UpsertAsync(
        IUserAgentCatalogQueryPort queryPort,
        IActorRuntime actorRuntime,
        OwnerScope caller,
        JsonElement args,
        CancellationToken ct)
    {
        var agentId = GetRequired(args, "agent_id", "delivery_target_id", "agentId", "deliveryTargetId");
        if (agentId.error != null)
            return agentId.error;

        var conversationId = GetRequired(args, "conversation_id", "conversationId");
        if (conversationId.error != null)
            return conversationId.error;

        var nyxProviderSlug = GetRequired(args, "nyx_provider_slug", "nyxProviderSlug");
        if (nyxProviderSlug.error != null)
            return nyxProviderSlug.error;

        // Platform argument is informational only — the canonical platform on the
        // upsert is the caller's platform from OwnerScope. The argument can be omitted.
        // We disregard it to avoid the LLM steering an upsert into a different platform
        // bucket than the surface the request actually came from.
        var platform = caller.Platform;

        // Issue #466 review: this tool no longer accepts NyxApiKey as an argument
        // (avoiding LLM credential exposure). The actor's MergeNonEmpty policy preserves
        // the existing key on partial upserts — but a *create* with no existing entry
        // would land a credential-less delivery target that can't dispatch outbound. Fail
        // closed instead of silently producing a broken entry. Real creation flows go
        // through AgentBuilderTool which mints the key inline.
        var existingForCaller = await queryPort.GetForCallerAsync(agentId.value!, caller, ct);
        if (existingForCaller is null)
        {
            return JsonSerializer.Serialize(new
            {
                error = "delivery_target_not_found_for_caller",
                hint = "agent_delivery_targets.upsert is a rebind operation only — it preserves the existing API key. To create a new agent (which mints credentials), use the agent_builder tool instead.",
            });
        }

        var projectionPort = _serviceProvider.GetService<UserAgentCatalogProjectionPort>();
        if (projectionPort != null)
            await projectionPort.EnsureProjectionForActorAsync(UserAgentCatalogGAgent.WellKnownId, ct);

        var versionBefore = await queryPort.GetStateVersionForCallerAsync(agentId.value!, caller, ct) ?? -1;

        var actor = await actorRuntime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
                    ?? await actorRuntime.CreateAsync<UserAgentCatalogGAgent>(UserAgentCatalogGAgent.WellKnownId);

#pragma warning disable CS0612 // legacy fields written for rollback safety during owner_scope migration
        var cmd = new UserAgentCatalogUpsertCommand
        {
            AgentId = agentId.value!,
            Platform = platform,
            ConversationId = conversationId.value!,
            NyxProviderSlug = nyxProviderSlug.value!,
            // NyxApiKey intentionally not accepted as a tool argument; the LLM should
            // never see / pass plaintext credentials. Existing credentials on the entry
            // are preserved through the actor's MergeNonEmpty upsert policy. Issue #466.
            NyxApiKey = string.Empty,
            OwnerNyxUserId = caller.NyxUserId,
            OwnerScope = caller.Clone(),
        };
#pragma warning restore CS0612

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(cmd),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = actor.Id },
            },
        };

        await actor.HandleEventAsync(envelope);

        var confirmed = false;
        for (var attempt = 0; attempt < _projectionWaitAttempts; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(_projectionWaitDelayMilliseconds, ct);

            var versionAfter = await queryPort.GetStateVersionForCallerAsync(agentId.value!, caller, ct) ?? -1;
            if (versionAfter <= versionBefore)
                continue;

            var after = await queryPort.GetForCallerAsync(agentId.value!, caller, ct);
            if (after == null)
                continue;

            if (string.Equals(after.OwnerScope?.Platform ?? string.Empty, platform, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(after.ConversationId, conversationId.value, StringComparison.Ordinal) &&
                string.Equals(after.NyxProviderSlug, nyxProviderSlug.value, StringComparison.Ordinal))
            {
                confirmed = true;
                break;
            }
        }

        return JsonSerializer.Serialize(new
        {
            status = confirmed ? "upserted" : "accepted",
            agent_id = agentId.value,
            delivery_target_id = agentId.value,
            platform,
            conversation_id = conversationId.value,
            nyx_provider_slug = nyxProviderSlug.value,
            note = confirmed ? "" : "Delivery target submitted but projection not yet confirmed. Try 'list' after a few seconds.",
        });
    }

    private async Task<string> DeleteAsync(
        IUserAgentCatalogQueryPort queryPort,
        IActorRuntime actorRuntime,
        OwnerScope caller,
        JsonElement args,
        CancellationToken ct)
    {
        var agentId = GetStr(args, "agent_id", "delivery_target_id", "id", "agentId", "deliveryTargetId");
        if (string.IsNullOrWhiteSpace(agentId))
            return """{"error":"'agent_id' is required for delete"}""";

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

        // Capture version + ensure projection scope is alive (matches the Upsert path
        // above). Without priming, an idle-deactivated projection grain leaves the
        // tombstone enqueued with no consumer and the document persists indefinitely.
        var versionBefore = await queryPort.GetStateVersionForCallerAsync(agentId, caller, ct) ?? -1;

        var projectionPort = _serviceProvider.GetService<UserAgentCatalogProjectionPort>();
        if (projectionPort != null)
            await projectionPort.EnsureProjectionForActorAsync(UserAgentCatalogGAgent.WellKnownId, ct);

        var actor = await actorRuntime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
                    ?? await actorRuntime.CreateAsync<UserAgentCatalogGAgent>(UserAgentCatalogGAgent.WellKnownId);

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(new UserAgentCatalogTombstoneCommand
            {
                AgentId = agentId,
            }),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = actor.Id },
            },
        };

        await actor.HandleEventAsync(envelope);

        // Tombstone triggers IProjectionWriteDispatcher.DeleteAsync (Channel RFC §7.1.1),
        // which also removes the document's projected StateVersion. Gate confirmation
        // on either document absence or a state-version advance that materializes the
        // delete — the prior absence-only check returned false negatives whenever the
        // 5 s budget lost the race to projection lag.
        var confirmed = false;
        for (var attempt = 0; attempt < _projectionWaitAttempts; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(_projectionWaitDelayMilliseconds, ct);

            var versionAfter = await queryPort.GetStateVersionForCallerAsync(agentId, caller, ct);
            if (versionAfter == null)
            {
                confirmed = true;
                break;
            }

            if (versionAfter.Value <= versionBefore)
                continue;

            if (await queryPort.GetForCallerAsync(agentId, caller, ct) == null)
            {
                confirmed = true;
                break;
            }
        }

        return JsonSerializer.Serialize(new
        {
            status = confirmed ? "deleted" : "accepted",
            agent_id = agentId,
            delivery_target_id = agentId,
            note = confirmed ? "" : "Tombstone is propagating. Try 'list' in a few seconds to confirm the delivery target is gone.",
        });
    }

    private static (string? value, string? error) GetRequired(JsonElement args, params string[] keys)
    {
        var value = GetStr(args, keys)?.Trim();
        if (!string.IsNullOrWhiteSpace(value))
            return (value, null);

        return (null, JsonSerializer.Serialize(new { error = $"'{keys[0]}' is required for upsert" }));
    }
}
