using System.Text.Json;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public sealed class ChannelRegistrationServiceSelection
{
    private static readonly ChannelRegistrationServiceSelection DefaultSelection =
        new(ChannelRegistrationAuthorizationMode.NyxidDefault, []);

    private ChannelRegistrationServiceSelection(
        ChannelRegistrationAuthorizationMode authorizationMode,
        IReadOnlyList<string> serviceIds)
    {
        AuthorizationMode = authorizationMode;
        ServiceIds = serviceIds;
    }

    public ChannelRegistrationAuthorizationMode AuthorizationMode { get; }

    public IReadOnlyList<string> ServiceIds { get; }

    public static ChannelRegistrationServiceSelection NyxIdDefault => DefaultSelection;

    internal static ChannelRegistrationServiceSelection Explicit(IReadOnlyList<string> serviceIds) =>
        new(ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist, serviceIds.ToArray());
}

public static class ChannelRegistrationServiceIdsJsonParser
{
    private const string ExplicitServiceAllowlistMode = "explicit_service_allowlist";

    public static bool TryParse(
        JsonElement root,
        out ChannelRegistrationServiceSelection selection)
    {
        selection = ChannelRegistrationServiceSelection.NyxIdDefault;
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        if (!TryReadAuthorizationMode(root, out var explicitSelection))
            return false;
        if (!explicitSelection)
            return true;

        if (!root.TryGetProperty("service_ids", out var serviceIdsElement))
            return true;

        if (serviceIdsElement.ValueKind != JsonValueKind.Array)
            return false;

        var normalized = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var element in serviceIdsElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
                return false;

            var serviceId = element.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(serviceId))
                return false;

            normalized.Add(serviceId);
        }

        selection = ChannelRegistrationServiceSelection.Explicit(normalized.ToArray());
        return true;
    }

    private static bool TryReadAuthorizationMode(JsonElement root, out bool explicitSelection)
    {
        explicitSelection = false;
        if (!root.TryGetProperty("authorization_mode", out var modeElement) ||
            modeElement.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (modeElement.ValueKind != JsonValueKind.String)
            return false;

        var mode = modeElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(mode) ||
            string.Equals(mode, "nyxid_default", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(mode, ExplicitServiceAllowlistMode, StringComparison.OrdinalIgnoreCase))
        {
            explicitSelection = true;
            return true;
        }

        return false;
    }
}

public static class ChannelBotRuntimeConfigJsonParser
{
    public static bool TryParse(JsonElement root, out ChannelBotRuntimeConfig? runtimeConfig)
    {
        runtimeConfig = null;
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        if (!root.TryGetProperty("runtime_config", out var runtimeConfigElement))
            return true;

        if (runtimeConfigElement.ValueKind == JsonValueKind.Null)
            return true;

        if (runtimeConfigElement.ValueKind != JsonValueKind.Object)
            return false;

        var config = new ChannelBotRuntimeConfig();
        if (!ReadString(runtimeConfigElement, "instructions", value => config.Instructions = value))
            return false;
        if (!ReadStringArray(runtimeConfigElement, "tool_set_refs", config.ToolSetRefs))
            return false;
        if (!ReadStringArray(runtimeConfigElement, "extra_tool_names", config.ExtraToolNames))
            return false;
        if (!ReadDefaultSkill(runtimeConfigElement, config))
            return false;
        if (!ReadSelectors(runtimeConfigElement, config))
            return false;
        if (!ReadCredentialSourceMode(runtimeConfigElement, config))
            return false;
        if (!ReadAgentKeyRequirements(runtimeConfigElement, config))
            return false;

        runtimeConfig = config;
        return true;
    }

    private static bool ReadString(JsonElement root, string propertyName, Action<string> assign)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind != JsonValueKind.String)
            return false;

        assign(element.GetString()?.Trim() ?? string.Empty);
        return true;
    }

    private static bool ReadStringArray(
        JsonElement root,
        string propertyName,
        ICollection<string> values)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                return false;
            var value = item.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                values.Add(value);
        }

        return true;
    }

    private static bool ReadDefaultSkill(JsonElement root, ChannelBotRuntimeConfig config)
    {
        if (!root.TryGetProperty("default_skill", out var element) || element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        var defaultSkill = new ChannelBotRuntimeDefaultSkillConfig();
        if (!ReadString(element, "name", value => defaultSkill.Name = value))
            return false;
        if (!ReadString(element, "version", value => defaultSkill.Version = value))
            return false;
        config.DefaultSkill = defaultSkill;
        return true;
    }

    private static bool ReadSelectors(JsonElement root, ChannelBotRuntimeConfig config)
    {
        if (!root.TryGetProperty("nyxid_service_selectors", out var element) || element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                return false;

            var selector = new ChannelBotRuntimeNyxIdServiceSelector();
            if (!ReadString(item, "service_slug", value => selector.ServiceSlug = value))
                return false;
            if (!ReadStringArray(item, "endpoint_names", selector.EndpointNames))
                return false;
            if (!string.IsNullOrWhiteSpace(selector.ServiceSlug))
                config.NyxidServiceSelectors.Add(selector);
        }

        return true;
    }

    private static bool ReadCredentialSourceMode(JsonElement root, ChannelBotRuntimeConfig config)
    {
        if (!root.TryGetProperty("credential_source_mode", out var element) || element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind != JsonValueKind.String)
            return false;

        config.CredentialSourceMode = element.GetString()?.Trim() switch
        {
            "registration_agent_key" => ChannelBotRuntimeCredentialSourceMode.RegistrationAgentKey,
            "sender_binding" => ChannelBotRuntimeCredentialSourceMode.SenderBinding,
            "" or null => ChannelBotRuntimeCredentialSourceMode.Unspecified,
            _ => ChannelBotRuntimeCredentialSourceMode.Unspecified,
        };
        return true;
    }

    private static bool ReadAgentKeyRequirements(JsonElement root, ChannelBotRuntimeConfig config)
    {
        if (!root.TryGetProperty("agent_key_service_requirements", out var element) || element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        var requirements = new ChannelBotRuntimeAgentKeyServiceRequirements();
        if (!ReadStringArray(element, "allowed_service_slugs", requirements.AllowedServiceSlugs))
            return false;
        config.AgentKeyServiceRequirements = requirements;
        return true;
    }
}
