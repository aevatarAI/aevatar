using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Aevatar.GAgentService.Abstractions.AgentProfiles;

public static class AgentProfileDeterminism
{
    private const int OpaqueIdentityBytes = 18;

    public static AgentProfileReference NormalizeReference(AgentProfileReference reference)
    {
        ThrowIfInvalid(AgentProfilePolicies.ValidateReference(reference));
        return new AgentProfileReference
        {
            OwnerHandle = NormalizeText(reference.OwnerHandle),
            ProfileSlug = NormalizeText(reference.ProfileSlug),
        };
    }

    public static AgentProfileOwnerIdentity NormalizeOwnerIdentity(AgentProfileOwnerIdentity owner)
    {
        ThrowIfInvalid(AgentProfilePolicies.ValidateOwnerIdentity(owner));
        return owner.OwnerCase switch
        {
            AgentProfileOwnerIdentity.OwnerOneofCase.User => new AgentProfileOwnerIdentity
            {
                User = new AgentProfileUserOwnerIdentity
                {
                    IdentityProvider = NormalizeText(owner.User.IdentityProvider),
                    SubjectId = NormalizeText(owner.User.SubjectId),
                },
            },
            AgentProfileOwnerIdentity.OwnerOneofCase.System => new AgentProfileOwnerIdentity
            {
                System = new AgentProfileSystemOwnerIdentity
                {
                    PlatformId = NormalizeText(owner.System.PlatformId),
                },
            },
            _ => throw new AgentProfileContractValidationException(
                AgentProfilePolicies.ValidateOwnerIdentity(owner)),
        };
    }

    public static AgentProfileIdentity NormalizeIdentity(AgentProfileIdentity identity)
    {
        ThrowIfInvalid(AgentProfilePolicies.ValidateIdentity(identity));
        return new AgentProfileIdentity
        {
            ProfileId = NormalizeText(identity.ProfileId),
            Owner = NormalizeOwnerIdentity(identity.Owner),
            OwningScopeId = NormalizeText(identity.OwningScopeId),
            Reference = NormalizeReference(identity.Reference),
        };
    }

    public static ExactOrnnSkillReference NormalizeExactSkillReference(
        ExactOrnnSkillReference reference)
    {
        ThrowIfInvalid(AgentProfilePolicies.ValidateExactSkillReference(reference));
        return new ExactOrnnSkillReference
        {
            SkillGuid = NormalizeText(reference.SkillGuid),
            LiteralVersion = NormalizeText(reference.LiteralVersion),
            ExpectedName = NormalizeText(reference.ExpectedName),
            ExpectedPublisherId = NormalizeText(reference.ExpectedPublisherId),
        };
    }

    public static AgentProfileToolPolicy NormalizeToolPolicy(AgentProfileToolPolicy policy)
    {
        var validationContent = new AgentProfileContent
        {
            DisplayName = "validation",
            ToolPolicy = policy,
        };
        ThrowIfInvalid(AgentProfilePolicies.ValidateContent(validationContent));

        var normalized = new AgentProfileToolPolicy { Mode = policy.Mode };
        normalized.ToolNames.Add(NormalizeDistinctStrings(policy.ToolNames));
        normalized.ToolSetRefs.Add(NormalizeDistinctStrings(policy.ToolSetRefs));
        return normalized;
    }

    public static AgentProfileSkillBinding NormalizeSkillBinding(AgentProfileSkillBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var validationContent = new AgentProfileContent
        {
            DisplayName = "validation",
            ToolPolicy = new AgentProfileToolPolicy
            {
                Mode = AgentProfileToolPolicyMode.InheritRouteMaximum,
            },
        };
        validationContent.SkillBindings.Add(binding);
        ThrowIfInvalid(AgentProfilePolicies.ValidateContent(validationContent));

        return new AgentProfileSkillBinding
        {
            BindingId = NormalizeText(binding.BindingId),
            ActivationMode = binding.ActivationMode,
            Skill = NormalizeExactSkillReference(binding.Skill),
        };
    }

    public static AgentProfileContent NormalizeContent(AgentProfileContent content)
    {
        ThrowIfInvalid(AgentProfilePolicies.ValidateContent(content));

        var normalized = new AgentProfileContent
        {
            DisplayName = NormalizeText(content.DisplayName),
            Purpose = NormalizeText(content.Purpose),
            Instructions = NormalizeText(content.Instructions),
            ToolPolicy = NormalizeToolPolicy(content.ToolPolicy),
        };
        normalized.SkillBindings.Add(content.SkillBindings
            .Select(NormalizeSkillBinding)
            .OrderBy(static binding => binding.BindingId, StringComparer.Ordinal));
        return normalized;
    }

    public static AgentProfileNamedTextAsset NormalizeNamedTextAsset(
        AgentProfileNamedTextAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (string.IsNullOrWhiteSpace(asset.Path) || HasBoundaryWhitespace(asset.Path))
        {
            throw ValidationException(
                "INVALID_ASSET_PATH",
                "Named text asset path is required.",
                "path");
        }

        return new AgentProfileNamedTextAsset
        {
            Path = NormalizeText(asset.Path),
            Content = NormalizeText(asset.Content),
        };
    }

    public static AgentProfileWorkflowAsset NormalizeWorkflowAsset(
        AgentProfileWorkflowAsset workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (string.IsNullOrWhiteSpace(workflow.WorkflowId) || HasBoundaryWhitespace(workflow.WorkflowId))
        {
            throw ValidationException(
                "INVALID_WORKFLOW_ID",
                "Workflow id is required.",
                "workflow_id");
        }

        var normalized = new AgentProfileWorkflowAsset
        {
            WorkflowId = NormalizeText(workflow.WorkflowId),
        };
        normalized.WorkflowYamls.Add(NormalizeDistinctStrings(workflow.WorkflowYamls));
        return normalized;
    }

    public static AgentProfileScriptAsset NormalizeScriptAsset(AgentProfileScriptAsset script)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (string.IsNullOrWhiteSpace(script.ScriptId) || HasBoundaryWhitespace(script.ScriptId))
        {
            throw ValidationException(
                "INVALID_SCRIPT_ID",
                "Script id is required.",
                "script_id");
        }

        var normalized = new AgentProfileScriptAsset
        {
            ScriptId = NormalizeText(script.ScriptId),
            EntryBehaviorTypeName = NormalizeText(script.EntryBehaviorTypeName),
        };
        normalized.SourceFiles.Add(NormalizeNamedAssets(script.SourceFiles));
        normalized.ProtoFiles.Add(NormalizeNamedAssets(script.ProtoFiles));
        return normalized;
    }

    public static ResolvedOrnnSkillPackage NormalizeResolvedSkillPackage(
        ResolvedOrnnSkillPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        ThrowIfInvalid(AgentProfilePolicies.ValidateExactSkillReference(new ExactOrnnSkillReference
        {
            SkillGuid = package.SkillGuid,
            LiteralVersion = package.LiteralVersion,
            ExpectedName = package.CanonicalName,
            ExpectedPublisherId = package.PublisherId,
        }));
        if (string.IsNullOrWhiteSpace(package.UpstreamSkillHash) ||
            HasBoundaryWhitespace(package.UpstreamSkillHash))
        {
            throw ValidationException(
                "MISSING_UPSTREAM_SKILL_HASH",
                "Resolved skill package requires an upstream content hash.",
                "upstream_skill_hash");
        }

        var normalized = new ResolvedOrnnSkillPackage
        {
            SkillGuid = NormalizeText(package.SkillGuid),
            LiteralVersion = NormalizeText(package.LiteralVersion),
            CanonicalName = NormalizeText(package.CanonicalName),
            PublisherId = NormalizeText(package.PublisherId),
            UpstreamSkillHash = NormalizeText(package.UpstreamSkillHash),
            Description = NormalizeText(package.Description),
            Instructions = NormalizeText(package.Instructions),
            Arguments = NormalizeText(package.Arguments),
            WhenToUse = NormalizeText(package.WhenToUse),
            ModelInvocable = package.ModelInvocable,
            UserInvocable = package.UserInvocable,
        };
        normalized.DeclaredToolNames.Add(NormalizeDistinctStrings(package.DeclaredToolNames));
        normalized.Workflows.Add(NormalizeIdentityEntries(
            package.Workflows,
            NormalizeWorkflowAsset,
            static workflow => workflow.WorkflowId,
            "CONFLICTING_WORKFLOW_ID",
            "Workflow entries sharing an id must be structurally identical.",
            "workflow_id"));
        normalized.Scripts.Add(NormalizeIdentityEntries(
            package.Scripts,
            NormalizeScriptAsset,
            static script => script.ScriptId,
            "CONFLICTING_SCRIPT_ID",
            "Script entries sharing an id must be structurally identical.",
            "script_id"));
        normalized.References.Add(NormalizeNamedAssets(package.References));
        normalized.Assets.Add(NormalizeNamedAssets(package.Assets));
        return normalized;
    }

    public static SealedAgentProfileSkill NormalizeSealedSkill(SealedAgentProfileSkill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ThrowIfInvalid(AgentProfilePolicies.ValidateSealedSkill(skill));
        return NormalizeSealedSkillCore(skill);
    }

    public static SealedAgentProfileSkillBinding NormalizeSealedSkillBinding(
        SealedAgentProfileSkillBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (string.IsNullOrWhiteSpace(binding.BindingId) ||
            HasBoundaryWhitespace(binding.BindingId) ||
            binding.ActivationMode == AgentProfileSkillActivationMode.Unspecified ||
            binding.Skill is null)
        {
            throw ValidationException(
                "INVALID_SEALED_SKILL_BINDING",
                "Sealed skill binding is incomplete.",
                "skill_bindings");
        }

        return new SealedAgentProfileSkillBinding
        {
            BindingId = NormalizeText(binding.BindingId),
            ActivationMode = binding.ActivationMode,
            Skill = NormalizeSealedSkill(binding.Skill),
        };
    }

    public static AgentProfilePublishedSnapshot NormalizePublishedSnapshot(
        AgentProfilePublishedSnapshot snapshot)
    {
        ThrowIfInvalid(AgentProfilePolicies.ValidatePublishedSnapshot(snapshot));

        var normalized = new AgentProfilePublishedSnapshot
        {
            Identity = NormalizeIdentity(snapshot.Identity),
            DisplayName = NormalizeText(snapshot.DisplayName),
            Purpose = NormalizeText(snapshot.Purpose),
            Instructions = NormalizeText(snapshot.Instructions),
            ToolPolicy = NormalizeToolPolicy(snapshot.ToolPolicy),
            PublishedRevision = snapshot.PublishedRevision,
            SourceDraftSha256 = snapshot.SourceDraftSha256,
            SnapshotSha256 = snapshot.SnapshotSha256,
        };
        normalized.SkillBindings.Add(snapshot.SkillBindings
            .Select(NormalizeSealedSkillBinding)
            .OrderBy(static binding => binding.BindingId, StringComparer.Ordinal));
        return normalized;
    }

    public static AgentProfilePublishedSummary NormalizePublishedSummary(
        AgentProfilePublishedSummary summary)
    {
        ThrowIfInvalid(AgentProfilePolicies.ValidatePublishedSummary(summary));
        return new AgentProfilePublishedSummary
        {
            Reference = NormalizeReference(summary.Reference),
            DisplayName = NormalizeText(summary.DisplayName),
            Purpose = NormalizeText(summary.Purpose),
            PublishedRevision = summary.PublishedRevision,
            SnapshotSha256 = summary.SnapshotSha256,
        };
    }

    public static ByteString ComputeDraftSha256(AgentProfileContent content) =>
        Sha256(NormalizeContent(content));

    public static ByteString ComputeSourceDraftSha256(AgentProfileContent content) =>
        ComputeDraftSha256(content);

    public static ByteString ComputeSealedSkillSha256(SealedAgentProfileSkill skill)
    {
        ThrowIfInvalid(AgentProfilePolicies.ValidateSealedSkillIdentity(skill));
        var canonical = NormalizeSealedSkillCore(skill);
        canonical.ContentSha256 = ByteString.Empty;
        return Sha256(canonical);
    }

    public static ByteString ComputeSkillContentSha256(SealedAgentProfileSkill skill) =>
        ComputeSealedSkillSha256(skill);

    public static ByteString ComputePublishedSnapshotSha256(
        AgentProfilePublishedSnapshot snapshot)
    {
        var canonical = NormalizePublishedSnapshot(snapshot);
        canonical.PublishedRevision = 0;
        canonical.SnapshotSha256 = ByteString.Empty;
        canonical.SourceDraftSha256 = ByteString.Empty;
        canonical.DisplayName = string.Empty;
        canonical.Purpose = string.Empty;
        return Sha256(canonical);
    }

    public static ByteString ComputeExecutionSnapshotSha256(
        AgentProfilePublishedSnapshot snapshot) =>
        ComputePublishedSnapshotSha256(snapshot);

    public static ByteString ComputeCreateAgentProfileInputSha256(
        AgentProfileIdentity target,
        AgentProfileContent initialContent) =>
        ComputeOperationInputSha256(
            "create-agent-profile",
            CreateAgentProfileCommand.Descriptor,
            target,
            new CreateAgentProfileCommand
            {
                InitialContent = NormalizeContent(initialContent),
            });

    public static ByteString ComputeUpdateAgentProfileDraftInputSha256(
        AgentProfileIdentity target,
        AgentProfileContent content) =>
        ComputeOperationInputSha256(
            "update-agent-profile-draft",
            UpdateAgentProfileDraftCommand.Descriptor,
            target,
            new UpdateAgentProfileDraftCommand
            {
                Content = NormalizeContent(content),
            });

    public static ByteString ComputeUpsertAgentProfileSkillBindingInputSha256(
        AgentProfileIdentity target,
        AgentProfileSkillBinding binding) =>
        ComputeOperationInputSha256(
            "upsert-agent-profile-skill-binding",
            UpsertAgentProfileSkillBindingCommand.Descriptor,
            target,
            new UpsertAgentProfileSkillBindingCommand
            {
                Binding = NormalizeSkillBinding(binding),
            });

    public static ByteString ComputeRemoveAgentProfileSkillBindingInputSha256(
        AgentProfileIdentity target,
        string bindingId)
    {
        ThrowIfInvalid(AgentProfilePolicies.ValidateBindingId(bindingId));
        return ComputeOperationInputSha256(
            "remove-agent-profile-skill-binding",
            RemoveAgentProfileSkillBindingCommand.Descriptor,
            target,
            new RemoveAgentProfileSkillBindingCommand
            {
                BindingId = NormalizeText(bindingId),
            });
    }

    public static ByteString ComputePublishAgentProfileInputSha256(
        AgentProfileIdentity target,
        AgentProfilePublishedSnapshot snapshot) =>
        ComputeOperationInputSha256(
            "publish-agent-profile",
            PublishAgentProfileCommand.Descriptor,
            target,
            new PublishAgentProfileCommand
            {
                Snapshot = NormalizePublishedSnapshot(snapshot),
            });

    public static ByteString Sha256(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return ByteString.CopyFrom(SHA256.HashData(SerializeDeterministically(message)));
    }

    public static string Sha256Hex(IMessage message) =>
        Convert.ToHexStringLower(Sha256(message).Span);

    public static string CreateOperationId(
        AgentProfileUserOwnerIdentity owner,
        string scopeId,
        string idempotencyKey)
    {
        ValidateCreateIdentityInputs(owner, scopeId, idempotencyKey);
        return $"op_{Base64Url(HashIdentity(
            "aevatar.agent-profile.operation-id.v1",
            owner.IdentityProvider,
            owner.SubjectId,
            scopeId,
            idempotencyKey).AsSpan(0, OpaqueIdentityBytes))}";
    }

    public static string CreateOperationId(
        string operationKind,
        string profileId,
        string idempotencyKey)
    {
        RequireOpaqueValue(operationKind, nameof(operationKind));
        RequireOpaqueValue(profileId, nameof(profileId));
        RequireOpaqueValue(idempotencyKey, nameof(idempotencyKey));
        return $"op_{Base64Url(HashIdentity(
            "aevatar.agent-profile.operation-id.v1",
            operationKind,
            profileId,
            idempotencyKey).AsSpan(0, OpaqueIdentityBytes))}";
    }

    public static string CreateProfileId(
        AgentProfileUserOwnerIdentity owner,
        string scopeId,
        string idempotencyKey)
    {
        ValidateCreateIdentityInputs(owner, scopeId, idempotencyKey);
        return $"prof_{Base64Url(HashIdentity(
            "aevatar.agent-profile.profile-id.v1",
            owner.IdentityProvider,
            owner.SubjectId,
            scopeId,
            idempotencyKey).AsSpan(0, OpaqueIdentityBytes))}";
    }

    public static string CreateCommandId() =>
        $"cmd_{Base64Url(RandomNumberGenerator.GetBytes(OpaqueIdentityBytes))}";

    public static string CreateCorrelationId() =>
        $"corr_{Base64Url(RandomNumberGenerator.GetBytes(OpaqueIdentityBytes))}";

    private static byte[] HashIdentity(string domain, params string[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, domain);
        foreach (var value in values)
            Append(hash, value);
        return hash.GetHashAndReset();
    }

    private static ByteString ComputeOperationInputSha256(
        string operationKind,
        MessageDescriptor operationDescriptor,
        AgentProfileIdentity target,
        IMessage semanticPayload)
    {
        ArgumentNullException.ThrowIfNull(operationDescriptor);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(semanticPayload);

        var normalizedTarget = NormalizeIdentity(target);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "aevatar.agent-profile.operation-input.v1");
        Append(hash, operationKind);
        Append(hash, operationDescriptor.FullName);
        Append(hash, AgentProfileIdentity.Descriptor.FullName);
        Append(hash, SerializeDeterministically(normalizedTarget));
        Append(hash, semanticPayload.Descriptor.FullName);
        Append(hash, SerializeDeterministically(semanticPayload));
        return ByteString.CopyFrom(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value) =>
        Append(hash, Encoding.UTF8.GetBytes(value));

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] SerializeDeterministically(IMessage message)
    {
        using var stream = new MemoryStream(message.CalculateSize());
        using (var output = new CodedOutputStream(stream, leaveOpen: true))
        {
            output.Deterministic = true;
            message.WriteTo(output);
            output.Flush();
        }

        return stream.ToArray();
    }

    private static IReadOnlyList<string> NormalizeDistinctStrings(IEnumerable<string> values) =>
        values
            .Select(NormalizeText)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static SealedAgentProfileSkill NormalizeSealedSkillCore(SealedAgentProfileSkill skill) =>
        new()
        {
            ExactReference = NormalizeExactSkillReference(skill.ExactReference),
            Package = NormalizeResolvedSkillPackage(skill.Package),
            ContentSha256 = skill.ContentSha256,
        };

    private static IReadOnlyList<AgentProfileNamedTextAsset> NormalizeNamedAssets(
        IEnumerable<AgentProfileNamedTextAsset> assets) =>
        NormalizeIdentityEntries(
            assets,
            NormalizeNamedTextAsset,
            static asset => asset.Path,
            "CONFLICTING_ASSET_PATH",
            "Named text assets sharing a path must be structurally identical.",
            "path");

    private static IReadOnlyList<T> NormalizeIdentityEntries<T>(
        IEnumerable<T> values,
        Func<T, T> normalize,
        Func<T, string> identity,
        string conflictCode,
        string conflictMessage,
        string conflictPath)
        where T : class
    {
        var entries = new SortedDictionary<string, T>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var normalized = normalize(value);
            var key = identity(normalized);
            if (!entries.TryGetValue(key, out var existing))
            {
                entries.Add(key, normalized);
                continue;
            }

            if (!EqualityComparer<T>.Default.Equals(existing, normalized))
                throw ValidationException(conflictCode, conflictMessage, conflictPath);
        }

        return entries.Values.ToArray();
    }

    private static string NormalizeText(string? value) =>
        (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Normalize(NormalizationForm.FormC);

    private static void ValidateCreateIdentityInputs(
        AgentProfileUserOwnerIdentity owner,
        string scopeId,
        string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ThrowIfInvalid(AgentProfilePolicies.ValidateOwnerIdentity(new AgentProfileOwnerIdentity
        {
            User = owner,
        }));
        RequireOpaqueValue(scopeId, nameof(scopeId));
        RequireOpaqueValue(idempotencyKey, nameof(idempotencyKey));
    }

    private static void RequireOpaqueValue(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (HasBoundaryWhitespace(value))
            throw new ArgumentException("Opaque identity values cannot have boundary whitespace.", parameterName);
    }

    private static bool HasBoundaryWhitespace(string value) =>
        value.Length > 0 &&
        (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]));

    private static void ThrowIfInvalid(IReadOnlyList<AgentProfileSafeDiagnostic> diagnostics)
    {
        if (diagnostics.Count > 0)
            throw new AgentProfileContractValidationException(diagnostics);
    }

    private static AgentProfileContractValidationException ValidationException(
        string code,
        string message,
        string path) =>
        new(
        [
            new AgentProfileSafeDiagnostic
            {
                Code = code,
                Message = message,
                Path = path,
            },
        ]);
}
