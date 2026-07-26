using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Aevatar.AI.Abstractions.Prompting;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.GAgentService.Abstractions.AgentProfiles;

public static class AgentProfilePolicies
{
    public const string SystemOwnerHandle = "system";
    public const string NyxIdIdentityProvider = "nyxid";
    public const string AevatarPlatformId = "aevatar";

    private const int HumanReferenceSegmentMaxLength = 63;
    private const int ExpectedSkillNameMaxBytes = AgentProfileValidationLimits.ExpectedOrnnNameMaxUtf8Bytes;
    private const int PublisherIdMaxBytes = AgentProfileValidationLimits.PublisherIdMaxUtf8Bytes;
    private const int IdentifierMaxBytes = AgentProfileValidationLimits.IdentifierMaxUtf8Bytes;
    private const int DisplayNameMaxBytes = AgentProfileValidationLimits.DisplayNameMaxUtf8Bytes;
    private const int PurposeMaxBytes = AgentProfileValidationLimits.PurposeMaxUtf8Bytes;
    private const int InstructionsMaxBytes = AgentProfileValidationLimits.ProfileInstructionsMaxUtf8Bytes;
    private const int SkillBindingMaxCount = AgentProfileValidationLimits.SkillBindingMaxCount;
    private const int ToolNameMaxCount = AgentProfileValidationLimits.ExplicitToolNameMaxCount;
    private const int ToolSetRefMaxCount = AgentProfileValidationLimits.ToolSetRefMaxCount;
    private const int RoutingDescriptionMaxBytes = 512;
    private const int ExplicitTriggerAliasMaxCount = 16;

    private static readonly Regex HumanReferenceSegmentPattern = new(
        "\\A[a-z0-9]+(?:-[a-z0-9]+)*\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex LiteralVersionPattern = new(
        "\\A(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex RoutingIntentIdPattern = new(
        "\\A[a-z0-9]+(?:[._-][a-z0-9]+)*\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static AgentProfileSafeDiagnostic NormalizeDiagnostic(
        AgentProfileSafeDiagnostic? diagnostic) =>
        new()
        {
            Code = BoundDiagnosticField(diagnostic?.Code),
            Message = BoundDiagnosticField(diagnostic?.Message),
            Path = BoundDiagnosticField(diagnostic?.Path),
        };

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

    public static IReadOnlyList<AgentProfileSafeDiagnostic> ValidateUserOwningScopeId(
        string? owningScopeId)
    {
        if (string.IsNullOrWhiteSpace(owningScopeId) || HasBoundaryWhitespace(owningScopeId))
        {
            return
            [
                Diagnostic(
                    "INVALID_OWNING_SCOPE_ID",
                    "User Profiles require an owning scope.",
                    "owning_scope_id"),
            ];
        }

        if (string.Equals(
                owningScopeId,
                PlatformScopeSemantics.ReservedPlatformScopeId,
                StringComparison.Ordinal))
        {
            return
            [
                Diagnostic(
                    "RESERVED_OWNING_SCOPE_ID",
                    "The platform scope is reserved for platform-owned and quarantined facts.",
                    "owning_scope_id"),
            ];
        }

        return [];
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
            diagnostics.AddRange(ValidateUserOwningScopeId(identity.OwningScopeId));

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
        if (content.RecoveryToolPolicy is not null)
        {
            diagnostics.AddRange(ValidateToolPolicy(
                content.RecoveryToolPolicy,
                "recovery_tool_policy"));
            ValidateNarrowingToolPolicy(
                content.RecoveryToolPolicy,
                content.ToolPolicy,
                "recovery_tool_policy",
                "RECOVERY_TOOL_POLICY_EXCEEDS_PROFILE_MAXIMUM",
                diagnostics);
        }

        if (content.SkillBindings.Count > SkillBindingMaxCount)
        {
            diagnostics.Add(Diagnostic(
                "TOO_MANY_SKILL_BINDINGS",
                "Profile skill binding count exceeds the limit.",
                "skill_bindings"));
        }

        var bindingIds = new HashSet<string>(StringComparer.Ordinal);
        var intentIds = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            else if (!bindingIds.Add(NormalizeBindingIdentity(binding.BindingId)))
            {
                diagnostics.Add(Diagnostic(
                    "DUPLICATE_BINDING_ID",
                    "Binding ids must be unique.",
                    $"{path}.binding_id"));
            }

            if (!IsSupportedSkillActivationMode(binding.ActivationMode))
            {
                diagnostics.Add(Diagnostic(
                    "INVALID_SKILL_ACTIVATION_MODE",
                    "Skill activation mode must be supported.",
                    $"{path}.activation_mode"));
            }

            ValidateRoutingPolicyForActivation(
                binding.ActivationMode,
                binding.RoutingPolicy,
                content.ToolPolicy,
                path,
                intentIds,
                aliases,
                diagnostics);

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
        if (snapshot.RecoveryToolPolicy is not null)
        {
            diagnostics.AddRange(ValidateToolPolicy(
                snapshot.RecoveryToolPolicy,
                "recovery_tool_policy"));
            ValidateNarrowingToolPolicy(
                snapshot.RecoveryToolPolicy,
                snapshot.ToolPolicy,
                "recovery_tool_policy",
                "RECOVERY_TOOL_POLICY_EXCEEDS_PROFILE_MAXIMUM",
                diagnostics);
        }

        var bindingIds = new HashSet<string>(StringComparer.Ordinal);
        var intentIds = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in snapshot.SkillBindings)
        {
            if (!IsBoundedOpaqueIdentifier(binding.BindingId, IdentifierMaxBytes))
            {
                diagnostics.Add(Diagnostic(
                    "INVALID_BINDING_ID",
                    "Binding id is required and must be bounded.",
                    "skill_bindings.binding_id"));
            }
            else if (!bindingIds.Add(NormalizeBindingIdentity(binding.BindingId)))
            {
                diagnostics.Add(Diagnostic(
                    "DUPLICATE_BINDING_ID",
                    "Binding ids must be unique.",
                    "skill_bindings.binding_id"));
            }

            if (!IsSupportedSkillActivationMode(binding.ActivationMode))
            {
                diagnostics.Add(Diagnostic(
                    "INVALID_SKILL_ACTIVATION_MODE",
                    "Skill activation mode must be supported.",
                    "skill_bindings.activation_mode"));
            }


            ValidateRoutingPolicyForActivation(
                binding.ActivationMode,
                binding.RoutingPolicy,
                snapshot.ToolPolicy,
                "skill_bindings",
                intentIds,
                aliases,
                diagnostics);

            diagnostics.AddRange(ValidateSealedSkill(binding.Skill)
                .Select(static diagnostic => PrefixPath(diagnostic, "skill_bindings.skill")));
        }

        return diagnostics;
    }

    public static IReadOnlyList<AgentProfileSafeDiagnostic> ValidatePublishedSummary(
        AgentProfilePublishedSummary? summary)
    {
        if (summary is null)
            return [Diagnostic("MISSING_PUBLISHED_SUMMARY", "Published summary is required.", "summary")];

        var diagnostics = ValidateReference(summary.Reference)
            .Select(static diagnostic => PrefixPath(diagnostic, "reference"))
            .ToList();
        ValidateAuthoredText(
            diagnostics,
            summary.DisplayName,
            "display_name",
            "INVALID_DISPLAY_NAME",
            DisplayNameMaxBytes,
            required: true);
        ValidateAuthoredText(
            diagnostics,
            summary.Purpose,
            "purpose",
            "INVALID_PURPOSE",
            PurposeMaxBytes,
            required: false);
        if (summary.SnapshotSha256.Length != 32)
        {
            diagnostics.Add(Diagnostic(
                "INVALID_PUBLISHED_SNAPSHOT_SHA256",
                "Published summary snapshot digest must be SHA-256.",
                "snapshot_sha256"));
        }

        return diagnostics;
    }

    public static IReadOnlyList<AgentProfileSafeDiagnostic> ValidatePublishedSnapshotHardLimits(
        AgentProfilePublishedSnapshot? snapshot)
    {
        if (snapshot is null)
            return [Diagnostic("MISSING_PUBLISHED_SNAPSHOT", "Published snapshot is required.", "snapshot")];

        var diagnostics = new List<AgentProfileSafeDiagnostic>();
        long aggregatePromptBytes = Encoding.UTF8.GetByteCount(snapshot.Instructions ?? string.Empty);
        var alwaysSkillProcedures = new List<string>();
        foreach (var binding in snapshot.SkillBindings)
        {
            var skill = binding.Skill;
            var package = skill?.Package;
            if (package is not null)
            {
                aggregatePromptBytes += PromptByteCount(package);
                if (binding.ActivationMode == AgentProfileSkillActivationMode.Always)
                    alwaysSkillProcedures.Add(package.Instructions);
                foreach (var (path, content) in EnumerateTextAssets(package))
                {
                    if (Encoding.UTF8.GetByteCount(content) <=
                        AgentProfileValidationLimits.TextAssetMaxUtf8Bytes)
                    {
                        continue;
                    }

                    AddHardLimitDiagnostic(
                        diagnostics,
                        "TEXT_ASSET_TOO_LARGE",
                        "Sealed skill text asset exceeds the UTF-8 byte limit.",
                        $"skill_bindings.{binding.BindingId}.skill.package.{path}");
                }
            }

            if (skill is not null &&
                skill.CalculateSize() > AgentProfileValidationLimits.SealedSkillMaxSerializedBytes)
            {
                AddHardLimitDiagnostic(
                    diagnostics,
                    "SEALED_SKILL_TOO_LARGE",
                    "Sealed skill serialized size exceeds the limit.",
                    $"skill_bindings.{binding.BindingId}.skill");
            }
        }

        if (aggregatePromptBytes >
            AgentProfileValidationLimits.RawAuthoritativeAggregateContentMaxUtf8Bytes)
        {
            AddHardLimitDiagnostic(
                diagnostics,
                "AGGREGATE_PROMPT_BYTES_EXCEEDED",
                "Aggregate Profile prompt bytes exceed the limit.",
                "skill_bindings");
        }
        if (aggregatePromptBytes >
            AgentProfileValidationLimits.RawAuthoritativeAggregateContentMaxEstimatedTokens)
        {
            AddHardLimitDiagnostic(
                diagnostics,
                "AGGREGATE_PROMPT_TOKENS_EXCEEDED",
                "Aggregate Profile prompt token upper bound exceeds the limit.",
                "skill_bindings");
        }
        var profileLayer = ProfilePromptLayerRenderer.Render(
            snapshot.Instructions,
            alwaysSkillProcedures);
        if (profileLayer.ActualUtf8Bytes >
            AgentProfileValidationLimits.MaterializedProfileLayerMaxUtf8Bytes)
        {
            AddHardLimitDiagnostic(
                diagnostics,
                "MATERIALIZED_PROFILE_LAYER_BYTES_EXCEEDED",
                "Materialized Profile layer bytes exceed the limit.",
                "skill_bindings");
        }
        if (profileLayer.EstimatedTokens >
            AgentProfileValidationLimits.MaterializedProfileLayerMaxEstimatedTokens)
        {
            AddHardLimitDiagnostic(
                diagnostics,
                "MATERIALIZED_PROFILE_LAYER_TOKENS_EXCEEDED",
                "Materialized Profile layer estimated tokens exceed the limit.",
                "skill_bindings");
        }
        if (snapshot.CalculateSize() > AgentProfileValidationLimits.PublishedSnapshotMaxSerializedBytes)
        {
            AddHardLimitDiagnostic(
                diagnostics,
                "PUBLISHED_SNAPSHOT_TOO_LARGE",
                "Published Profile snapshot serialized size exceeds the limit.",
                "snapshot");
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
        AgentProfileToolPolicy? policy,
        string path = "tool_policy")
    {
        if (policy is null)
            return [Diagnostic("MISSING_TOOL_POLICY", "Profile tool policy is required.", path)];

        var diagnostics = new List<AgentProfileSafeDiagnostic>();
        if (!IsSupportedToolPolicyMode(policy.Mode))
        {
            diagnostics.Add(Diagnostic(
                "INVALID_TOOL_POLICY_MODE",
                "Profile tool policy mode must be supported.",
                $"{path}.mode"));
        }

        if (policy.ToolNames.Count > ToolNameMaxCount)
        {
            diagnostics.Add(Diagnostic(
                "TOO_MANY_TOOL_NAMES",
                "Tool name count exceeds the limit.",
                $"{path}.tool_names"));
        }

        if (policy.ToolSetRefs.Count > ToolSetRefMaxCount)
        {
            diagnostics.Add(Diagnostic(
                "TOO_MANY_TOOL_SET_REFS",
                "Tool-set reference count exceeds the limit.",
                $"{path}.tool_set_refs"));
        }

        for (var index = 0; index < policy.ToolNames.Count; index++)
        {
            if (!IsBoundedOpaqueIdentifier(policy.ToolNames[index], IdentifierMaxBytes))
            {
                diagnostics.Add(Diagnostic(
                    "INVALID_TOOL_NAME",
                    "Tool name is required and must be bounded.",
                    $"{path}.tool_names[{index}]"));
            }
        }

        for (var index = 0; index < policy.ToolSetRefs.Count; index++)
        {
            if (!IsBoundedOpaqueIdentifier(policy.ToolSetRefs[index], IdentifierMaxBytes))
            {
                diagnostics.Add(Diagnostic(
                    "INVALID_TOOL_SET_REF",
                    "Tool-set reference is required and must be bounded.",
                    $"{path}.tool_set_refs[{index}]"));
            }
        }

        return diagnostics;
    }

    private static void ValidateRoutingPolicyForActivation(
        AgentProfileSkillActivationMode activationMode,
        AgentProfileSkillRoutingPolicy? routingPolicy,
        AgentProfileToolPolicy? maximumPolicy,
        string bindingPath,
        HashSet<string> intentIds,
        HashSet<string> aliases,
        List<AgentProfileSafeDiagnostic> diagnostics)
    {
        if (activationMode == AgentProfileSkillActivationMode.Always)
        {
            if (routingPolicy is not null)
            {
                diagnostics.Add(Diagnostic(
                    "UNEXPECTED_SKILL_ROUTING_POLICY",
                    "Always-active skill bindings cannot carry routing policy.",
                    $"{bindingPath}.routing_policy"));
            }
            return;
        }

        if (activationMode is not (
                AgentProfileSkillActivationMode.Routed or
                AgentProfileSkillActivationMode.DefaultForUnmatchedTurn))
        {
            return;
        }

        if (routingPolicy is null)
        {
            diagnostics.Add(Diagnostic(
                "MISSING_SKILL_ROUTING_POLICY",
                "Routed skill bindings require routing policy.",
                $"{bindingPath}.routing_policy"));
            return;
        }

        var path = $"{bindingPath}.routing_policy";
        var normalizedIntentId = (routingPolicy.IntentId ?? string.Empty)
            .Normalize(NormalizationForm.FormC);
        if (Encoding.UTF8.GetByteCount(normalizedIntentId) > IdentifierMaxBytes ||
            !RoutingIntentIdPattern.IsMatch(normalizedIntentId))
        {
            diagnostics.Add(Diagnostic(
                "INVALID_ROUTING_INTENT_ID",
                "Routing intent id must use canonical lowercase identifier form.",
                $"{path}.intent_id"));
        }
        else if (!intentIds.Add(normalizedIntentId))
        {
            diagnostics.Add(Diagnostic(
                "DUPLICATE_ROUTING_INTENT_ID",
                "Routing intent ids must be unique.",
                $"{path}.intent_id"));
        }

        ValidateAuthoredText(
            diagnostics,
            routingPolicy.RoutingDescription,
            $"{path}.routing_description",
            "INVALID_ROUTING_DESCRIPTION",
            RoutingDescriptionMaxBytes,
            required: true);

        if (routingPolicy.ExplicitTriggerAliases.Count > ExplicitTriggerAliasMaxCount)
        {
            diagnostics.Add(Diagnostic(
                "TOO_MANY_EXPLICIT_TRIGGER_ALIASES",
                "Explicit trigger alias count exceeds the limit.",
                $"{path}.explicit_trigger_aliases"));
        }
        for (var index = 0; index < routingPolicy.ExplicitTriggerAliases.Count; index++)
        {
            var alias = routingPolicy.ExplicitTriggerAliases[index];
            var normalizedAlias = (alias ?? string.Empty).Normalize(NormalizationForm.FormC);
            if (!IsBoundedOpaqueIdentifier(normalizedAlias, IdentifierMaxBytes))
            {
                diagnostics.Add(Diagnostic(
                    "INVALID_EXPLICIT_TRIGGER_ALIAS",
                    "Explicit trigger alias is required and must be bounded.",
                    $"{path}.explicit_trigger_aliases[{index}]"));
            }
            else if (!aliases.Add(normalizedAlias))
            {
                diagnostics.Add(Diagnostic(
                    "DUPLICATE_EXPLICIT_TRIGGER_ALIAS",
                    "Explicit trigger aliases must be globally unique.",
                    $"{path}.explicit_trigger_aliases[{index}]"));
            }
        }

        diagnostics.AddRange(ValidateToolPolicy(
            routingPolicy.TaskToolPolicy,
            $"{path}.task_tool_policy"));
        ValidateNarrowingToolPolicy(
            routingPolicy.TaskToolPolicy,
            maximumPolicy,
            $"{path}.task_tool_policy",
            "TASK_TOOL_POLICY_EXCEEDS_PROFILE_MAXIMUM",
            diagnostics);

        if (routingPolicy.SideEffectClass is not (
                AgentProfileSkillSideEffectClass.ReadOnly or
                AgentProfileSkillSideEffectClass.ExternalHandoff or
                AgentProfileSkillSideEffectClass.ServiceCall or
                AgentProfileSkillSideEffectClass.Maintenance))
        {
            diagnostics.Add(Diagnostic(
                "INVALID_SKILL_SIDE_EFFECT_CLASS",
                "Skill side-effect class must be explicit.",
                $"{path}.side_effect_class"));
        }
    }

    private static void ValidateNarrowingToolPolicy(
        AgentProfileToolPolicy? candidate,
        AgentProfileToolPolicy? maximum,
        string path,
        string diagnosticCode,
        List<AgentProfileSafeDiagnostic> diagnostics)
    {
        if (candidate is null || maximum is null ||
            candidate.Mode != AgentProfileToolPolicyMode.ExplicitAllowlist)
        {
            if (candidate is not null && candidate.Mode == AgentProfileToolPolicyMode.InheritRouteMaximum)
            {
                diagnostics.Add(Diagnostic(
                    diagnosticCode,
                    "Narrowing tool policy cannot inherit the Profile maximum.",
                    path));
            }
            return;
        }

        if (maximum.Mode == AgentProfileToolPolicyMode.InheritRouteMaximum)
            return;
        if (maximum.Mode != AgentProfileToolPolicyMode.ExplicitAllowlist)
            return;

        var maximumToolNames = maximum.ToolNames
            .Select(static value => value.Normalize(NormalizationForm.FormC))
            .ToHashSet(StringComparer.Ordinal);
        var maximumToolSetRefs = maximum.ToolSetRefs
            .Select(static value => value.Normalize(NormalizationForm.FormC))
            .ToHashSet(StringComparer.Ordinal);
        var isSubset = candidate.ToolNames.All(value =>
                maximumToolNames.Contains(value.Normalize(NormalizationForm.FormC))) &&
            candidate.ToolSetRefs.All(value =>
                maximumToolSetRefs.Contains(value.Normalize(NormalizationForm.FormC)));
        if (!isSubset)
        {
            diagnostics.Add(Diagnostic(
                diagnosticCode,
                "Narrowing tool policy cannot exceed the Profile maximum.",
                path));
        }
    }

    private static bool IsSupportedSkillActivationMode(AgentProfileSkillActivationMode mode) =>
        mode is AgentProfileSkillActivationMode.Always or
            AgentProfileSkillActivationMode.Routed or
            AgentProfileSkillActivationMode.DefaultForUnmatchedTurn;

    private static bool IsSupportedToolPolicyMode(AgentProfileToolPolicyMode mode) =>
        mode is AgentProfileToolPolicyMode.InheritRouteMaximum or
            AgentProfileToolPolicyMode.ExplicitAllowlist;

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

    private static string NormalizeBindingIdentity(string bindingId) =>
        bindingId.Normalize(NormalizationForm.FormC);

    private static IEnumerable<(string Path, string Content)> EnumerateTextAssets(
        ResolvedOrnnSkillPackage package)
    {
        foreach (var workflow in package.Workflows)
        {
            for (var index = 0; index < workflow.WorkflowYamls.Count; index++)
                yield return ($"workflows.{workflow.WorkflowId}[{index}]", workflow.WorkflowYamls[index]);
        }

        foreach (var script in package.Scripts)
        {
            foreach (var source in script.SourceFiles)
                yield return ($"scripts.{script.ScriptId}.{source.Path}", source.Content);
            foreach (var proto in script.ProtoFiles)
                yield return ($"scripts.{script.ScriptId}.{proto.Path}", proto.Content);
        }

        foreach (var reference in package.References)
            yield return ($"references.{reference.Path}", reference.Content);
        foreach (var asset in package.Assets)
            yield return ($"assets.{asset.Path}", asset.Content);
    }

    private static long PromptByteCount(ResolvedOrnnSkillPackage package) =>
        Encoding.UTF8.GetByteCount(package.Description ?? string.Empty) +
        Encoding.UTF8.GetByteCount(package.Instructions ?? string.Empty) +
        Encoding.UTF8.GetByteCount(package.Arguments ?? string.Empty) +
        Encoding.UTF8.GetByteCount(package.WhenToUse ?? string.Empty);

    private static void AddHardLimitDiagnostic(
        ICollection<AgentProfileSafeDiagnostic> diagnostics,
        string code,
        string message,
        string path)
    {
        if (diagnostics.Count < AgentProfileValidationLimits.DiagnosticMaxCount)
            diagnostics.Add(Diagnostic(code, message, path));
    }

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
        Diagnostic(
            diagnostic.Code,
            diagnostic.Message,
            string.IsNullOrEmpty(diagnostic.Path)
                ? prefix
                : $"{prefix}.{diagnostic.Path}");

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
        NormalizeDiagnostic(new AgentProfileSafeDiagnostic
        {
            Code = code,
            Message = message,
            Path = path,
        });

    private static string BoundDiagnosticField(string? value)
    {
        var normalized = (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Normalize(NormalizationForm.FormC);
        var bytes = Encoding.UTF8.GetBytes(normalized);
        if (bytes.Length <= AgentProfileValidationLimits.DiagnosticMessageMaxUtf8Bytes)
            return normalized;

        var length = AgentProfileValidationLimits.DiagnosticMessageMaxUtf8Bytes;
        while (length > 0 && (bytes[length] & 0xC0) == 0x80)
            length--;
        return Encoding.UTF8.GetString(bytes, 0, length);
    }
}
