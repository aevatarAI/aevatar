using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Aevatar.GAgentService.Abstractions.AgentProfiles;

public static class AgentProfilePolicies
{
    public const string SystemOwnerHandle = "system";
    public const string NyxIdIdentityProvider = "nyxid";
    public const string AevatarPlatformId = "aevatar";

    private const int HumanReferenceSegmentMaxLength = 63;
    private const int ExpectedSkillNameMaxBytes = 64;
    private const int PublisherIdMaxBytes = 256;
    private const int IdentifierMaxBytes = 128;
    private const int DisplayNameMaxBytes = 256;
    private const int PurposeMaxBytes = 4_096;
    private const int InstructionsMaxBytes = 32_768;
    private const int SkillBindingMaxCount = 32;
    private const int ToolNameMaxCount = 128;
    private const int ToolSetRefMaxCount = 32;

    private static readonly Regex HumanReferenceSegmentPattern = new(
        "\\A[a-z0-9]+(?:-[a-z0-9]+)*\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex LiteralVersionPattern = new(
        "\\A(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static IReadOnlyList<AgentProfileSafeDiagnostic> ValidateReference(
        AgentProfileReference? reference)
    {
        if (reference is null)
            return [Diagnostic("MISSING_PROFILE_REFERENCE", "Profile reference is required.", "reference")];

        var diagnostics = new List<AgentProfileSafeDiagnostic>();
        if (!IsHumanReferenceSegment(reference.OwnerHandle))
        {
            diagnostics.Add(Diagnostic(
                "INVALID_OWNER_HANDLE",
                "Owner handle must be a canonical lowercase reference segment.",
                "owner_handle"));
        }

        if (!IsHumanReferenceSegment(reference.ProfileSlug))
        {
            diagnostics.Add(Diagnostic(
                "INVALID_PROFILE_SLUG",
                "Profile slug must be a canonical lowercase reference segment.",
                "profile_slug"));
        }

        return diagnostics;
    }

    public static IReadOnlyList<AgentProfileSafeDiagnostic> ValidateUserOwnerHandle(
        string? ownerHandle)
    {
        if (!IsHumanReferenceSegment(ownerHandle))
        {
            return
            [
                Diagnostic(
                    "INVALID_OWNER_HANDLE",
                    "Owner handle must be a canonical lowercase reference segment.",
                    "owner_handle"),
            ];
        }

        if (string.Equals(ownerHandle, SystemOwnerHandle, StringComparison.Ordinal))
        {
            return
            [
                Diagnostic(
                    "RESERVED_OWNER_HANDLE",
                    "The system owner handle is reserved.",
                    "owner_handle"),
            ];
        }

        return [];
    }

    public static IReadOnlyList<AgentProfileSafeDiagnostic> ValidateUserReference(
        AgentProfileReference? reference)
    {
        var diagnostics = ValidateReference(reference).ToList();
        if (reference is not null &&
            string.Equals(reference.OwnerHandle, SystemOwnerHandle, StringComparison.Ordinal))
        {
            diagnostics.Add(Diagnostic(
                "RESERVED_OWNER_HANDLE",
                "The system owner handle is reserved.",
                "owner_handle"));
        }

        return diagnostics;
    }

    public static IReadOnlyList<AgentProfileSafeDiagnostic> ValidateOwnerIdentity(
        AgentProfileOwnerIdentity? owner)
    {
        if (owner is null)
            return [Diagnostic("MISSING_PROFILE_OWNER", "Profile owner is required.", "owner")];

        return owner.OwnerCase switch
        {
            AgentProfileOwnerIdentity.OwnerOneofCase.User => ValidateUserOwner(owner.User),
            AgentProfileOwnerIdentity.OwnerOneofCase.System => ValidateSystemOwner(owner.System),
            _ => [Diagnostic("INVALID_PROFILE_OWNER", "A typed Profile owner is required.", "owner")],
        };
    }

    public static IReadOnlyList<AgentProfileSafeDiagnostic> ValidateIdentity(
        AgentProfileIdentity? identity)
    {
        if (identity is null)
            return [Diagnostic("MISSING_PROFILE_IDENTITY", "Profile identity is required.", "identity")];

        var diagnostics = new List<AgentProfileSafeDiagnostic>();
        if (string.IsNullOrWhiteSpace(identity.ProfileId) || HasBoundaryWhitespace(identity.ProfileId))
        {
            diagnostics.Add(Diagnostic(
                "INVALID_PROFILE_ID",
                "Opaque Profile id is required.",
                "profile_id"));
        }

        diagnostics.AddRange(ValidateOwnerIdentity(identity.Owner));
        diagnostics.AddRange(ValidateReference(identity.Reference));

        if (identity.Owner?.OwnerCase == AgentProfileOwnerIdentity.OwnerOneofCase.User)
        {
            if (string.IsNullOrWhiteSpace(identity.OwningScopeId) || HasBoundaryWhitespace(identity.OwningScopeId))
            {
                diagnostics.Add(Diagnostic(
                    "INVALID_OWNING_SCOPE_ID",
                    "User Profiles require an owning scope.",
                    "owning_scope_id"));
            }

            if (string.Equals(identity.Reference?.OwnerHandle, SystemOwnerHandle, StringComparison.Ordinal))
            {
                diagnostics.Add(Diagnostic(
                    "RESERVED_OWNER_HANDLE",
                    "The system owner handle is reserved.",
                    "reference.owner_handle"));
            }
        }
        else if (identity.Owner?.OwnerCase == AgentProfileOwnerIdentity.OwnerOneofCase.System)
        {
            if (!string.IsNullOrEmpty(identity.OwningScopeId))
            {
                diagnostics.Add(Diagnostic(
                    "SYSTEM_PROFILE_SCOPE_FORBIDDEN",
                    "System Profiles do not have an owning scope.",
                    "owning_scope_id"));
            }

            if (!string.Equals(identity.Reference?.OwnerHandle, SystemOwnerHandle, StringComparison.Ordinal))
            {
                diagnostics.Add(Diagnostic(
                    "INVALID_SYSTEM_PROFILE_REFERENCE",
                    "System Profiles use the reserved system owner handle.",
                    "reference.owner_handle"));
            }
        }

        return diagnostics;
    }

    public static IReadOnlyList<AgentProfileSafeDiagnostic> ValidateExactSkillReference(
        ExactOrnnSkillReference? reference)
    {
        if (reference is null)
            return [Diagnostic("MISSING_SKILL_REFERENCE", "Exact skill reference is required.", "skill")];

        var diagnostics = new List<AgentProfileSafeDiagnostic>();
        if (!Guid.TryParseExact(reference.SkillGuid, "D", out var guid) ||
            !string.Equals(reference.SkillGuid, guid.ToString("D"), StringComparison.Ordinal))
        {
            diagnostics.Add(Diagnostic(
                "INVALID_SKILL_GUID",
                "Skill GUID must use canonical lowercase D format.",
                "skill_guid"));
        }

        if (!LiteralVersionPattern.IsMatch(reference.LiteralVersion ?? string.Empty))
        {
            diagnostics.Add(Diagnostic(
                "INVALID_LITERAL_VERSION",
                "Literal version must use canonical major.minor form.",
                "literal_version"));
        }

        if (!IsCanonicalName(reference.ExpectedName, ExpectedSkillNameMaxBytes))
        {
            diagnostics.Add(Diagnostic(
                "INVALID_EXPECTED_SKILL_NAME",
                "Expected skill name must be canonical and lowercase.",
                "expected_name"));
        }

        if (!IsBoundedOpaqueIdentifier(reference.ExpectedPublisherId, PublisherIdMaxBytes))
        {
            diagnostics.Add(Diagnostic(
                "INVALID_EXPECTED_PUBLISHER_ID",
                "Expected publisher id is required and must be bounded.",
                "expected_publisher_id"));
        }

        return diagnostics;
    }

    public static IReadOnlyList<AgentProfileSafeDiagnostic> ValidateBindingId(string? bindingId)
    {
        if (IsBoundedOpaqueIdentifier(bindingId, IdentifierMaxBytes))
            return [];

        return
        [
            Diagnostic(
                "INVALID_BINDING_ID",
                "Binding id is required and must be bounded.",
                "binding_id"),
        ];
    }

    public static IReadOnlyList<AgentProfileSafeDiagnostic> ValidateSealedSkill(
        SealedAgentProfileSkill? skill)
    {
        var diagnostics = ValidateSealedSkillIdentity(skill).ToList();
        if (skill is null || diagnostics.Count > 0)
            return diagnostics;

        if (skill.ContentSha256.Length != SHA256.HashSizeInBytes)
        {
            diagnostics.Add(Diagnostic(
                "SEALED_SKILL_CONTENT_SHA256_MISMATCH",
                "Sealed skill content digest is invalid.",
                "content_sha256"));
            return diagnostics;
        }

        try
        {
            var expected = AgentProfileDeterminism.ComputeSealedSkillSha256(skill);
            if (!CryptographicOperations.FixedTimeEquals(expected.Span, skill.ContentSha256.Span))
            {
                diagnostics.Add(Diagnostic(
                    "SEALED_SKILL_CONTENT_SHA256_MISMATCH",
                    "Sealed skill content digest is invalid.",
                    "content_sha256"));
            }
        }
        catch (AgentProfileContractValidationException exception)
        {
            diagnostics.AddRange(exception.Diagnostics.Select(static diagnostic => diagnostic.Clone()));
        }

        return diagnostics;
    }

    internal static IReadOnlyList<AgentProfileSafeDiagnostic> ValidateSealedSkillIdentity(
        SealedAgentProfileSkill? skill)
    {
        if (skill is null)
            return [Diagnostic("MISSING_SEALED_SKILL", "Sealed skill is required.", "skill")];

        var diagnostics = new List<AgentProfileSafeDiagnostic>();
        diagnostics.AddRange(ValidateExactSkillReference(skill.ExactReference));
        if (skill.Package is null)
        {
            diagnostics.Add(Diagnostic(
                "MISSING_RESOLVED_SKILL_PACKAGE",
                "Sealed skill package is required.",
                "package"));
            return diagnostics;
        }

        diagnostics.AddRange(ValidateExactSkillReference(new ExactOrnnSkillReference
        {
            SkillGuid = skill.Package.SkillGuid,
            LiteralVersion = skill.Package.LiteralVersion,
            ExpectedName = skill.Package.CanonicalName,
            ExpectedPublisherId = skill.Package.PublisherId,
        }).Select(static diagnostic => PrefixPath(diagnostic, "package")));
        if (string.IsNullOrWhiteSpace(skill.Package.UpstreamSkillHash) ||
            HasBoundaryWhitespace(skill.Package.UpstreamSkillHash))
        {
            diagnostics.Add(Diagnostic(
                "MISSING_UPSTREAM_SKILL_HASH",
                "Resolved skill package requires an upstream content hash.",
                "package.upstream_skill_hash"));
        }

        if (skill.ExactReference is null)
            return diagnostics;

        AddOrdinalMismatch(
            diagnostics,
            skill.ExactReference.SkillGuid,
            skill.Package.SkillGuid,
            "SEALED_SKILL_GUID_MISMATCH",
            "package.skill_guid");
        AddOrdinalMismatch(
            diagnostics,
            skill.ExactReference.LiteralVersion,
            skill.Package.LiteralVersion,
            "SEALED_SKILL_LITERAL_VERSION_MISMATCH",
            "package.literal_version");
        AddOrdinalMismatch(
            diagnostics,
            skill.ExactReference.ExpectedName,
            skill.Package.CanonicalName,
            "SEALED_SKILL_CANONICAL_NAME_MISMATCH",
            "package.canonical_name");
        AddOrdinalMismatch(
            diagnostics,
            skill.ExactReference.ExpectedPublisherId,
            skill.Package.PublisherId,
            "SEALED_SKILL_PUBLISHER_ID_MISMATCH",
            "package.publisher_id");
        return diagnostics;
    }

    public static IReadOnlyList<AgentProfileSafeDiagnostic> ValidateContent(
        AgentProfileContent? content)
    {
        if (content is null)
            return [Diagnostic("MISSING_PROFILE_CONTENT", "Profile content is required.", "content")];

        var diagnostics = new List<AgentProfileSafeDiagnostic>();
        ValidateAuthoredText(
            diagnostics,
            content.DisplayName,
            "display_name",
            "INVALID_DISPLAY_NAME",
            DisplayNameMaxBytes,
            required: true);
        ValidateAuthoredText(
            diagnostics,
            content.Purpose,
            "purpose",
            "INVALID_PURPOSE",
            PurposeMaxBytes,
            required: false);
        ValidateAuthoredText(
            diagnostics,
            content.Instructions,
            "instructions",
            "INVALID_INSTRUCTIONS",
            InstructionsMaxBytes,
            required: false);

        diagnostics.AddRange(ValidateToolPolicy(content.ToolPolicy));

        if (content.SkillBindings.Count > SkillBindingMaxCount)
        {
            diagnostics.Add(Diagnostic(
                "TOO_MANY_SKILL_BINDINGS",
                "Profile skill binding count exceeds the limit.",
                "skill_bindings"));
        }

        var bindingIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < content.SkillBindings.Count; index++)
        {
            var binding = content.SkillBindings[index];
            var path = $"skill_bindings[{index}]";
            if (ValidateBindingId(binding.BindingId).Count > 0)
            {
                diagnostics.Add(Diagnostic(
                    "INVALID_BINDING_ID",
                    "Binding id is required and must be bounded.",
                    $"{path}.binding_id"));
            }
            else if (!bindingIds.Add(binding.BindingId))
            {
                diagnostics.Add(Diagnostic(
                    "DUPLICATE_BINDING_ID",
                    "Binding ids must be unique.",
                    $"{path}.binding_id"));
            }

            if (binding.ActivationMode == AgentProfileSkillActivationMode.Unspecified)
            {
                diagnostics.Add(Diagnostic(
                    "INVALID_SKILL_ACTIVATION_MODE",
                    "Skill activation mode must be specified.",
                    $"{path}.activation_mode"));
            }

            diagnostics.AddRange(ValidateExactSkillReference(binding.Skill)
                .Select(diagnostic => PrefixPath(diagnostic, path)));
        }

        return diagnostics;
    }

    public static IReadOnlyList<AgentProfileSafeDiagnostic> ValidatePublishedSnapshot(
        AgentProfilePublishedSnapshot? snapshot)
    {
        if (snapshot is null)
            return [Diagnostic("MISSING_PUBLISHED_SNAPSHOT", "Published snapshot is required.", "snapshot")];

        var diagnostics = new List<AgentProfileSafeDiagnostic>();
        diagnostics.AddRange(ValidateIdentity(snapshot.Identity));
        ValidateAuthoredText(
            diagnostics,
            snapshot.DisplayName,
            "display_name",
            "INVALID_DISPLAY_NAME",
            DisplayNameMaxBytes,
            required: true);
        ValidateAuthoredText(
            diagnostics,
            snapshot.Purpose,
            "purpose",
            "INVALID_PURPOSE",
            PurposeMaxBytes,
            required: false);
        ValidateAuthoredText(
            diagnostics,
            snapshot.Instructions,
            "instructions",
            "INVALID_INSTRUCTIONS",
            InstructionsMaxBytes,
            required: false);
        diagnostics.AddRange(ValidateToolPolicy(snapshot.ToolPolicy));

        var bindingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in snapshot.SkillBindings)
        {
            if (!IsBoundedOpaqueIdentifier(binding.BindingId, IdentifierMaxBytes))
            {
                diagnostics.Add(Diagnostic(
                    "INVALID_BINDING_ID",
                    "Binding id is required and must be bounded.",
                    "skill_bindings.binding_id"));
            }
            else if (!bindingIds.Add(binding.BindingId))
            {
                diagnostics.Add(Diagnostic(
                    "DUPLICATE_BINDING_ID",
                    "Binding ids must be unique.",
                    "skill_bindings.binding_id"));
            }

            if (binding.ActivationMode == AgentProfileSkillActivationMode.Unspecified)
            {
                diagnostics.Add(Diagnostic(
                    "INVALID_SKILL_ACTIVATION_MODE",
                    "Skill activation mode must be specified.",
                    "skill_bindings.activation_mode"));
            }

            diagnostics.AddRange(ValidateSealedSkill(binding.Skill)
                .Select(static diagnostic => PrefixPath(diagnostic, "skill_bindings.skill")));
        }

        return diagnostics;
    }

    private static IReadOnlyList<AgentProfileSafeDiagnostic> ValidateUserOwner(
        AgentProfileUserOwnerIdentity? owner)
    {
        var diagnostics = new List<AgentProfileSafeDiagnostic>();
        if (owner is null ||
            !string.Equals(owner.IdentityProvider, NyxIdIdentityProvider, StringComparison.Ordinal))
        {
            diagnostics.Add(Diagnostic(
                "INVALID_IDENTITY_PROVIDER",
                "User Profile owner identity provider must be nyxid.",
                "owner.user.identity_provider"));
        }

        if (owner is null || !IsBoundedOpaqueIdentifier(owner.SubjectId, PublisherIdMaxBytes))
        {
            diagnostics.Add(Diagnostic(
                "INVALID_OWNER_SUBJECT_ID",
                "User Profile owner subject id is required and must be bounded.",
                "owner.user.subject_id"));
        }

        return diagnostics;
    }

    private static IReadOnlyList<AgentProfileSafeDiagnostic> ValidateSystemOwner(
        AgentProfileSystemOwnerIdentity? owner)
    {
        if (owner is not null &&
            string.Equals(owner.PlatformId, AevatarPlatformId, StringComparison.Ordinal))
        {
            return [];
        }

        return
        [
            Diagnostic(
                "INVALID_SYSTEM_PLATFORM_ID",
                "System Profile owner platform must be aevatar.",
                "owner.system.platform_id"),
        ];
    }

    private static IReadOnlyList<AgentProfileSafeDiagnostic> ValidateToolPolicy(
        AgentProfileToolPolicy? policy)
    {
        if (policy is null)
            return [Diagnostic("MISSING_TOOL_POLICY", "Profile tool policy is required.", "tool_policy")];

        var diagnostics = new List<AgentProfileSafeDiagnostic>();
        if (policy.Mode == AgentProfileToolPolicyMode.Unspecified)
        {
            diagnostics.Add(Diagnostic(
                "INVALID_TOOL_POLICY_MODE",
                "Profile tool policy mode must be specified.",
                "tool_policy.mode"));
        }

        if (policy.ToolNames.Count > ToolNameMaxCount)
        {
            diagnostics.Add(Diagnostic(
                "TOO_MANY_TOOL_NAMES",
                "Tool name count exceeds the limit.",
                "tool_policy.tool_names"));
        }

        if (policy.ToolSetRefs.Count > ToolSetRefMaxCount)
        {
            diagnostics.Add(Diagnostic(
                "TOO_MANY_TOOL_SET_REFS",
                "Tool-set reference count exceeds the limit.",
                "tool_policy.tool_set_refs"));
        }

        for (var index = 0; index < policy.ToolNames.Count; index++)
        {
            if (!IsBoundedOpaqueIdentifier(policy.ToolNames[index], IdentifierMaxBytes))
            {
                diagnostics.Add(Diagnostic(
                    "INVALID_TOOL_NAME",
                    "Tool name is required and must be bounded.",
                    $"tool_policy.tool_names[{index}]"));
            }
        }

        for (var index = 0; index < policy.ToolSetRefs.Count; index++)
        {
            if (!IsBoundedOpaqueIdentifier(policy.ToolSetRefs[index], IdentifierMaxBytes))
            {
                diagnostics.Add(Diagnostic(
                    "INVALID_TOOL_SET_REF",
                    "Tool-set reference is required and must be bounded.",
                    $"tool_policy.tool_set_refs[{index}]"));
            }
        }

        return diagnostics;
    }

    private static bool IsHumanReferenceSegment(string? value) =>
        value is { Length: >= 1 and <= HumanReferenceSegmentMaxLength } &&
        HumanReferenceSegmentPattern.IsMatch(value);

    private static bool IsCanonicalName(string? value, int maxBytes) =>
        value is not null &&
        Encoding.UTF8.GetByteCount(value) <= maxBytes &&
        HumanReferenceSegmentPattern.IsMatch(value);

    private static bool IsBoundedOpaqueIdentifier(string? value, int maxBytes) =>
        !string.IsNullOrWhiteSpace(value) &&
        !HasBoundaryWhitespace(value) &&
        !value.Any(char.IsControl) &&
        Encoding.UTF8.GetByteCount(value) <= maxBytes;

    private static bool HasBoundaryWhitespace(string value) =>
        value.Length > 0 &&
        (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]));

    private static void ValidateAuthoredText(
        ICollection<AgentProfileSafeDiagnostic> diagnostics,
        string? value,
        string path,
        string code,
        int maxBytes,
        bool required)
    {
        if ((required && string.IsNullOrWhiteSpace(value)) ||
            value is null ||
            Encoding.UTF8.GetByteCount(value) > maxBytes ||
            value.Any(static character => character == '\0'))
        {
            diagnostics.Add(Diagnostic(code, "Authored text violates its field contract.", path));
        }
    }

    private static AgentProfileSafeDiagnostic PrefixPath(
        AgentProfileSafeDiagnostic diagnostic,
        string prefix) =>
        new()
        {
            Code = diagnostic.Code,
            Message = diagnostic.Message,
            Path = string.IsNullOrEmpty(diagnostic.Path)
                ? prefix
                : $"{prefix}.{diagnostic.Path}",
        };

    private static void AddOrdinalMismatch(
        ICollection<AgentProfileSafeDiagnostic> diagnostics,
        string expected,
        string actual,
        string code,
        string path)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            diagnostics.Add(Diagnostic(code, "Sealed skill identity does not match its exact reference.", path));
    }

    private static AgentProfileSafeDiagnostic Diagnostic(
        string code,
        string message,
        string path) =>
        new()
        {
            Code = code,
            Message = message,
            Path = path,
        };
}
