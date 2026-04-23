using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Tool for NyxID chat to manage ChannelRuntime registrations.
/// ChannelRuntime is no longer a channel credential authority; the supported
/// production path is Lark registration provisioned through Nyx relay.
/// </summary>
public sealed class ChannelRegistrationTool : IAgentTool
{
    private const string DefaultNyxProviderSlug = "api-lark-bot";
    private readonly IServiceProvider _serviceProvider;

    public ChannelRegistrationTool(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public string Name => "channel_registrations";

    public string Description =>
        "Manage Aevatar ChannelRuntime registrations for the supported Nyx-backed Lark relay flow. " +
        "Actions: list, register_lark_via_nyx, rebuild_projection, repair_lark_mirror, delete. " +
        "Use register_lark_via_nyx for first-time provisioning, rebuild_projection to re-materialize the local registration read model from the authoritative actor state, and repair_lark_mirror when Nyx relay resources already exist but the local Aevatar mirror is missing. " +
        "Legacy direct callback registration and update_token flows are retired because ChannelRuntime no longer stores channel credentials. " +
        "Repair requires verified Nyx bot/api-key state plus an existing relay credential reference that still resolves in the local secrets store.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "register_lark_via_nyx", "rebuild_projection", "repair_lark_mirror", "delete"],
              "description": "Action to perform (default: list)."
            },
            "nyx_provider_slug": {
              "type": "string",
              "description": "NyxID bot service slug (optional for register_lark_via_nyx; defaults to api-lark-bot)"
            },
            "scope_id": {
              "type": "string",
              "description": "Scope ID for multi-tenant isolation (optional)"
            },
            "webhook_base_url": {
              "type": "string",
              "description": "Base URL for Nyx relay callbacks, e.g. 'https://aevatar-console-backend-api.aevatar.ai' (required for register_lark_via_nyx)"
            },
            "app_id": {
              "type": "string",
              "description": "Lark app ID (required for register_lark_via_nyx)"
            },
            "app_secret": {
              "type": "string",
              "description": "Lark app secret (required for register_lark_via_nyx)"
            },
            "verification_token": {
              "type": "string",
              "description": "Lark verification token (optional for register_lark_via_nyx, but pass it through when the backend requires it)"
            },
            "label": {
              "type": "string",
              "description": "Human-readable label for the Nyx channel bot (optional)"
            },
            "nyx_channel_bot_id": {
              "type": "string",
              "description": "Existing Nyx channel bot ID (required for repair_lark_mirror)"
            },
            "nyx_agent_api_key_id": {
              "type": "string",
              "description": "Existing Nyx relay API key ID whose callback points at Aevatar (required for repair_lark_mirror)"
            },
            "nyx_conversation_route_id": {
              "type": "string",
              "description": "Existing Nyx conversation route ID (optional for repair_lark_mirror, but strongly recommended)"
            },
            "credential_ref": {
              "type": "string",
              "description": "Existing local relay credential reference to preserve during repair_lark_mirror (optional when registration_id still points at a stored relay secret)"
            },
            "reason": {
              "type": "string",
              "description": "Optional operator reason for rebuild_projection"
            },
            "registration_id": {
              "type": "string",
              "description": "Registration ID (for delete, or optional requested ID for repair_lark_mirror)"
            },
            "confirm": {
              "type": "boolean",
              "description": "Must be true to execute delete. First call delete without confirm to inspect the registration."
            }
          }
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;
        var action = GetStr(root, "action") ?? "list";

        return action switch
        {
            "list" => await ExecuteWithQueryAsync(queryPort => ListAsync(queryPort, ct)),
            "register_lark_via_nyx" => await RegisterLarkViaNyxAsync(token, root, ct),
            "rebuild_projection" => await ExecuteWithStoreAsync((queryPort, actorRuntime, dispatchPort) => RebuildProjectionAsync(queryPort, actorRuntime, dispatchPort, root, ct)),
            "repair_lark_mirror" => await RepairLarkMirrorAsync(root, ct),
            "delete" => await ExecuteWithStoreAsync((queryPort, actorRuntime, dispatchPort) => DeleteAsync(queryPort, actorRuntime, dispatchPort, root, ct)),
            "register" => RetiredActionError("Direct callback registration is retired. Use action=register_lark_via_nyx."),
            "update_token" => RetiredActionError("update_token is retired. ChannelRuntime no longer stores or refreshes channel credentials."),
            _ => await ExecuteWithQueryAsync(queryPort => ListAsync(queryPort, ct)),
        };
    }

    private async Task<string> ExecuteWithQueryAsync(Func<IChannelBotRegistrationQueryPort, Task<string>> operation)
    {
        var queryPort = _serviceProvider.GetService<IChannelBotRegistrationQueryPort>();
        if (queryPort is null)
            return """{"error":"Channel runtime not available. IChannelBotRegistrationQueryPort is not registered in DI."}""";

        return await operation(queryPort);
    }

    private async Task<string> ExecuteWithStoreAsync(
        Func<IChannelBotRegistrationQueryPort, IActorRuntime, IActorDispatchPort, Task<string>> operation)
    {
        var queryPort = _serviceProvider.GetService<IChannelBotRegistrationQueryPort>();
        var actorRuntime = _serviceProvider.GetService<IActorRuntime>();
        var dispatchPort = _serviceProvider.GetService<IActorDispatchPort>();
        if (queryPort is null || actorRuntime is null || dispatchPort is null)
        {
            return """{"error":"Channel runtime not available. IChannelBotRegistrationQueryPort, IActorRuntime, or IActorDispatchPort is not registered in DI."}""";
        }

        return await operation(queryPort, actorRuntime, dispatchPort);
    }

    private static string? GetStr(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ResolveNyxProviderSlug(JsonElement args)
    {
        var slug = GetStr(args, "nyx_provider_slug")?.Trim();
        return string.IsNullOrWhiteSpace(slug) ? DefaultNyxProviderSlug : slug;
    }

    private static string RetiredActionError(string message) =>
        JsonSerializer.Serialize(new
        {
            error_code = "retired_action",
            error = message,
        });

    private static string SerializeLarkRegistrationPayload(
        string status,
        string registrationId,
        string nyxProviderSlug,
        string nyxChannelBotId,
        string nyxAgentApiKeyId,
        string nyxConversationRouteId,
        string relayCallbackUrl,
        string webhookUrl,
        string error,
        string note) =>
        JsonSerializer.Serialize(new
        {
            status,
            registration_id = registrationId,
            platform = "lark",
            nyx_provider_slug = nyxProviderSlug,
            nyx_channel_bot_id = nyxChannelBotId,
            nyx_agent_api_key_id = nyxAgentApiKeyId,
            nyx_conversation_route_id = nyxConversationRouteId,
            relay_callback_url = relayCallbackUrl,
            webhook_url = webhookUrl,
            error,
            note,
        });

    private async Task<string> ListAsync(IChannelBotRegistrationQueryPort queryPort, CancellationToken ct)
    {
        var registrations = await queryPort.QueryAllAsync(ct);
        var result = registrations.Select(entry => new
        {
            id = entry.Id,
            platform = entry.Platform,
            registration_mode = "nyx_relay_webhook",
            nyx_provider_slug = entry.NyxProviderSlug,
            scope_id = entry.ScopeId,
            webhook_url = entry.WebhookUrl,
            callback_url = string.Empty,
            nyx_channel_bot_id = entry.NyxChannelBotId,
            nyx_agent_api_key_id = entry.NyxAgentApiKeyId,
            nyx_conversation_route_id = entry.NyxConversationRouteId,
        }).ToList();

        return JsonSerializer.Serialize(
            new { registrations = result, total = result.Count },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
    }

    private async Task<string> RegisterLarkViaNyxAsync(
        string accessToken,
        JsonElement args,
        CancellationToken ct)
    {
        var provisioningService = _serviceProvider.GetService<INyxLarkProvisioningService>();
        if (provisioningService is null)
            return """{"error":"Nyx-backed Lark provisioning service is not registered."}""";

        var result = await provisioningService.ProvisionAsync(
            new NyxLarkProvisioningRequest(
                AccessToken: accessToken,
                AppId: GetStr(args, "app_id")?.Trim() ?? string.Empty,
                AppSecret: GetStr(args, "app_secret")?.Trim() ?? string.Empty,
                VerificationToken: GetStr(args, "verification_token")?.Trim() ?? string.Empty,
                WebhookBaseUrl: GetStr(args, "webhook_base_url")?.Trim() ?? string.Empty,
                ScopeId: GetStr(args, "scope_id")?.Trim() ?? string.Empty,
                Label: GetStr(args, "label")?.Trim() ?? string.Empty,
                NyxProviderSlug: GetStr(args, "nyx_provider_slug")?.Trim() ?? string.Empty),
            ct);

        return SerializeLarkRegistrationPayload(
            status: result.Status,
            registrationId: result.RegistrationId ?? string.Empty,
            nyxProviderSlug: ResolveNyxProviderSlug(args),
            nyxChannelBotId: result.NyxChannelBotId ?? string.Empty,
            nyxAgentApiKeyId: result.NyxAgentApiKeyId ?? string.Empty,
            nyxConversationRouteId: result.NyxConversationRouteId ?? string.Empty,
            relayCallbackUrl: result.RelayCallbackUrl ?? string.Empty,
            webhookUrl: result.WebhookUrl ?? string.Empty,
            error: result.Error ?? string.Empty,
            note: result.Note ?? string.Empty);
    }

    private async Task<string> RepairLarkMirrorAsync(JsonElement args, CancellationToken ct)
    {
        var provisioningService = _serviceProvider.GetService<INyxLarkProvisioningService>();
        if (provisioningService is null)
            return """{"error":"Nyx-backed Lark provisioning service is not registered."}""";

        var nyxChannelBotId = GetStr(args, "nyx_channel_bot_id")?.Trim() ?? string.Empty;
        var nyxAgentApiKeyId = GetStr(args, "nyx_agent_api_key_id")?.Trim() ?? string.Empty;
        var nyxConversationRouteId = GetStr(args, "nyx_conversation_route_id")?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(nyxChannelBotId))
            return """{"error":"'nyx_channel_bot_id' is required for repair_lark_mirror"}""";
        if (string.IsNullOrWhiteSpace(nyxAgentApiKeyId))
            return """{"error":"'nyx_agent_api_key_id' is required for repair_lark_mirror"}""";

        var queryPort = _serviceProvider.GetService<IChannelBotRegistrationQueryPort>();
        if (queryPort is not null)
        {
            try
            {
                var registrations = await queryPort.QueryAllAsync(ct);
                var existing = registrations.FirstOrDefault(entry =>
                    string.Equals(entry.Platform, "lark", StringComparison.OrdinalIgnoreCase) &&
                    MatchesNyxIdentity(entry, nyxChannelBotId, nyxAgentApiKeyId, nyxConversationRouteId));
                if (existing is not null)
                {
                    return SerializeLarkRegistrationPayload(
                        status: "already_registered",
                        registrationId: existing.Id,
                        nyxProviderSlug: string.IsNullOrWhiteSpace(existing.NyxProviderSlug)
                            ? DefaultNyxProviderSlug
                            : existing.NyxProviderSlug,
                        nyxChannelBotId: existing.NyxChannelBotId,
                        nyxAgentApiKeyId: existing.NyxAgentApiKeyId,
                        nyxConversationRouteId: existing.NyxConversationRouteId,
                        relayCallbackUrl: string.Empty,
                        webhookUrl: existing.WebhookUrl,
                        error: string.Empty,
                        note: "Matching local Aevatar mirror already exists.");
                }
            }
            catch
            {
                // Repair must remain usable even when the query-side projection is degraded.
            }
        }

        var result = await provisioningService.RepairLocalMirrorAsync(
            new NyxLarkMirrorRepairRequest(
                AccessToken: AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken) ?? string.Empty,
                RequestedRegistrationId: GetStr(args, "registration_id")?.Trim() ?? string.Empty,
                ScopeId: GetStr(args, "scope_id")?.Trim() ?? string.Empty,
                NyxProviderSlug: ResolveNyxProviderSlug(args),
                WebhookBaseUrl: GetStr(args, "webhook_base_url")?.Trim() ?? string.Empty,
                NyxChannelBotId: nyxChannelBotId,
                NyxAgentApiKeyId: nyxAgentApiKeyId,
                NyxConversationRouteId: nyxConversationRouteId,
                CredentialRef: GetStr(args, "credential_ref")?.Trim() ?? string.Empty),
            ct);

        return SerializeLarkRegistrationPayload(
            status: result.Status,
            registrationId: result.RegistrationId ?? string.Empty,
            nyxProviderSlug: ResolveNyxProviderSlug(args),
            nyxChannelBotId: result.NyxChannelBotId ?? string.Empty,
            nyxAgentApiKeyId: result.NyxAgentApiKeyId ?? string.Empty,
            nyxConversationRouteId: result.NyxConversationRouteId ?? string.Empty,
            relayCallbackUrl: string.Empty,
            webhookUrl: result.WebhookUrl ?? string.Empty,
            error: result.Error ?? string.Empty,
            note: result.Note ?? string.Empty);
    }

    private async Task<string> RebuildProjectionAsync(
        IChannelBotRegistrationQueryPort queryPort,
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort,
        JsonElement args,
        CancellationToken ct)
    {
        await ChannelBotRegistrationStoreCommands.DispatchRebuildProjectionAsync(
            actorRuntime,
            dispatchPort,
            GetStr(args, "reason")?.Trim() ?? "tool_manual_rebuild",
            ct);

        int? observedRegistrationsBeforeRebuild = null;
        var note = "Projection rebuild dispatched from authoritative channel-bot-registration-store state. Query-side registrations may take a moment to refresh.";
        try
        {
            observedRegistrationsBeforeRebuild = (await queryPort.QueryAllAsync(ct)).Count;
        }
        catch
        {
            note = "Projection rebuild dispatched from authoritative channel-bot-registration-store state. Query-side observation is currently unavailable; registrations may still refresh asynchronously.";
        }

        return JsonSerializer.Serialize(new
        {
            status = "accepted",
            actor_id = ChannelBotRegistrationGAgent.WellKnownId,
            observed_registrations_before_rebuild = observedRegistrationsBeforeRebuild,
            note,
        });
    }

    private static bool MatchesNyxIdentity(
        ChannelBotRegistrationEntry entry,
        string nyxChannelBotId,
        string nyxAgentApiKeyId,
        string nyxConversationRouteId)
    {
        var hasConstraint = false;

        if (!MatchesIfProvided(entry.NyxChannelBotId, nyxChannelBotId, ref hasConstraint))
            return false;
        if (!MatchesIfProvided(entry.NyxAgentApiKeyId, nyxAgentApiKeyId, ref hasConstraint))
            return false;
        if (!MatchesIfProvided(entry.NyxConversationRouteId, nyxConversationRouteId, ref hasConstraint))
            return false;

        return hasConstraint;
    }

    private static bool MatchesIfProvided(string actual, string expected, ref bool hasConstraint)
    {
        if (string.IsNullOrWhiteSpace(expected))
            return true;

        hasConstraint = true;
        return !string.IsNullOrWhiteSpace(actual) &&
               string.Equals(actual, expected, StringComparison.Ordinal);
    }

    private async Task<string> DeleteAsync(
        IChannelBotRegistrationQueryPort queryPort,
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort,
        JsonElement args,
        CancellationToken ct)
    {
        var registrationId = GetStr(args, "registration_id") ?? GetStr(args, "id");
        if (string.IsNullOrWhiteSpace(registrationId))
            return """{"error":"'registration_id' is required for delete"}""";

        var exists = await queryPort.GetAsync(registrationId, ct);
        if (exists is null)
            return JsonSerializer.Serialize(new { error = $"Registration '{registrationId}' not found" });

        var confirm = args.TryGetProperty("confirm", out var confirmValue) && confirmValue.ValueKind == JsonValueKind.True;
        if (!confirm)
        {
            return JsonSerializer.Serialize(new
            {
                status = "confirm_required",
                registration_id = exists.Id,
                platform = exists.Platform,
                registration_mode = "nyx_relay_webhook",
                nyx_provider_slug = exists.NyxProviderSlug,
                scope_id = exists.ScopeId,
                nyx_channel_bot_id = exists.NyxChannelBotId,
                nyx_agent_api_key_id = exists.NyxAgentApiKeyId,
                nyx_conversation_route_id = exists.NyxConversationRouteId,
                note = "Call again with confirm=true to delete this registration. This action cannot be undone.",
            });
        }

        await ChannelBotRegistrationStoreCommands.DispatchUnregisterAsync(
            actorRuntime,
            dispatchPort,
            registrationId,
            ct);

        var confirmed = false;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(500, ct);

            if (await queryPort.GetAsync(registrationId, ct) == null)
            {
                confirmed = true;
                break;
            }
        }

        return JsonSerializer.Serialize(new
        {
            status = confirmed ? "deleted" : "accepted",
            registration_id = registrationId,
            note = confirmed ? string.Empty : "Delete submitted but projection not yet confirmed. Try 'list' after a few seconds.",
        });
    }
}
