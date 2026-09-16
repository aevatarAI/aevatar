using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

internal sealed record RuntimeConfigFieldError(
    string Field,
    string Code,
    string Message);

internal static class ChannelBotRuntimeConfigValidation
{
    private const int MaxInstructionsLength = 4000;
    private const int MaxShortValueLength = 128;
    private const int MaxToolSetRefs = 32;
    private const int MaxExtraToolNames = 64;
    private const int MaxSelectors = 16;
    private const int MaxEndpointNames = 64;

    public static IReadOnlyList<RuntimeConfigFieldError> ValidateStructure(
        ChannelBotRuntimeConfig? config)
    {
        var errors = new List<RuntimeConfigFieldError>();
        if (config is null)
            return errors;

        AddLengthError(errors, "runtime_config.instructions", config.Instructions, MaxInstructionsLength);
        if (config.DefaultSkill is not null)
        {
            AddLengthError(errors, "runtime_config.default_skill.name", config.DefaultSkill.Name, MaxShortValueLength);
            AddLengthError(errors, "runtime_config.default_skill.version", config.DefaultSkill.Version, MaxShortValueLength);
            if (string.IsNullOrWhiteSpace(config.DefaultSkill.Name) &&
                !string.IsNullOrWhiteSpace(config.DefaultSkill.Version))
            {
                errors.Add(new RuntimeConfigFieldError(
                    "runtime_config.default_skill.version",
                    "default_skill_name_required",
                    "default_skill.version requires default_skill.name."));
            }
        }

        ValidateStringList(errors, "runtime_config.tool_set_refs", config.ToolSetRefs, MaxToolSetRefs, MaxShortValueLength);
        ValidateStringList(errors, "runtime_config.extra_tool_names", config.ExtraToolNames, MaxExtraToolNames, MaxShortValueLength);
        ValidateSelectors(errors, config);
        return errors;
    }

    public static IReadOnlyList<RuntimeConfigFieldError> ValidateSelectorAuthorization(
        ChannelBotRuntimeConfig? config,
        ChannelRegistrationAuthorizationMode authorizationMode,
        IReadOnlyList<string> verifiedServiceSlugs)
    {
        var errors = new List<RuntimeConfigFieldError>();
        if (config is null || config.NyxidServiceSelectors.Count == 0)
            return errors;

        if (authorizationMode != ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist)
        {
            errors.Add(new RuntimeConfigFieldError(
                "runtime_config.nyxid_service_selectors",
                "explicit_service_allowlist_required",
                "nyxid_service_selectors require explicit_service_allowlist authorization."));
            return errors;
        }

        var authorizedSelectorSlugs = verifiedServiceSlugs
            .Where(static slug => !string.IsNullOrWhiteSpace(slug))
            .ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < config.NyxidServiceSelectors.Count; index++)
        {
            var serviceSlug = config.NyxidServiceSelectors[index].ServiceSlug;
            if (authorizedSelectorSlugs.Contains(serviceSlug))
                continue;

            errors.Add(new RuntimeConfigFieldError(
                $"runtime_config.nyxid_service_selectors[{index}].service_slug",
                "service_not_authorized",
                "runtime selector service_slug must come from the verified registration service authorization."));
        }

        return errors;
    }

    private static void ValidateSelectors(
        ICollection<RuntimeConfigFieldError> errors,
        ChannelBotRuntimeConfig config)
    {
        if (config.NyxidServiceSelectors.Count > MaxSelectors)
        {
            errors.Add(new RuntimeConfigFieldError(
                "runtime_config.nyxid_service_selectors",
                "too_many_items",
                $"nyxid_service_selectors supports at most {MaxSelectors} items."));
        }

        var seenSlugs = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < config.NyxidServiceSelectors.Count; index++)
        {
            var selector = config.NyxidServiceSelectors[index];
            var field = $"runtime_config.nyxid_service_selectors[{index}].service_slug";
            AddLengthError(errors, field, selector.ServiceSlug, MaxShortValueLength);
            if (!seenSlugs.Add(selector.ServiceSlug))
            {
                errors.Add(new RuntimeConfigFieldError(
                    field,
                    "duplicate_service_slug",
                    "Each nyxid service selector must target a distinct service_slug."));
            }

            ValidateStringList(
                errors,
                $"runtime_config.nyxid_service_selectors[{index}].endpoint_names",
                selector.EndpointNames,
                MaxEndpointNames,
                MaxShortValueLength);
        }
    }

    private static void ValidateStringList(
        ICollection<RuntimeConfigFieldError> errors,
        string field,
        IReadOnlyList<string> values,
        int maxCount,
        int maxLength)
    {
        if (values.Count > maxCount)
        {
            errors.Add(new RuntimeConfigFieldError(
                field,
                "too_many_items",
                $"{field} supports at most {maxCount} items."));
        }

        var seenValues = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            AddLengthError(errors, $"{field}[{index}]", value, maxLength);
            if (!seenValues.Add(value))
            {
                errors.Add(new RuntimeConfigFieldError(
                    $"{field}[{index}]",
                    "duplicate_value",
                    "Repeated values are not allowed."));
            }
        }
    }

    private static void AddLengthError(
        ICollection<RuntimeConfigFieldError> errors,
        string field,
        string value,
        int maxLength)
    {
        if ((value?.Length ?? 0) <= maxLength)
            return;

        errors.Add(new RuntimeConfigFieldError(
            field,
            "too_long",
            $"{field} must be {maxLength} characters or fewer."));
    }
}
