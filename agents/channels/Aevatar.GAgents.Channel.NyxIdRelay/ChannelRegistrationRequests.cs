using System.Text.Json.Serialization;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

// HTTP contracts used by OpenAPI. Handlers retain explicit JSON presence checks
// so omitting a field does not become an explicit empty value or selection.
internal sealed class ChannelRegistrationCreateRequest
{
    [JsonPropertyName("registration_id")]
    public string RegistrationId { get; init; } = string.Empty;

    [JsonPropertyName("nyx_channel_bot_id")]
    public required string NyxChannelBotId { get; init; }

    [JsonPropertyName("nyx_conversation_route_id")]
    public string NyxConversationRouteId { get; init; } = string.Empty;

    [JsonPropertyName("nyx_provider_slug")]
    public string NyxProviderSlug { get; init; } = string.Empty;

    [JsonPropertyName("skill_name")]
    public string SkillName { get; init; } = string.Empty;

    [JsonPropertyName("authorization_mode")]
    public string AuthorizationMode { get; init; } = string.Empty;

    [JsonPropertyName("service_ids")]
    public string[] ServiceIds { get; init; } = [];

    [JsonPropertyName("runtime_config")]
    public ChannelRegistrationRuntimeConfigRequest RuntimeConfig { get; init; } = new();
}

internal sealed class ChannelRegistrationUpdateRequest
{
    [JsonPropertyName("skill_name")]
    public string SkillName { get; init; } = string.Empty;

    [JsonPropertyName("authorization_mode")]
    public string AuthorizationMode { get; init; } = string.Empty;

    [JsonPropertyName("service_ids")]
    public string[] ServiceIds { get; init; } = [];

    [JsonPropertyName("runtime_config")]
    public ChannelRegistrationRuntimeConfigRequest RuntimeConfig { get; init; } = new();
}

internal sealed class ChannelRegistrationRuntimeConfigRequest
{
    [JsonPropertyName("instructions")]
    public string Instructions { get; init; } = string.Empty;

    [JsonPropertyName("default_skill")]
    public ChannelRegistrationDefaultSkillRequest DefaultSkill { get; init; } = new();

    [JsonPropertyName("tool_set_refs")]
    public string[] ToolSetRefs { get; init; } = [];

    [JsonPropertyName("extra_tool_names")]
    public string[] ExtraToolNames { get; init; } = [];

    [JsonPropertyName("nyxid_service_selectors")]
    public ChannelRegistrationServiceSelectorRequest[] NyxIdServiceSelectors { get; init; } = [];

    [JsonPropertyName("credential_source_mode")]
    public string CredentialSourceMode { get; init; } = string.Empty;
}

internal sealed class ChannelRegistrationDefaultSkillRequest
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;
}

internal sealed class ChannelRegistrationServiceSelectorRequest
{
    [JsonPropertyName("service_slug")]
    public string ServiceSlug { get; init; } = string.Empty;

    [JsonPropertyName("endpoint_names")]
    public string[] EndpointNames { get; init; } = [];
}
