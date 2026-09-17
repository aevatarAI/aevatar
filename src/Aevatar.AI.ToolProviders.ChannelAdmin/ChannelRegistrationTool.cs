using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.AI.ToolProviders.ChannelAdmin;

/// <summary>
/// Tool for NyxID chat to list and remove ChannelRuntime registration mirrors.
/// Bot provisioning and channel credentials belong to NyxID; binding existing
/// bots to Aevatar is exposed through the channel registration HTTP API.
/// </summary>
public sealed class ChannelRegistrationTool : IAgentTool
{
    // Refactor (iter83/cluster-083-agent-tool-source-root-provider-locator):
    //   Old pattern: tool source captures root IServiceProvider; tools resolve business ports via service locator in ExecuteAsync
    //   New principle: tool source + tools constructor-inject typed contracts; no root provider lookup
    // Refactor (iter56/cluster-933-channel-registration-rebuild-narrow): old=public rebuild surfaces, new=internal Runtime startup helper only
    // Refactor (iter56/cluster-933-channel-registration-rebuild-narrow): old=manual readmodel rematerialization path, new=startup-owned projection refresh
    // Refactor (iter36/cluster-041-nyx-relay-command-skeleton):
    //   Old pattern: Nyx relay registration endpoints + singleton provisioning services 在 Host 内做 platform selection / scope resolution / remote Nyx provisioning / actor creation / envelope construction / dispatch through raw runtime/dispatch helpers。
    //   New principle: Channel registration 暴露 typed application command facade(reuse existing CQRS command dispatch skeleton);Host 仅 adapt HTTP;provisioning adapters 只调 existing NyxID REST surfaces(**不修改 NyxID 仓库**);local mirror writes 进 standard command skeleton via narrow dispatch port。**不引入新 actor type / 新 envelope / 新 projection phase**(reflector force-pick minimal,排除 structural 的 ChannelRelayRegistrationRunGAgent)。
    private readonly IChannelBotRegistrationQueryPort _queryPort;
    private readonly ChannelRegistrationCommandFacade _commandFacade;
    private readonly INyxChannelBotDeprovisioningService _deprovisioningService;

    public ChannelRegistrationTool(
        IChannelBotRegistrationQueryPort queryPort,
        ChannelRegistrationCommandFacade commandFacade,
        INyxChannelBotDeprovisioningService deprovisioningService)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _commandFacade = commandFacade ?? throw new ArgumentNullException(nameof(commandFacade));
        _deprovisioningService = deprovisioningService ?? throw new ArgumentNullException(nameof(deprovisioningService));
    }

    public string Name => "channel_registrations";

    public string Description =>
        "Manage Aevatar ChannelRuntime local registration mirrors for existing NyxID channel bots. " +
        "Actions: list, delete. " +
        "All actions operate only on registrations in the caller's own scope; list never returns " +
        "other tenants' registrations and delete rejects them as not found. " +
        "Bot provisioning is NyxID-owned; register_channel_via_nyx, direct callback registration, and update_token flows are retired. " +
        "Do not ask the user for scope_id; it is resolved from the current NyxID request context and should only be supplied explicitly for diagnostics.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "delete"],
              "description": "Action to perform (default: list)."
            },
            "scope_id": {
              "type": "string",
              "description": "Scope ID for multi-tenant isolation. Normally supplied from the current NyxID request context; only pass explicitly for diagnostics."
            },
            "registration_id": {
              "type": "string",
              "description": "Registration ID for delete"
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
        var token = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;
        var action = NormalizeOptional(GetStr(root, "action")) ?? "list";

        return action switch
        {
            "list" => await ListAsync(root, ct),
            "delete" => await DeleteAsync(token, root, ct),
            "register_channel_via_nyx" => RetiredActionError("register_channel_via_nyx is retired. Create and manage channel bots in NyxID, then bind them through /api/channels/registrations."),
            "register" => RetiredActionError("Direct callback registration is retired. Create and manage channel bots in NyxID, then bind them through /api/channels/registrations."),
            "register_lark_via_nyx" => RetiredActionError("register_lark_via_nyx is retired. Create and manage channel bots in NyxID, then bind them through /api/channels/registrations."),
            "update_token" => RetiredActionError("update_token is retired. ChannelRuntime no longer stores or refreshes channel credentials."),
            _ => SerializeError($"Unsupported channel registration action '{action}'."),
        };
    }

    private static string? GetStr(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static ToolScopeResolution ResolveToolScopeId(JsonElement args, bool required)
    {
        var explicitScopeId = NormalizeOptional(GetStr(args, "scope_id"));
        var contextScopeId = NormalizeOptional(AgentToolRequestContext.ScopeId);
        if (explicitScopeId is not null &&
            contextScopeId is not null &&
            !string.Equals(explicitScopeId, contextScopeId, StringComparison.Ordinal))
        {
            return new ToolScopeResolution(null, "scope_id does not match the current NyxID request scope");
        }

        var resolved = explicitScopeId ?? contextScopeId;
        if (required && resolved is null)
            return new ToolScopeResolution(null, "scope_id is required from the current NyxID request context");

        return new ToolScopeResolution(resolved, null);
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string SerializeError(string error) =>
        JsonSerializer.Serialize(new { error });

    private sealed record ToolScopeResolution(string? ScopeId, string? Error);

    private static string RetiredActionError(string message) =>
        JsonSerializer.Serialize(new
        {
            error_code = "retired_action",
            error = message,
        });

    private async Task<string> ListAsync(JsonElement args, CancellationToken ct)
    {
        // Tenant isolation (mirrors GET /api/channels/registrations): the read model holds
        // every tenant's registrations, so the tool must filter to the caller's scope. The
        // admin-gated cross-account view stays on the HTTP surface; this tool never offers it.
        var scopeResolution = ResolveToolScopeId(args, required: true);
        if (scopeResolution.Error is not null)
            return SerializeError(scopeResolution.Error);

        var snapshots = await _queryPort.QueryAllSnapshotsAsync(ct);
        var visible = snapshots.Where(snapshot => string.Equals(
            snapshot.Registration.ScopeId,
            scopeResolution.ScopeId,
            StringComparison.Ordinal));
        var result = visible.Select(snapshot =>
        {
            var entry = snapshot.Registration;
            return new
            {
                id = entry.Id,
                platform = entry.Platform,
                registration_mode = "nyx_relay_webhook",
                authorization_mode = MapAuthorizationMode(entry),
                service_ids = MapRegistrationServiceIds(entry),
                state_version = snapshot.StateVersion,
                nyx_provider_slug = entry.NyxProviderSlug,
                scope_id = entry.ScopeId,
                webhook_url = entry.WebhookUrl,
                callback_url = string.Empty,
                nyx_channel_bot_id = entry.NyxChannelBotId,
                nyx_agent_api_key_id = entry.NyxAgentApiKeyId,
                nyx_conversation_route_id = entry.NyxConversationRouteId,
                default_skill_name = entry.DefaultSkillName,
            };
        }).ToArray();

        return JsonSerializer.Serialize(
            new { registrations = result, total = result.Length },
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });
    }

    private static string? MapAuthorizationMode(ChannelBotRegistrationEntry entry) =>
        entry.AuthorizationMode switch
        {
            ChannelRegistrationAuthorizationMode.NyxidDefault => "nyxid_default",
            ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist =>
                "explicit_service_allowlist",
            ChannelRegistrationAuthorizationMode.Unspecified => null,
            _ => "unsupported",
        };

    private static IReadOnlyList<string>? MapRegistrationServiceIds(
        ChannelBotRegistrationEntry entry) =>
        entry.AuthorizationMode == ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist &&
        entry.RegistrationServiceAllowlist is not null
            ? entry.RegistrationServiceAllowlist.ServiceIds.ToArray()
            : null;

    private async Task<string> DeleteAsync(
        string accessToken,
        JsonElement args,
        CancellationToken ct)
    {
        // Refactor (iter36/cluster-041-nyx-relay-command-skeleton):
        //   Old pattern: tool queried then dispatched unregister through raw runtime helper.
        //   New principle: query is only existence/confirmation; write enters command facade.
        var registrationId = GetStr(args, "registration_id") ?? GetStr(args, "id");
        if (string.IsNullOrWhiteSpace(registrationId))
            return """{"error":"'registration_id' is required for delete"}""";

        var scopeResolution = ResolveToolScopeId(args, required: true);
        if (scopeResolution.Error is not null)
            return SerializeError(scopeResolution.Error);

        // Tenant isolation: a registration outside the caller's scope must be indistinguishable
        // from a nonexistent one (single branch => byte-identical payload), so foreign
        // registration ids cannot be probed or deleted through this tool.
        var exists = await _queryPort.GetAsync(registrationId, ct);
        if (exists is null ||
            !string.Equals(exists.ScopeId, scopeResolution.ScopeId, StringComparison.Ordinal))
        {
            return JsonSerializer.Serialize(new { error = $"Registration '{registrationId}' not found" });
        }

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

        var deprovisionResult = await _deprovisioningService.DeprovisionAsync(
            accessToken,
            NyxChannelBotDeprovisioningRequest.FromRegistration(exists),
            ct);
        if (!deprovisionResult.Succeeded)
        {
            return JsonSerializer.Serialize(new
            {
                error = deprovisionResult.ConversationRouteRemoved
                    ? "nyx_agent_key_delete_failed"
                    : "nyx_conversation_route_delete_failed",
                registration_id = registrationId,
                conversation_route_id = exists.NyxConversationRouteId,
                agent_key_id = exists.NyxAgentApiKeyId,
                warnings = deprovisionResult.Warnings,
                note = "Registration-owned NyxID cleanup is incomplete; the registration and stable ownership IDs were kept so you can retry.",
            });
        }

        await _commandFacade.UnregisterAsync(registrationId, ct);

        // Refactor (iter6/cluster-014):
        //   Old pattern: Delete slept and re-read the projection to upgrade accepted into deleted.
        //   New principle: Unregister ACK is accepted-only; deletion visibility is observed by follow-up query.
        return JsonSerializer.Serialize(new
        {
            status = "accepted",
            registration_id = registrationId,
            warnings = deprovisionResult.Warnings,
            note = "Unregister accepted. Projection is propagating; try 'list' in a few seconds to confirm the registration is gone.",
        });
    }
}
