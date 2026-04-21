using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Tool for managing agent outbound delivery targets used by workflow human interaction cards.
/// </summary>
public sealed class AgentDeliveryTargetTool : IAgentTool
{
    private readonly IServiceProvider _serviceProvider;

    public AgentDeliveryTargetTool(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public string Name => "agent_delivery_targets";

    public string Description =>
        "Manage agent delivery targets for workflow human interaction cards and outbound channel delivery. " +
        "Actions: list, upsert, delete. " +
        "Use this to bind an agent_id/delivery_target_id to a Lark conversation, Nyx provider slug, and Nyx API key.";

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
            "nyx_api_key": {
              "type": "string",
              "description": "Nyx API key used for outbound proxy requests"
            },
            "owner_nyx_user_id": {
              "type": "string",
              "description": "Optional owner Nyx user ID. If omitted, the current user will be resolved when possible."
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
        if (queryPort is null || actorRuntime is null)
            return """{"error":"Agent delivery target runtime not available. IUserAgentCatalogQueryPort or IActorRuntime not registered in DI."}""";

        using var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;
        var action = GetStr(root, "action") ?? "list";

        return action switch
        {
            "list" => await ListAsync(queryPort, token, ct),
            "upsert" => await UpsertAsync(queryPort, actorRuntime, token, root, ct),
            "delete" => await DeleteAsync(queryPort, actorRuntime, token, root, ct),
            _ => await ListAsync(queryPort, token, ct),
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

    private async Task<string> ListAsync(IUserAgentCatalogQueryPort queryPort, string token, CancellationToken ct)
    {
        var currentOwner = await ResolveCurrentOwnerNyxUserIdAsync(token, ct);
        if (currentOwner.error != null)
            return currentOwner.error;

        var entries = await queryPort.QueryAllAsync(ct);
        var result = entries
            .Where(entry => string.Equals(entry.OwnerNyxUserId, currentOwner.value, StringComparison.Ordinal))
            .Select(static entry => new
            {
                agent_id = entry.AgentId,
                delivery_target_id = entry.AgentId,
                platform = entry.Platform,
                conversation_id = entry.ConversationId,
                nyx_provider_slug = entry.NyxProviderSlug,
                nyx_api_key_hint = MaskSecret(entry.NyxApiKey),
                owner_nyx_user_id = entry.OwnerNyxUserId,
                created_at = entry.CreatedAt,
                updated_at = entry.UpdatedAt,
            })
            .ToArray();

        return JsonSerializer.Serialize(new { delivery_targets = result, total = result.Length });
    }

    private async Task<string> UpsertAsync(
        IUserAgentCatalogQueryPort queryPort,
        IActorRuntime actorRuntime,
        string token,
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

        var nyxApiKey = GetRequired(args, "nyx_api_key", "nyxApiKey");
        if (nyxApiKey.error != null)
            return nyxApiKey.error;

        var platform = (GetStr(args, "platform") ?? "lark").Trim().ToLowerInvariant();
        var ownerNyxUserId = await ResolveOwnerNyxUserIdAsync(token, args, ct);
        if (string.IsNullOrWhiteSpace(ownerNyxUserId))
        {
            return JsonSerializer.Serialize(new
            {
                error = "Could not resolve current NyxID user id for delivery target ownership.",
            });
        }

        var projectionPort = _serviceProvider.GetService<UserAgentCatalogProjectionPort>();
        if (projectionPort != null)
            await projectionPort.EnsureProjectionForActorAsync(UserAgentCatalogGAgent.WellKnownId, ct);

        var versionBefore = await queryPort.GetStateVersionAsync(agentId.value!, ct) ?? -1;

        var actor = await actorRuntime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
                    ?? await actorRuntime.CreateAsync<UserAgentCatalogGAgent>(UserAgentCatalogGAgent.WellKnownId);

        var cmd = new UserAgentCatalogUpsertCommand
        {
            AgentId = agentId.value!,
            Platform = platform,
            ConversationId = conversationId.value!,
            NyxProviderSlug = nyxProviderSlug.value!,
            NyxApiKey = nyxApiKey.value!,
            OwnerNyxUserId = ownerNyxUserId,
        };

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
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(500, ct);

            var versionAfter = await queryPort.GetStateVersionAsync(agentId.value!, ct) ?? -1;
            if (versionAfter <= versionBefore)
                continue;

            var after = await queryPort.GetAsync(agentId.value!, ct);
            if (after == null)
                continue;

            if (string.Equals(after.Platform, platform, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(after.ConversationId, conversationId.value, StringComparison.Ordinal) &&
                string.Equals(after.NyxProviderSlug, nyxProviderSlug.value, StringComparison.Ordinal) &&
                string.Equals(after.NyxApiKey, nyxApiKey.value, StringComparison.Ordinal))
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
            nyx_api_key_hint = MaskSecret(nyxApiKey.value!),
            owner_nyx_user_id = ownerNyxUserId,
            note = confirmed ? "" : "Delivery target submitted but projection not yet confirmed. Try 'list' after a few seconds.",
        });
    }

    private async Task<string> DeleteAsync(
        IUserAgentCatalogQueryPort queryPort,
        IActorRuntime actorRuntime,
        string token,
        JsonElement args,
        CancellationToken ct)
    {
        var agentId = GetStr(args, "agent_id", "delivery_target_id", "id", "agentId", "deliveryTargetId");
        if (string.IsNullOrWhiteSpace(agentId))
            return """{"error":"'agent_id' is required for delete"}""";

        var exists = await queryPort.GetAsync(agentId, ct);
        if (exists is null)
            return JsonSerializer.Serialize(new { error = $"Delivery target '{agentId}' not found" });

        var currentOwner = await ResolveCurrentOwnerNyxUserIdAsync(token, ct);
        if (currentOwner.error != null)
            return currentOwner.error;

        if (!string.Equals(exists.OwnerNyxUserId, currentOwner.value, StringComparison.Ordinal))
            return JsonSerializer.Serialize(new { error = $"Delivery target '{agentId}' not found" });

        if (!GetBool(args, "confirm"))
        {
            return JsonSerializer.Serialize(new
            {
                status = "confirm_required",
                agent_id = exists.AgentId,
                delivery_target_id = exists.AgentId,
                platform = exists.Platform,
                conversation_id = exists.ConversationId,
                nyx_provider_slug = exists.NyxProviderSlug,
                note = "Call again with confirm=true to delete this delivery target mapping.",
            });
        }

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
        // purely on document absence — versionAfter would be null after the delete lands.
        var confirmed = false;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(500, ct);

            if (await queryPort.GetAsync(agentId, ct) == null)
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
            note = confirmed ? "" : "Delete submitted but projection not yet confirmed. Try 'list' after a few seconds.",
        });
    }

    private async Task<string> ResolveOwnerNyxUserIdAsync(string token, JsonElement args, CancellationToken ct)
    {
        var explicitOwner = GetStr(args, "owner_nyx_user_id", "ownerNyxUserId");
        if (!string.IsNullOrWhiteSpace(explicitOwner))
            return explicitOwner.Trim();

        var currentOwner = await ResolveCurrentOwnerNyxUserIdAsync(token, ct);
        return currentOwner.value ?? string.Empty;
    }

    private async Task<(string? value, string? error)> ResolveCurrentOwnerNyxUserIdAsync(string token, CancellationToken ct)
    {
        var client = _serviceProvider.GetService<NyxIdApiClient>();
        if (client == null)
        {
            return (null, JsonSerializer.Serialize(new
            {
                error = "Agent delivery target runtime not available. NyxIdApiClient not registered in DI.",
            }));
        }

        try
        {
            using var doc = JsonDocument.Parse(await client.GetCurrentUserAsync(token, ct));
            var ownerNyxUserId = TryReadOwnerNyxUserId(doc.RootElement);
            if (!string.IsNullOrWhiteSpace(ownerNyxUserId))
                return (ownerNyxUserId, null);

            return (null, JsonSerializer.Serialize(new
            {
                error = "Could not resolve current NyxID user id.",
            }));
        }
        catch
        {
            return (null, JsonSerializer.Serialize(new
            {
                error = "Could not resolve current NyxID user id.",
            }));
        }
    }

    private static string? TryReadOwnerNyxUserId(JsonElement root)
    {
        if (TryReadString(root, "id", "user_id", "sub") is { } direct)
            return direct;

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("user", out var user) &&
            TryReadString(user, "id", "user_id", "sub") is { } nested)
        {
            return nested;
        }

        return null;
    }

    private static string? TryReadString(JsonElement element, params string[] properties)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in properties)
        {
            if (element.TryGetProperty(property, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static (string? value, string? error) GetRequired(JsonElement args, params string[] keys)
    {
        var value = GetStr(args, keys)?.Trim();
        if (!string.IsNullOrWhiteSpace(value))
            return (value, null);

        return (null, JsonSerializer.Serialize(new { error = $"'{keys[0]}' is required for upsert" }));
    }

    private static string MaskSecret(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        if (value.Length <= 4)
            return new string('*', value.Length);

        return $"***{value[^4..]}";
    }
}
