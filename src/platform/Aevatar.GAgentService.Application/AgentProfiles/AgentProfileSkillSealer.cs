using System.Text;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Abstractions.Ports;

namespace Aevatar.GAgentService.Application.AgentProfiles;

public sealed class AgentProfileSealingResult
{
    private readonly AgentProfilePublishedSnapshot? _snapshot;
    private readonly IReadOnlyList<AgentProfileSafeDiagnostic> _diagnostics;

    private AgentProfileSealingResult(
        AgentProfilePublishedSnapshot? snapshot,
        IReadOnlyList<AgentProfileSafeDiagnostic> diagnostics)
    {
        _snapshot = snapshot?.Clone();
        _diagnostics = diagnostics.Select(static diagnostic => diagnostic.Clone()).ToArray();
    }

    public bool IsSuccess => _snapshot is not null && _diagnostics.Count == 0;

    public AgentProfilePublishedSnapshot? Snapshot => _snapshot?.Clone();

    public IReadOnlyList<AgentProfileSafeDiagnostic> Diagnostics =>
        _diagnostics.Select(static diagnostic => diagnostic.Clone()).ToArray();

    public static AgentProfileSealingResult Success(AgentProfilePublishedSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new AgentProfileSealingResult(snapshot, []);
    }

    public static AgentProfileSealingResult Failed(
        IReadOnlyList<AgentProfileSafeDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return new AgentProfileSealingResult(null, diagnostics);
    }
}

public sealed class AgentProfileSkillSealer
{
    private readonly IExactOrnnSkillResolver _resolver;
    private readonly IToolSetRegistry _toolSetRegistry;

    public AgentProfileSkillSealer(
        IExactOrnnSkillResolver resolver,
        IToolSetRegistry toolSetRegistry)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _toolSetRegistry = toolSetRegistry ?? throw new ArgumentNullException(nameof(toolSetRegistry));
    }

    public async Task<AgentProfileSealingResult> ResolveAndSealAsync(
        AgentProfileIdentity identity,
        AgentProfileContent content,
        string? nyxIdAccessToken,
        CancellationToken ct = default)
    {
        var diagnostics = new List<AgentProfileSafeDiagnostic>();
        AddDiagnostics(diagnostics, AgentProfilePolicies.ValidateIdentity(identity));
        AddDiagnostics(diagnostics, AgentProfilePolicies.ValidateContent(content));
        if (content is not null)
            ValidatePublishOnlyStructure(content, diagnostics);
        if (diagnostics.Count > 0)
            return AgentProfileSealingResult.Failed(diagnostics);

        AgentProfileIdentity normalizedIdentity;
        AgentProfileContent normalizedContent;
        try
        {
            normalizedIdentity = AgentProfileDeterminism.NormalizeIdentity(identity);
            normalizedContent = AgentProfileDeterminism.NormalizeContent(content!);
        }
        catch (AgentProfileContractValidationException exception)
        {
            AddDiagnostics(diagnostics, exception.Diagnostics);
            return AgentProfileSealingResult.Failed(diagnostics);
        }

        ValidateToolSetReferences(normalizedContent.ToolPolicy, diagnostics);
        if (normalizedContent.SkillBindings.Count > 0 &&
            string.IsNullOrWhiteSpace(nyxIdAccessToken))
        {
            AddDiagnostic(
                diagnostics,
                "ORNN_ACCESS_TOKEN_REQUIRED",
                "An Ornn access token is required when Profile skills are bound.",
                "skill_bindings");
        }
        if (diagnostics.Count > 0)
            return AgentProfileSealingResult.Failed(diagnostics);

        var sealedBindings = new List<SealedAgentProfileSkillBinding>(
            normalizedContent.SkillBindings.Count);
        long aggregatePromptBytes = Encoding.UTF8.GetByteCount(normalizedContent.Instructions);
        foreach (var binding in normalizedContent.SkillBindings)
        {
            ct.ThrowIfCancellationRequested();
            var resolution = await _resolver.ResolveAsync(
                nyxIdAccessToken!,
                binding.Skill,
                ct);
            if (!resolution.IsSuccess || resolution.Package is null)
            {
                var failure = resolution.Failure ?? new AgentProfileSafeDiagnostic
                {
                    Code = "ORNN_DEPENDENCY_UNAVAILABLE",
                    Message = "The exact Ornn skill dependency is unavailable.",
                };
                AddDiagnostic(
                    diagnostics,
                    failure.Code,
                    failure.Message,
                    BindingPath(binding.BindingId, failure.Path));
                continue;
            }

            ResolvedOrnnSkillPackage package;
            try
            {
                package = AgentProfileDeterminism.NormalizeResolvedSkillPackage(resolution.Package);
            }
            catch (AgentProfileContractValidationException exception)
            {
                AddDiagnostics(
                    diagnostics,
                    exception.Diagnostics,
                    $"skill_bindings.{binding.BindingId}.package");
                continue;
            }

            aggregatePromptBytes += PromptByteCount(package);
            var bindingDiagnosticCount = diagnostics.Count;
            ValidateTextAssets(binding.BindingId, package, diagnostics);
            ValidateDeclaredDependencies(
                binding.BindingId,
                package,
                normalizedContent.ToolPolicy,
                diagnostics);

            var sealedSkill = new SealedAgentProfileSkill
            {
                ExactReference = binding.Skill.Clone(),
                Package = package,
            };
            try
            {
                sealedSkill.ContentSha256 = AgentProfileDeterminism.ComputeSkillContentSha256(sealedSkill);
            }
            catch (AgentProfileContractValidationException exception)
            {
                AddDiagnostics(
                    diagnostics,
                    exception.Diagnostics,
                    $"skill_bindings.{binding.BindingId}.skill");
            }

            if (sealedSkill.CalculateSize() > AgentProfileValidationLimits.SealedSkillMaxSerializedBytes)
            {
                AddDiagnostic(
                    diagnostics,
                    "SEALED_SKILL_TOO_LARGE",
                    "Sealed skill serialized size exceeds the limit.",
                    $"skill_bindings.{binding.BindingId}.skill");
            }

            if (diagnostics.Count != bindingDiagnosticCount)
                continue;

            sealedBindings.Add(new SealedAgentProfileSkillBinding
            {
                BindingId = binding.BindingId,
                ActivationMode = binding.ActivationMode,
                Skill = sealedSkill,
            });
        }

        if (aggregatePromptBytes > AgentProfileValidationLimits.AggregatePromptMaxUtf8Bytes)
        {
            AddDiagnostic(
                diagnostics,
                "AGGREGATE_PROMPT_BYTES_EXCEEDED",
                "Aggregate Profile prompt bytes exceed the limit.",
                "skill_bindings");
        }
        if (aggregatePromptBytes > AgentProfileValidationLimits.AggregatePromptMaxTokens)
        {
            AddDiagnostic(
                diagnostics,
                "AGGREGATE_PROMPT_TOKENS_EXCEEDED",
                "Aggregate Profile prompt token upper bound exceeds the limit.",
                "skill_bindings");
        }
        if (diagnostics.Count > 0)
            return AgentProfileSealingResult.Failed(diagnostics);

        var snapshot = new AgentProfilePublishedSnapshot
        {
            Identity = normalizedIdentity,
            DisplayName = normalizedContent.DisplayName,
            Purpose = normalizedContent.Purpose,
            Instructions = normalizedContent.Instructions,
            ToolPolicy = normalizedContent.ToolPolicy.Clone(),
            PublishedRevision = 0,
            SourceDraftSha256 = AgentProfileDeterminism.ComputeSourceDraftSha256(normalizedContent),
        };
        snapshot.SkillBindings.Add(sealedBindings);
        snapshot.SnapshotSha256 = AgentProfileDeterminism.ComputeExecutionSnapshotSha256(snapshot);
        if (snapshot.CalculateSize() > AgentProfileValidationLimits.PublishedSnapshotMaxSerializedBytes)
        {
            AddDiagnostic(
                diagnostics,
                "PUBLISHED_SNAPSHOT_TOO_LARGE",
                "Published Profile snapshot serialized size exceeds the limit.",
                "snapshot");
            return AgentProfileSealingResult.Failed(diagnostics);
        }

        return AgentProfileSealingResult.Success(
            AgentProfileDeterminism.NormalizePublishedSnapshot(snapshot));
    }

    private static void ValidatePublishOnlyStructure(
        AgentProfileContent content,
        List<AgentProfileSafeDiagnostic> diagnostics)
    {
        var defaultCount = content.SkillBindings.Count(static binding =>
            binding.ActivationMode == AgentProfileSkillActivationMode.DefaultForUnmatchedTurn);
        if (defaultCount > 1)
        {
            AddDiagnostic(
                diagnostics,
                "MULTIPLE_DEFAULT_SKILLS",
                "Only one default-for-unmatched-turn skill is allowed.",
                "skill_bindings");
        }

        for (var index = 0; index < content.SkillBindings.Count; index++)
        {
            var activationMode = content.SkillBindings[index].ActivationMode;
            if (activationMode is AgentProfileSkillActivationMode.Unspecified or
                AgentProfileSkillActivationMode.Always or
                AgentProfileSkillActivationMode.Routed or
                AgentProfileSkillActivationMode.DefaultForUnmatchedTurn)
            {
                continue;
            }

            AddDiagnostic(
                diagnostics,
                "INVALID_SKILL_ACTIVATION_MODE",
                "Skill activation mode is invalid.",
                $"skill_bindings[{index}].activation_mode");
        }
    }

    private void ValidateToolSetReferences(
        AgentProfileToolPolicy policy,
        List<AgentProfileSafeDiagnostic> diagnostics)
    {
        var registered = _toolSetRegistry.GetRegisteredNames()
            .ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < policy.ToolSetRefs.Count; index++)
        {
            if (registered.Contains(policy.ToolSetRefs[index]))
                continue;

            AddDiagnostic(
                diagnostics,
                "UNKNOWN_TOOL_SET_REF",
                "Profile tool-set reference is not registered.",
                $"tool_policy.tool_set_refs[{index}]");
        }
    }

    private static void ValidateDeclaredDependencies(
        string bindingId,
        ResolvedOrnnSkillPackage package,
        AgentProfileToolPolicy policy,
        List<AgentProfileSafeDiagnostic> diagnostics)
    {
        if (policy.Mode != AgentProfileToolPolicyMode.ExplicitAllowlist)
            return;

        var allowed = policy.ToolNames.ToHashSet(StringComparer.Ordinal);
        foreach (var dependency in package.DeclaredToolNames)
        {
            if (allowed.Contains(dependency))
                continue;

            AddDiagnostic(
                diagnostics,
                "SKILL_TOOL_DEPENDENCY_NOT_ALLOWED",
                "A declared skill tool dependency is not in the explicit allowlist.",
                $"skill_bindings.{bindingId}.declared_tool_names.{dependency}");
        }
    }

    private static void ValidateTextAssets(
        string bindingId,
        ResolvedOrnnSkillPackage package,
        List<AgentProfileSafeDiagnostic> diagnostics)
    {
        foreach (var (path, content) in EnumerateTextAssets(package))
        {
            if (Encoding.UTF8.GetByteCount(content) <= AgentProfileValidationLimits.TextAssetMaxUtf8Bytes)
                continue;

            AddDiagnostic(
                diagnostics,
                "TEXT_ASSET_TOO_LARGE",
                "Sealed skill text asset exceeds the UTF-8 byte limit.",
                $"skill_bindings.{bindingId}.{path}");
        }
    }

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
        Encoding.UTF8.GetByteCount(package.Description) +
        Encoding.UTF8.GetByteCount(package.Instructions) +
        Encoding.UTF8.GetByteCount(package.Arguments) +
        Encoding.UTF8.GetByteCount(package.WhenToUse);

    private static string BindingPath(string bindingId, string path) =>
        string.IsNullOrEmpty(path)
            ? $"skill_bindings.{bindingId}"
            : $"skill_bindings.{bindingId}.{path}";

    private static void AddDiagnostics(
        List<AgentProfileSafeDiagnostic> destination,
        IEnumerable<AgentProfileSafeDiagnostic> diagnostics,
        string prefix = "")
    {
        foreach (var diagnostic in diagnostics)
        {
            AddDiagnostic(
                destination,
                diagnostic.Code,
                diagnostic.Message,
                string.IsNullOrEmpty(prefix)
                    ? diagnostic.Path
                    : string.IsNullOrEmpty(diagnostic.Path)
                        ? prefix
                        : $"{prefix}.{diagnostic.Path}");
        }
    }

    private static void AddDiagnostic(
        List<AgentProfileSafeDiagnostic> diagnostics,
        string code,
        string message,
        string path)
    {
        if (diagnostics.Count >= AgentProfileValidationLimits.DiagnosticMaxCount)
            return;

        diagnostics.Add(new AgentProfileSafeDiagnostic
        {
            Code = BoundUtf8(code, AgentProfileValidationLimits.IdentifierMaxUtf8Bytes),
            Message = BoundUtf8(
                message,
                AgentProfileValidationLimits.DiagnosticMessageMaxUtf8Bytes),
            Path = BoundUtf8(path, AgentProfileValidationLimits.DiagnosticMessageMaxUtf8Bytes),
        });
    }

    private static string BoundUtf8(string? value, int maxBytes)
    {
        var normalized = (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Normalize(NormalizationForm.FormC);
        var bytes = Encoding.UTF8.GetBytes(normalized);
        if (bytes.Length <= maxBytes)
            return normalized;

        var length = maxBytes;
        while (length > 0 && (bytes[length] & 0xC0) == 0x80)
            length--;
        return Encoding.UTF8.GetString(bytes, 0, length);
    }
}
