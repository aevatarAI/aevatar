using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Tool for NyxId chat to manage Aevatar channel bot registrations.
/// Allows the agent to register, list, and delete channel bots so users
/// don't need to call the REST API manually.
/// </summary>
public sealed class ChannelRegistrationTool : IAgentTool
{
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Constructor takes IServiceProvider only. Dependencies (IChannelBotRegistrationQueryPort,
    /// IActorRuntime) are resolved lazily in ExecuteAsync — at call time the Orleans silo is
    /// fully started and all services are available. This avoids construction-time DI failures
    /// when the tool is instantiated during grain activation.
    /// </summary>
    public ChannelRegistrationTool(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public string Name => "channel_registrations";

    public string Description =>
        "Manage Aevatar channel bot registrations (Lark, Telegram, Discord). " +
        "Actions: list, register, register_lark_via_nyx, delete, update_token. " +
        "Use this to set up platform bot callbacks so users can chat with agents via messaging apps. " +
        "Use register_lark_via_nyx for the Nyx-backed Lark relay path so Aevatar stores only Nyx IDs and no Lark credentials. " +
        "Use register only for legacy non-Lark direct callbacks. " +
        "Use update_token to refresh the NyxID access token on an existing legacy registration when the old token expires.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "register", "register_lark_via_nyx", "delete", "update_token"],
              "description": "Action to perform (default: list). Use register for legacy non-Lark direct callbacks, register_lark_via_nyx for the Nyx relay path, and update_token to refresh the stored NyxID token on an existing legacy registration."
            },
            "platform": {
              "type": "string",
              "enum": ["lark", "telegram", "discord"],
              "description": "Platform (for register)"
            },
            "nyx_provider_slug": {
              "type": "string",
              "description": "NyxID bot service slug, e.g. 'api-lark-bot' (for register, optional for register_lark_via_nyx)"
            },
            "verification_token": {
              "type": "string",
              "description": "Platform verification token (for register, optional)"
            },
            "scope_id": {
              "type": "string",
              "description": "Scope ID for multi-tenant isolation (for register, optional)"
            },
            "webhook_base_url": {
              "type": "string",
              "description": "Base URL for webhook callbacks, e.g. 'https://aevatar-console-backend-api.aevatar.ai' (required for register_lark_via_nyx; optional for legacy register)"
            },
            "credential_ref": {
              "type": "string",
              "description": "Opaque credential reference for the platform secret (for register, optional). For Lark this should resolve to either the raw encrypt key or a JSON secret containing encrypt_key."
            },
            "nyx_refresh_token": {
              "type": "string",
              "description": "NyxID refresh token to store alongside the access token (for register, update_token). If omitted, the tool falls back to nyxid.refresh_token metadata when available; update_token otherwise preserves the stored refresh token."
            },
            "registration_id": {
              "type": "string",
              "description": "Registration ID (for delete, update_token)"
            },
            "app_id": {
              "type": "string",
              "description": "Lark app ID (required for register_lark_via_nyx)"
            },
            "app_secret": {
              "type": "string",
              "description": "Lark app secret (required for register_lark_via_nyx)"
            },
            "label": {
              "type": "string",
              "description": "Human-readable label for the Nyx channel bot (optional for register_lark_via_nyx)"
            },
            "confirm": {
              "type": "boolean",
              "description": "Must be true to execute delete. First call delete without confirm to see details, then call again with confirm=true."
            }
          }
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";
        var refreshTokenMetadata = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdRefreshToken);

        using var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;
        var action = GetStr(root, "action") ?? "list";

        return action switch
        {
            "list" => await ExecuteWithRuntimeAsync((queryPort, _) => ListAsync(queryPort, ct)),
            "register" => await ExecuteWithRuntimeAsync((queryPort, actorRuntime) =>
                RegisterAsync(queryPort, actorRuntime, token, ResolveRegisterRefreshToken(root, refreshTokenMetadata), root, ct)),
            "register_lark_via_nyx" => await RegisterLarkViaNyxAsync(root, ct),
            "delete" => await ExecuteWithRuntimeAsync((queryPort, actorRuntime) => DeleteAsync(queryPort, actorRuntime, root, ct)),
            "update_token" => await ExecuteWithRuntimeAsync((queryPort, actorRuntime) =>
                UpdateTokenAsync(queryPort, actorRuntime, token, refreshTokenMetadata, root, ct)),
            _ => await ExecuteWithRuntimeAsync((queryPort, _) => ListAsync(queryPort, ct)),
        };
    }

    private async Task<string> ExecuteWithRuntimeAsync(
        Func<IChannelBotRegistrationQueryPort, IActorRuntime, Task<string>> operation)
    {
        var queryPort = _serviceProvider.GetService<IChannelBotRegistrationQueryPort>();
        var actorRuntime = _serviceProvider.GetService<IActorRuntime>();
        if (queryPort is null || actorRuntime is null)
            return """{"error":"Channel runtime not available. IChannelBotRegistrationQueryPort or IActorRuntime not registered in DI."}""";

        return await operation(queryPort, actorRuntime);
    }

    private static string? GetStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool TryGetProvidedString(JsonElement el, string prop, out string? value)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
        {
            value = v.GetString();
            return true;
        }

        value = null;
        return false;
    }

    internal static string ResolveRegisterRefreshToken(JsonElement args, string? metadataRefreshToken)
    {
        if (TryGetProvidedString(args, "nyx_refresh_token", out var explicitRefreshToken))
            return explicitRefreshToken?.Trim() ?? string.Empty;

        return !string.IsNullOrWhiteSpace(metadataRefreshToken)
            ? metadataRefreshToken.Trim()
            : string.Empty;
    }

    internal static string ResolveUpdateRefreshToken(
        JsonElement args,
        string? metadataRefreshToken,
        string? existingRefreshToken)
    {
        if (TryGetProvidedString(args, "nyx_refresh_token", out var explicitRefreshToken))
            return explicitRefreshToken?.Trim() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(metadataRefreshToken))
            return metadataRefreshToken.Trim();

        return existingRefreshToken?.Trim() ?? string.Empty;
    }

    private static ChannelBotLegacyDirectBinding? BuildLegacyDirectBinding(
        string? nyxUserToken,
        string? nyxRefreshToken,
        string? verificationToken,
        string? credentialRef)
    {
        var userToken = nyxUserToken?.Trim() ?? string.Empty;
        var refreshToken = nyxRefreshToken?.Trim() ?? string.Empty;
        var verifyToken = verificationToken?.Trim() ?? string.Empty;
        var secretRef = credentialRef?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userToken) &&
            string.IsNullOrWhiteSpace(refreshToken) &&
            string.IsNullOrWhiteSpace(verifyToken) &&
            string.IsNullOrWhiteSpace(secretRef))
        {
            return null;
        }

        return new ChannelBotLegacyDirectBinding
        {
            NyxUserToken = userToken,
            NyxRefreshToken = refreshToken,
            VerificationToken = verifyToken,
            CredentialRef = secretRef,
            EncryptKey = string.Empty,
        };
    }

    private static ChannelBotLegacyDirectBinding MergeLegacyDirectBinding(
        ChannelBotLegacyDirectBinding? existing,
        string userToken,
        string refreshToken) =>
        new()
        {
            NyxUserToken = userToken,
            NyxRefreshToken = refreshToken,
            VerificationToken = existing?.VerificationToken ?? string.Empty,
            CredentialRef = existing?.CredentialRef ?? string.Empty,
            EncryptKey = existing?.EncryptKey ?? string.Empty,
        };

    private async Task<string> ListAsync(IChannelBotRegistrationQueryPort queryPort, CancellationToken ct)
    {
        var registrations = await queryPort.QueryAllAsync(ct);
        var result = registrations.Select(e => new
        {
            id = e.Id,
            platform = e.Platform,
            registration_mode = string.IsNullOrWhiteSpace(e.NyxAgentApiKeyId) ? "legacy_direct_callback" : "nyx_relay_webhook",
            nyx_provider_slug = e.NyxProviderSlug,
            scope_id = e.ScopeId,
            webhook_url = e.WebhookUrl,
            callback_url = string.IsNullOrWhiteSpace(e.NyxAgentApiKeyId)
                ? $"/api/channels/{e.Platform}/callback/{e.Id}"
                : string.Empty,
            nyx_channel_bot_id = e.NyxChannelBotId,
            nyx_agent_api_key_id = e.NyxAgentApiKeyId,
            nyx_conversation_route_id = e.NyxConversationRouteId,
        }).ToList();

        return JsonSerializer.Serialize(new { registrations = result, total = result.Count },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
    }

    private async Task<string> RegisterAsync(
        IChannelBotRegistrationQueryPort queryPort, IActorRuntime actorRuntime,
        string token, string refreshToken, JsonElement args, CancellationToken ct)
    {
        var platform = GetStr(args, "platform");
        if (string.IsNullOrWhiteSpace(platform))
            return """{"error":"'platform' is required for register"}""";

        if (string.Equals(platform.Trim(), "lark", StringComparison.OrdinalIgnoreCase))
        {
            return """
                {"error":"Direct Lark registration is retired. Use action=register_lark_via_nyx so Lark webhook ingress goes through NyxID."}
                """;
        }

        var nyxProviderSlug = GetStr(args, "nyx_provider_slug");
        if (string.IsNullOrWhiteSpace(nyxProviderSlug))
            return """{"error":"'nyx_provider_slug' is required for register (e.g. 'api-lark-bot')"}""";

        var webhookBaseUrl = GetStr(args, "webhook_base_url") ?? "";
        var callbackPath = $"/api/channels/{platform.Trim().ToLowerInvariant()}/callback";
        var webhookUrl = !string.IsNullOrWhiteSpace(webhookBaseUrl)
            ? webhookBaseUrl.TrimEnd('/') + callbackPath
            : string.Empty;

        // Ensure projection scope is activated before dispatch
        var projectionPort = _serviceProvider.GetService<ChannelBotRegistrationProjectionPort>();
        if (projectionPort != null)
            await projectionPort.EnsureProjectionForActorAsync(ChannelBotRegistrationGAgent.WellKnownId, ct);

        var registrationId = Guid.NewGuid().ToString("N");

        var actor = await actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
                    ?? await actorRuntime.CreateAsync<ChannelBotRegistrationGAgent>(
                        ChannelBotRegistrationGAgent.WellKnownId);

        var cmd = new ChannelBotRegisterCommand
        {
            Platform = platform.Trim().ToLowerInvariant(),
            NyxProviderSlug = nyxProviderSlug.Trim(),
            ScopeId = GetStr(args, "scope_id")?.Trim() ?? string.Empty,
            WebhookUrl = webhookUrl,
            RequestedId = registrationId,
            LegacyDirectBinding = BuildLegacyDirectBinding(
                token,
                refreshToken,
                GetStr(args, "verification_token"),
                GetStr(args, "credential_ref")),
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

        // Projection scope is now activated. Poll for the document to appear.
        // The projector runs async via the materialization scope agent.
        string? confirmedId = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(500, ct);
            var entry = await queryPort.GetAsync(registrationId, ct);
            if (entry != null)
            {
                confirmedId = entry.Id;
                break;
            }
        }

        return JsonSerializer.Serialize(new
        {
            status = confirmedId != null ? "registered" : "accepted",
            registration_id = registrationId,
            platform = cmd.Platform,
            nyx_provider_slug = cmd.NyxProviderSlug,
            callback_url = $"{callbackPath}/{registrationId}",
            webhook_url = !string.IsNullOrWhiteSpace(webhookUrl) ? $"{webhookUrl}/{registrationId}" : "",
            auto_refresh_ready = !string.IsNullOrWhiteSpace(cmd.LegacyDirectBinding?.NyxRefreshToken),
            note = confirmedId == null ? "Registration submitted but projection not yet confirmed. Try 'list' after a few seconds." : "",
        });
    }

    private async Task<string> RegisterLarkViaNyxAsync(JsonElement args, CancellationToken ct)
    {
        var provisioningService = _serviceProvider.GetService<INyxLarkProvisioningService>();
        if (provisioningService is null)
            return """{"error":"Nyx-backed Lark provisioning service is not registered."}""";

        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        var result = await provisioningService.ProvisionAsync(
            new NyxLarkProvisioningRequest(
                AccessToken: token,
                AppId: GetStr(args, "app_id")?.Trim() ?? string.Empty,
                AppSecret: GetStr(args, "app_secret")?.Trim() ?? string.Empty,
                WebhookBaseUrl: GetStr(args, "webhook_base_url")?.Trim() ?? string.Empty,
                ScopeId: GetStr(args, "scope_id")?.Trim() ?? string.Empty,
                Label: GetStr(args, "label")?.Trim() ?? string.Empty,
                NyxProviderSlug: GetStr(args, "nyx_provider_slug")?.Trim() ?? string.Empty),
            ct);

        return JsonSerializer.Serialize(new
        {
            status = result.Status,
            registration_id = result.RegistrationId ?? string.Empty,
            platform = "lark",
            nyx_provider_slug = string.IsNullOrWhiteSpace(GetStr(args, "nyx_provider_slug"))
                ? "api-lark-bot"
                : GetStr(args, "nyx_provider_slug")!.Trim(),
            nyx_channel_bot_id = result.NyxChannelBotId ?? string.Empty,
            nyx_agent_api_key_id = result.NyxAgentApiKeyId ?? string.Empty,
            nyx_conversation_route_id = result.NyxConversationRouteId ?? string.Empty,
            relay_callback_url = result.RelayCallbackUrl ?? string.Empty,
            webhook_url = result.WebhookUrl ?? string.Empty,
            error = result.Error ?? string.Empty,
            note = result.Note ?? string.Empty,
        });
    }

    private async Task<string> UpdateTokenAsync(
        IChannelBotRegistrationQueryPort queryPort, IActorRuntime actorRuntime,
        string token, string? metadataRefreshToken, JsonElement args, CancellationToken ct)
    {
        var registrationId = GetStr(args, "registration_id") ?? GetStr(args, "id");
        if (string.IsNullOrWhiteSpace(registrationId))
            return """{"error":"'registration_id' is required for update_token"}""";

        var exists = await queryPort.GetAsync(registrationId, ct);
        if (exists is null)
            return JsonSerializer.Serialize(new { error = $"Registration '{registrationId}' not found" });

        if (string.Equals(exists.Platform, "lark", StringComparison.OrdinalIgnoreCase))
        {
            return """
                {"error":"Lark token refresh is not supported on the Nyx relay path. Re-provision through register_lark_via_nyx and use Nyx relay callbacks instead of persisted Nyx session tokens."}
                """;
        }
        var runtimeQueryPort = _serviceProvider.GetService<IChannelBotRegistrationRuntimeQueryPort>();
        var runtimeRegistration = runtimeQueryPort is null
            ? exists
            : await runtimeQueryPort.GetAsync(registrationId, ct) ?? exists;
        var refreshToken = ResolveUpdateRefreshToken(
            args,
            metadataRefreshToken,
            runtimeRegistration.LegacyDirectBinding?.NyxRefreshToken);

        // Snapshot the projection version before dispatch. An orphaned projection
        // document (from a deleted registration) retains a stale version that never
        // advances because the projector only re-upserts entries still in actor state.
        var versionBefore = await queryPort.GetStateVersionAsync(registrationId, ct) ?? -1;

        // Always dispatch to the actor — it is the authority on current state.
        var actor = await actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
                    ?? await actorRuntime.CreateAsync<ChannelBotRegistrationGAgent>(
                        ChannelBotRegistrationGAgent.WellKnownId);

        var cmd = new ChannelBotUpdateTokenCommand
        {
            RegistrationId = registrationId,
            LegacyDirectBinding = MergeLegacyDirectBinding(
                runtimeRegistration.LegacyDirectBinding,
                token,
                refreshToken),
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

        // Poll projection: require BOTH version advance (proves the actor persisted
        // an event — not an orphaned document) AND desired token visible.
        var confirmed = false;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(500, ct);
            var versionAfter = await queryPort.GetStateVersionAsync(registrationId, ct) ?? -1;
            if (versionAfter <= versionBefore)
                continue;
            var after = runtimeQueryPort is null
                ? await queryPort.GetAsync(registrationId, ct)
                : await runtimeQueryPort.GetAsync(registrationId, ct);
            if (after is not null &&
                string.Equals(after.LegacyDirectBinding?.NyxUserToken, token, StringComparison.Ordinal) &&
                string.Equals(after.LegacyDirectBinding?.NyxRefreshToken, refreshToken, StringComparison.Ordinal))
            {
                confirmed = true;
                break;
            }
        }

        if (!confirmed)
        {
            return JsonSerializer.Serialize(new
            {
                status = "error",
                registration_id = registrationId,
                error = "Token update was dispatched but not confirmed — the projection did not reflect the change. " +
                        "The registration may not exist in the actor's state. Try delete + re-register.",
            });
        }

        return JsonSerializer.Serialize(new
        {
            status = "token_updated",
            registration_id = registrationId,
            platform = exists.Platform,
            auto_refresh_ready = !string.IsNullOrWhiteSpace(refreshToken),
            note = !string.IsNullOrWhiteSpace(refreshToken)
                ? "NyxID access and refresh tokens have been updated. Lark outbound replies can auto-refresh."
                : "NyxID access token has been refreshed, but no refresh token was stored. Manual re-auth will still be required when it expires.",
        });
    }

    private async Task<string> DeleteAsync(
        IChannelBotRegistrationQueryPort queryPort, IActorRuntime actorRuntime,
        JsonElement args, CancellationToken ct)
    {
        var registrationId = GetStr(args, "registration_id") ?? GetStr(args, "id");
        if (string.IsNullOrWhiteSpace(registrationId))
            return """{"error":"'registration_id' is required for delete"}""";

        var exists = await queryPort.GetAsync(registrationId, ct);
        if (exists is null)
            return JsonSerializer.Serialize(new { error = $"Registration '{registrationId}' not found" });

        var confirm = args.TryGetProperty("confirm", out var cv) && cv.ValueKind == JsonValueKind.True;
        if (!confirm)
        {
            return JsonSerializer.Serialize(new
            {
                status = "confirm_required",
                registration_id = exists.Id,
                platform = exists.Platform,
                registration_mode = string.IsNullOrWhiteSpace(exists.NyxAgentApiKeyId) ? "legacy_direct_callback" : "nyx_relay_webhook",
                nyx_provider_slug = exists.NyxProviderSlug,
                scope_id = exists.ScopeId,
                nyx_channel_bot_id = exists.NyxChannelBotId,
                nyx_agent_api_key_id = exists.NyxAgentApiKeyId,
                nyx_conversation_route_id = exists.NyxConversationRouteId,
                note = "Call again with confirm=true to delete this registration. This action cannot be undone.",
            });
        }

        var actor = await actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
                    ?? await actorRuntime.CreateAsync<ChannelBotRegistrationGAgent>(
                        ChannelBotRegistrationGAgent.WellKnownId);

        var cmd = new ChannelBotUnregisterCommand { RegistrationId = registrationId };
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
        return JsonSerializer.Serialize(new { status = "deleted", registration_id = registrationId });
    }
}
