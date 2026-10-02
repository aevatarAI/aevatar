using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

internal sealed record NyxIdServiceSelectorResponse(
    [property: JsonPropertyName("service_slug")] string ServiceSlug,
    [property: JsonPropertyName("endpoint_names")] IReadOnlyList<string> EndpointNames);

// HTTP boundary DTOs shared by serialization and the published OpenAPI contract.
internal sealed class ChannelCallerResponse
{
    [Description("Authenticated sender scope; must be non-empty before onboarding can continue.")]
    [JsonPropertyName("scope_id")]
    public required string ScopeId { get; init; }

    [JsonPropertyName("is_admin")]
    public required bool IsAdmin { get; init; }

    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("grant_source")]
    public required string GrantSource { get; init; }
}

internal sealed class ChannelRegistrationAcceptedResponse
{
    [Description("Accepted for dispatch only. Read back the exact registration before continuing dependent work.")]
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("registration_id")]
    public required string RegistrationId { get; init; }

    [JsonPropertyName("command_id")]
    public required string CommandId { get; init; }

    [JsonPropertyName("correlation_id")]
    public required string CorrelationId { get; init; }

    [JsonPropertyName("platform")]
    public required string Platform { get; init; }

    [JsonPropertyName("nyx_provider_slug")]
    public required string NyxProviderSlug { get; init; }

    [JsonPropertyName("nyx_channel_bot_id")]
    public required string NyxChannelBotId { get; init; }

    [JsonPropertyName("nyx_agent_api_key_id")]
    public required string NyxAgentApiKeyId { get; init; }

    [JsonPropertyName("nyx_conversation_route_id")]
    public required string NyxConversationRouteId { get; init; }

    [JsonPropertyName("relay_callback_url")]
    public required string RelayCallbackUrl { get; init; }

    [JsonPropertyName("webhook_url")]
    public required string WebhookUrl { get; init; }

    [JsonPropertyName("workflow_result_delivery_status")]
    public required string WorkflowResultDeliveryStatus { get; init; }
}

internal sealed class ChannelRegistrationUpdatedResponse
{
    [Description("Accepted for dispatch only; not yet committed or visible in the read model.")]
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("registration_id")]
    public required string RegistrationId { get; init; }

    [JsonPropertyName("command_id")]
    public required string CommandId { get; init; }

    [JsonPropertyName("correlation_id")]
    public required string CorrelationId { get; init; }

    [JsonPropertyName("skill_name")]
    public required string SkillName { get; init; }
}

internal sealed class ChannelRegistrationListResponse
{
    [Description("Aevatar registration ID; omitted for an unbound NyxID Bot. Distinct from nyx_channel_bot_id.")]
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("platform")]
    public required string Platform { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("registration_mode")]
    public required string RegistrationMode { get; init; }

    [Description("unbound or bound; bound reflects the materialized registration.")]
    [JsonPropertyName("binding_status")]
    public required string BindingStatus { get; init; }

    [JsonPropertyName("availability_status")]
    public required string AvailabilityStatus { get; init; }

    [JsonPropertyName("nyx_status")]
    public required string NyxStatus { get; init; }

    [JsonPropertyName("authorization_mode")]
    public string? AuthorizationMode { get; init; }

    [Description("Complete explicit allowlist of exact NyxID user_service_id values; omitted for other authorization modes.")]
    [JsonPropertyName("service_ids")]
    public IReadOnlyList<string>? ServiceIds { get; init; }

    [Description("Committed source version of the read model; omitted before binding.")]
    [JsonPropertyName("state_version")]
    public long? StateVersion { get; init; }

    [JsonPropertyName("nyx_provider_slug")]
    public required string NyxProviderSlug { get; init; }

    [JsonPropertyName("callback_url")]
    public required string CallbackUrl { get; init; }

    [JsonPropertyName("webhook_url")]
    public required string WebhookUrl { get; init; }

    [JsonPropertyName("nyx_channel_bot_id")]
    public required string NyxChannelBotId { get; init; }

    [JsonPropertyName("nyx_channel_bot_owner_scope_id")]
    public required string NyxChannelBotOwnerScopeId { get; init; }

    [JsonPropertyName("nyx_channel_bot_owner_scope_name")]
    public string? NyxChannelBotOwnerScopeName { get; init; }

    [JsonPropertyName("nyx_agent_api_key_id")]
    public required string NyxAgentApiKeyId { get; init; }

    [JsonPropertyName("nyx_conversation_route_id")]
    public required string NyxConversationRouteId { get; init; }

    [JsonPropertyName("skill_name")]
    public required string SkillName { get; init; }

    [JsonPropertyName("has_instructions")]
    public required bool HasInstructions { get; init; }

    [JsonPropertyName("has_tool_set_refs")]
    public required bool HasToolSetRefs { get; init; }

    [JsonPropertyName("has_extra_tool_names")]
    public required bool HasExtraToolNames { get; init; }

    [JsonPropertyName("nyxid_service_selectors")]
    public required IReadOnlyList<NyxIdServiceSelectorResponse> NyxIdServiceSelectors { get; init; }

    [JsonPropertyName("agent_key")]
    public required ChannelRegistrationAgentKeyResponse AgentKey { get; init; }

    [JsonPropertyName("workflow_result_delivery_status")]
    public required string WorkflowResultDeliveryStatus { get; init; }

    [JsonPropertyName("workflow_result_delivery_failure_phase")]
    public string? WorkflowResultDeliveryFailurePhase { get; init; }

    [JsonPropertyName("workflow_result_delivery_failure_reason")]
    public string? WorkflowResultDeliveryFailureReason { get; init; }

    [JsonPropertyName("owned")]
    public required bool Owned { get; init; }
}

internal sealed class ChannelRegistrationDetailResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("platform")]
    public required string Platform { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("registration_mode")]
    public required string RegistrationMode { get; init; }

    [JsonPropertyName("binding_status")]
    public required string BindingStatus { get; init; }

    [JsonPropertyName("authorization_mode")]
    public string? AuthorizationMode { get; init; }

    [JsonPropertyName("service_ids")]
    public IReadOnlyList<string>? ServiceIds { get; init; }

    [JsonPropertyName("runtime_config")]
    public required ChannelRegistrationRuntimeConfigResponse RuntimeConfig { get; init; }

    [JsonPropertyName("skill_name")]
    public required string SkillName { get; init; }

    [JsonPropertyName("state_version")]
    public required long StateVersion { get; init; }

    [JsonPropertyName("nyx_provider_slug")]
    public required string NyxProviderSlug { get; init; }

    [JsonPropertyName("webhook_url")]
    public required string WebhookUrl { get; init; }

    [JsonPropertyName("nyx_channel_bot_id")]
    public required string NyxChannelBotId { get; init; }

    [JsonPropertyName("nyx_channel_bot_owner_scope_id")]
    public required string NyxChannelBotOwnerScopeId { get; init; }

    [JsonPropertyName("nyx_agent_api_key_id")]
    public required string NyxAgentApiKeyId { get; init; }

    [JsonPropertyName("nyx_conversation_route_id")]
    public required string NyxConversationRouteId { get; init; }

    [JsonPropertyName("agent_key")]
    public required ChannelRegistrationAgentKeyResponse AgentKey { get; init; }

    [JsonPropertyName("workflow_result_delivery_status")]
    public required string WorkflowResultDeliveryStatus { get; init; }

    [JsonPropertyName("workflow_result_delivery_failure_phase")]
    public string? WorkflowResultDeliveryFailurePhase { get; init; }

    [JsonPropertyName("workflow_result_delivery_failure_reason")]
    public string? WorkflowResultDeliveryFailureReason { get; init; }

    [JsonPropertyName("owned")]
    public required bool Owned { get; init; }
}

internal sealed class ChannelRegistrationRuntimeConfigResponse
{
    [JsonPropertyName("instructions")]
    public required string Instructions { get; init; }

    [JsonPropertyName("tool_set_refs")]
    public required IReadOnlyList<string> ToolSetRefs { get; init; }

    [JsonPropertyName("extra_tool_names")]
    public required IReadOnlyList<string> ExtraToolNames { get; init; }

    [JsonPropertyName("nyxid_service_selectors")]
    public required IReadOnlyList<NyxIdServiceSelectorResponse> NyxIdServiceSelectors { get; init; }

    [JsonPropertyName("credential_source_mode")]
    public required string CredentialSourceMode { get; init; }
}

internal sealed class ChannelRegistrationAgentKeyResponse
{
    [JsonPropertyName("api_key_id")]
    public required string ApiKeyId { get; init; }

    [Description("Registration Agent Key readiness; does not prove the current sender's service access.")]
    [JsonPropertyName("ready")]
    public required bool Ready { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }
}

internal sealed class ChannelRegistrationStatusResponse
{
    [JsonPropertyName("registration_id")]
    public required string RegistrationId { get; init; }

    [JsonPropertyName("nyx_channel_bot_id")]
    public string? NyxChannelBotId { get; init; }

    [Description("Live Bot state, or unknown when unavailable. Does not establish the full registration binding invariant.")]
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [Description("ISO 8601 event timestamp when available.")]
    [JsonPropertyName("last_event_at")]
    public string? LastEventAt { get; init; }

    [JsonPropertyName("workflow_result_delivery_status")]
    public required string WorkflowResultDeliveryStatus { get; init; }

    [JsonPropertyName("workflow_result_delivery_failure_phase")]
    public string? WorkflowResultDeliveryFailurePhase { get; init; }

    [JsonPropertyName("workflow_result_delivery_failure_reason")]
    public string? WorkflowResultDeliveryFailureReason { get; init; }

    [Description("False for an elevated cross-account observation; otherwise omitted.")]
    [JsonPropertyName("owned")]
    public bool? Owned { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

internal sealed class ChannelRegistrationErrorResponse
{
    [JsonPropertyName("error")]
    public required string Error { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("registration_id")]
    public string? RegistrationId { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    [JsonPropertyName("field_errors")]
    public IReadOnlyList<RuntimeConfigFieldError>? FieldErrors { get; init; }
}
