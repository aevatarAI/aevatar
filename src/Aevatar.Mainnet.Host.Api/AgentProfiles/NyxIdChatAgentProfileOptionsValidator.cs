using System.Text;
using System.Text.RegularExpressions;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Microsoft.Extensions.Options;

namespace Aevatar.Mainnet.Host.Api.AgentProfiles;

public sealed class NyxIdChatAgentProfileOptionsValidator : IValidateOptions<NyxIdChatAgentProfileOptions>
{
    private const int MaximumSnapshotBytes = 65_536;
    private const int MaximumMembers = 32;
    private const int MaximumAliasesPerMember = 16;
    private const int MaximumPolicyEntries = 64;
    private const int MaximumStringBytes = 128;
    private const int MaximumRoutingDescriptionBytes = 512;
    private const int MaximumColdPreTurnMilliseconds = 2_100;
    private const string RequiredAgentKind = "nyxid.chat";

    private static readonly Regex ProfileIdPattern = new(
        "^[a-z0-9]+(?:[._-][a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex ProfileVersionPattern = new(
        "^[0-9A-Za-z]+(?:[._+-][0-9A-Za-z]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex ToolNamePattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex LiteralVersionPattern = new(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly IToolSetRegistry _toolSetRegistry;
    private readonly NyxIdChatAgentProfileValidationBaseline _validationBaseline;

    public NyxIdChatAgentProfileOptionsValidator(
        IToolSetRegistry toolSetRegistry,
        NyxIdChatAgentProfileValidationBaseline validationBaseline)
    {
        _toolSetRegistry = toolSetRegistry ?? throw new ArgumentNullException(nameof(toolSetRegistry));
        _validationBaseline = validationBaseline ?? throw new ArgumentNullException(nameof(validationBaseline));
    }

    public ValidateOptionsResult Validate(string? name, NyxIdChatAgentProfileOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        if (!string.Equals(
                options.ExternalReference,
                NyxIdChatAgentProfileOptions.StableExternalReference,
                StringComparison.Ordinal))
        {
            errors.Add($"ExternalReference must be '{NyxIdChatAgentProfileOptions.StableExternalReference}'.");
        }

        foreach (var forbiddenName in AgentProfileProductionSchemaScanner.FindForbiddenNames())
            errors.Add($"Production profile schema name '{forbiddenName}' is forbidden.");

        var profileIsPresent = options.Profile is not null;
        var requiresBaseline = options.Enabled || profileIsPresent;
        var requiredRecoveryNames = ValidateBaselineNames(
            _validationBaseline.RequiredRecoveryToolNames,
            nameof(NyxIdChatAgentProfileValidationBaseline.RequiredRecoveryToolNames),
            requiresBaseline,
            errors);
        var deniedLegacyNames = ValidateBaselineNames(
            _validationBaseline.DeniedLegacyToolNames,
            nameof(NyxIdChatAgentProfileValidationBaseline.DeniedLegacyToolNames),
            requiresBaseline,
            errors);

        if (options.Enabled && options.Profile is null)
            errors.Add("An enabled NyxID chat agent profile requires a complete Profile payload.");

        if (options.Profile is not null)
            ValidateProfile(options.Profile, requiredRecoveryNames, deniedLegacyNames, errors);

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    private void ValidateProfile(
        AgentProfileSnapshot profile,
        HashSet<string> requiredRecoveryNames,
        HashSet<string> deniedLegacyNames,
        List<string> errors)
    {
        ValidateIdentifier(profile.ProfileId, nameof(profile.ProfileId), ProfileIdPattern, errors);
        ValidateIdentifier(profile.ProfileVersion, nameof(profile.ProfileVersion), ProfileVersionPattern, errors);
        ValidateRequiredString(profile.PolicyRevision, nameof(profile.PolicyRevision), MaximumStringBytes, errors);
        if (!string.Equals(profile.AgentKind, RequiredAgentKind, StringComparison.Ordinal))
            errors.Add($"{nameof(profile.AgentKind)} must be '{RequiredAgentKind}'.");

        if (!profile.DeterministicPolicySha256.IsEmpty)
            errors.Add($"{nameof(profile.DeterministicPolicySha256)} must be empty in configuration input.");

        ValidateExactRef(profile.SkillsetProvenance, nameof(profile.SkillsetProvenance), errors);
        ValidateToolName(profile.RouteToolSetRef, nameof(profile.RouteToolSetRef), errors);
        var registeredToolSets = new HashSet<string>(_toolSetRegistry.GetRegisteredNames(), StringComparer.Ordinal);
        EnsureRegisteredToolSet(profile.RouteToolSetRef, nameof(profile.RouteToolSetRef), registeredToolSets, errors);

        var maximumPolicy = ValidatePolicy(
            profile.MaximumToolPolicy,
            nameof(profile.MaximumToolPolicy),
            registeredToolSets,
            errors);
        var recoveryPolicy = ValidatePolicy(
            profile.RecoveryToolPolicy,
            nameof(profile.RecoveryToolPolicy),
            registeredToolSets,
            errors);
        ValidateProperSubset(recoveryPolicy, maximumPolicy, nameof(profile.RecoveryToolPolicy), errors);

        if (profile.Members.Count is < 1 or > MaximumMembers)
            errors.Add($"{nameof(profile.Members)} must contain between 1 and {MaximumMembers} entries.");

        ValidateMembers(profile, maximumPolicy, registeredToolSets, errors);
        ValidateRuntimeParameters(profile, errors);

        foreach (var requiredName in requiredRecoveryNames)
        {
            if (!maximumPolicy.ToolNames.Contains(requiredName))
                errors.Add($"Required recovery tool '{requiredName}' is missing from the maximum policy.");
            if (!recoveryPolicy.ToolNames.Contains(requiredName))
                errors.Add($"Required recovery tool '{requiredName}' is missing from the recovery policy.");
        }

        foreach (var deniedName in deniedLegacyNames)
        {
            if (maximumPolicy.ToolNames.Contains(deniedName) || recoveryPolicy.ToolNames.Contains(deniedName))
                errors.Add($"Denied legacy tool '{deniedName}' appears in a profile policy.");
            if (profile.Members.Any(member => member.TaskToolPolicy?.ToolNames.Contains(deniedName, StringComparer.OrdinalIgnoreCase) == true))
                errors.Add($"Denied legacy tool '{deniedName}' appears in a member policy.");
        }

        if (profile.DeterministicPolicySha256.IsEmpty &&
            AgentProfileSnapshotCodec.Seal(profile).CalculateSize() > MaximumSnapshotBytes)
        {
            errors.Add($"The sealed profile snapshot cannot exceed {MaximumSnapshotBytes} bytes.");
        }
    }

    private static void ValidateMembers(
        AgentProfileSnapshot profile,
        PolicySet maximumPolicy,
        HashSet<string> registeredToolSets,
        List<string> errors)
    {
        var intentIds = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exactSkillRefs = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < profile.Members.Count; index++)
        {
            var member = profile.Members[index];
            var path = $"{nameof(profile.Members)}[{index}]";
            ValidateIdentifier(member.IntentId, $"{path}.{nameof(member.IntentId)}", ProfileIdPattern, errors);
            if (!intentIds.Add(member.IntentId))
                errors.Add($"{path}.{nameof(member.IntentId)} must be unique within the profile.");

            ValidateRequiredString(
                member.RoutingDescription,
                $"{path}.{nameof(member.RoutingDescription)}",
                MaximumRoutingDescriptionBytes,
                errors);
            ValidateExactRef(member.SkillRef, $"{path}.{nameof(member.SkillRef)}", errors);
            if (member.SkillRef is not null &&
                !exactSkillRefs.Add($"{member.SkillRef.Guid}\n{member.SkillRef.LiteralVersion}"))
            {
                errors.Add($"{path}.{nameof(member.SkillRef)} must be unique within the profile.");
            }

            if (member.ExplicitTriggerAliases.Count > MaximumAliasesPerMember)
                errors.Add($"{path}.{nameof(member.ExplicitTriggerAliases)} cannot exceed {MaximumAliasesPerMember} entries.");
            foreach (var alias in member.ExplicitTriggerAliases)
            {
                ValidateRequiredString(
                    alias,
                    $"{path}.{nameof(member.ExplicitTriggerAliases)}",
                    MaximumStringBytes,
                    errors);
                if (!aliases.Add(alias))
                    errors.Add($"{path}.{nameof(member.ExplicitTriggerAliases)} values must be globally unique ignoring case.");
            }

            var taskPolicy = ValidatePolicy(
                member.TaskToolPolicy,
                $"{path}.{nameof(member.TaskToolPolicy)}",
                registeredToolSets,
                errors);
            ValidateProperSubset(taskPolicy, maximumPolicy, $"{path}.{nameof(member.TaskToolPolicy)}", errors);
            if ((int)member.SideEffectClass is < 1 or > 4)
                errors.Add($"{path}.{nameof(member.SideEffectClass)} must be explicit.");
            ValidateRequiredString(
                member.ExpectedSkillName,
                $"{path}.{nameof(member.ExpectedSkillName)}",
                MaximumStringBytes,
                errors);
            ValidateRequiredString(
                member.ReviewedPublisherId,
                $"{path}.{nameof(member.ReviewedPublisherId)}",
                MaximumStringBytes,
                errors);
        }
    }

    private static void ValidateRuntimeParameters(AgentProfileSnapshot profile, List<string> errors)
    {
        if (profile.MaxPlanSteps != 4)
            errors.Add($"{nameof(profile.MaxPlanSteps)} must be 4.");
        if (profile.HandoffTtlSeconds != 900)
            errors.Add($"{nameof(profile.HandoffTtlSeconds)} must be 900.");
        if (profile.ClassifierTimeoutMs != 600)
            errors.Add($"{nameof(profile.ClassifierTimeoutMs)} must be 600.");
        if (profile.ExactSkillFetchTimeoutMs != 1_500)
            errors.Add($"{nameof(profile.ExactSkillFetchTimeoutMs)} must be 1500.");
        if (profile.MaxSelectedSkillBytes != 24_576)
            errors.Add($"{nameof(profile.MaxSelectedSkillBytes)} must be 24576.");
        if (profile.ClassifierTimeoutMs + profile.ExactSkillFetchTimeoutMs > MaximumColdPreTurnMilliseconds)
            errors.Add($"The cold pre-turn budget cannot exceed {MaximumColdPreTurnMilliseconds} ms.");
        if ((int)profile.ActivationMode is < 1 or > 2)
            errors.Add($"{nameof(profile.ActivationMode)} must be Shadow or Enforced.");
    }

    private static HashSet<string> ValidateBaselineNames(
        IReadOnlySet<string> names,
        string path,
        bool requireNonEmpty,
        List<string> errors)
    {
        if (requireNonEmpty && names.Count == 0)
            errors.Add($"{path} cannot be empty when a profile is enabled or supplied.");

        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in names)
        {
            ValidateToolName(value, path, errors);
            if (!values.Add(value))
                errors.Add($"{path} cannot contain duplicates ignoring case.");
        }

        return values;
    }

    private static PolicySet ValidatePolicy(
        AgentProfileToolPolicy? policy,
        string path,
        HashSet<string> registeredToolSets,
        List<string> errors)
    {
        if (policy is null)
        {
            errors.Add($"{path} is required.");
            return PolicySet.Empty;
        }

        if (policy.ToolNames.Count > MaximumPolicyEntries)
            errors.Add($"{path}.{nameof(policy.ToolNames)} cannot exceed {MaximumPolicyEntries} entries.");
        if (policy.ToolSetRefs.Count > MaximumPolicyEntries)
            errors.Add($"{path}.{nameof(policy.ToolSetRefs)} cannot exceed {MaximumPolicyEntries} entries.");

        var toolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var toolName in policy.ToolNames)
        {
            ValidateToolName(toolName, $"{path}.{nameof(policy.ToolNames)}", errors);
            if (!toolNames.Add(toolName))
                errors.Add($"{path}.{nameof(policy.ToolNames)} cannot contain duplicates ignoring case.");
        }

        var toolSets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var toolSetRef in policy.ToolSetRefs)
        {
            ValidateToolName(toolSetRef, $"{path}.{nameof(policy.ToolSetRefs)}", errors);
            if (!toolSets.Add(toolSetRef))
                errors.Add($"{path}.{nameof(policy.ToolSetRefs)} cannot contain duplicate values.");
            EnsureRegisteredToolSet(toolSetRef, $"{path}.{nameof(policy.ToolSetRefs)}", registeredToolSets, errors);
        }

        return new PolicySet(toolNames, toolSets);
    }

    private static void ValidateProperSubset(
        PolicySet candidate,
        PolicySet maximum,
        string path,
        List<string> errors)
    {
        if (!candidate.ToolNames.IsSubsetOf(maximum.ToolNames) ||
            !candidate.ToolSets.IsSubsetOf(maximum.ToolSets) ||
            candidate.ToolNames.SetEquals(maximum.ToolNames) && candidate.ToolSets.SetEquals(maximum.ToolSets))
        {
            errors.Add($"{path} must be a component-wise proper subset of the maximum policy.");
        }
    }

    private static void ValidateExactRef(ExactRemoteSkillRef? exactRef, string path, List<string> errors)
    {
        if (exactRef is null)
        {
            errors.Add($"{path} is required.");
            return;
        }

        ValidateExactIdentity(exactRef.Guid, exactRef.LiteralVersion, path, errors);
    }

    private static void ValidateExactRef(ExactRemoteSkillsetRef? exactRef, string path, List<string> errors)
    {
        if (exactRef is null)
        {
            errors.Add($"{path} is required.");
            return;
        }

        ValidateExactIdentity(exactRef.Guid, exactRef.LiteralVersion, path, errors);
    }

    private static void ValidateExactIdentity(string guid, string literalVersion, string path, List<string> errors)
    {
        if (!Guid.TryParseExact(guid, "D", out var parsedGuid) ||
            parsedGuid == Guid.Empty ||
            !string.Equals(guid, parsedGuid.ToString("D"), StringComparison.Ordinal))
        {
            errors.Add($"{path}.Guid must be a nonzero canonical lowercase D GUID.");
        }

        ValidateRequiredString(literalVersion, $"{path}.LiteralVersion", MaximumStringBytes, errors);
        if (!LiteralVersionPattern.IsMatch(literalVersion ?? string.Empty))
            errors.Add($"{path}.LiteralVersion must use the '<major>.<minor>' literal form.");
    }

    private static void ValidateIdentifier(
        string value,
        string path,
        Regex pattern,
        List<string> errors)
    {
        ValidateRequiredString(value, path, MaximumStringBytes, errors);
        if (!pattern.IsMatch(value ?? string.Empty))
            errors.Add($"{path} has an invalid canonical form.");
    }

    private static void ValidateToolName(string value, string path, List<string> errors)
    {
        ValidateRequiredString(value, path, MaximumStringBytes, errors);
        if (!ToolNamePattern.IsMatch(value ?? string.Empty))
            errors.Add($"{path} has an invalid tool or tool-set name.");
    }

    private static void ValidateRequiredString(
        string? value,
        string path,
        int maximumBytes,
        List<string> errors)
    {
        if (string.IsNullOrEmpty(value))
        {
            errors.Add($"{path} is required.");
            return;
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
            errors.Add($"{path} cannot contain leading or trailing whitespace.");
        if (!value.IsNormalized(NormalizationForm.FormC))
            errors.Add($"{path} must already be normalized to Unicode NFC.");
        if (Encoding.UTF8.GetByteCount(value) > maximumBytes)
            errors.Add($"{path} cannot exceed {maximumBytes} UTF-8 bytes.");
    }

    private static void EnsureRegisteredToolSet(
        string name,
        string path,
        HashSet<string> registeredToolSets,
        List<string> errors)
    {
        if (!registeredToolSets.Contains(name))
            errors.Add($"{path} references unknown tool set '{name}'.");
    }

    private sealed record PolicySet(HashSet<string> ToolNames, HashSet<string> ToolSets)
    {
        public static PolicySet Empty { get; } = new(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.Ordinal));
    }
}
