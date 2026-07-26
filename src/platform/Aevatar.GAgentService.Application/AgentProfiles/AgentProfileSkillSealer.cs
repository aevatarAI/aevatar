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

        ValidateToolSetReferences(normalizedContent.ToolPolicy, "tool_policy", diagnostics);
        ValidateToolSetReferences(
            normalizedContent.RecoveryToolPolicy,
            "recovery_tool_policy",
            diagnostics);
        foreach (var binding in normalizedContent.SkillBindings)
        {
            if (binding.RoutingPolicy is not null)
            {
                ValidateToolSetReferences(
                    binding.RoutingPolicy.TaskToolPolicy,
                    $"skill_bindings.{binding.BindingId}.routing_policy.task_tool_policy",
                    diagnostics);
            }
        }
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

            var bindingDiagnosticCount = diagnostics.Count;
            ValidateDeclaredToolNames(binding.BindingId, package, diagnostics);
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

            if (diagnostics.Count != bindingDiagnosticCount)
                continue;

            sealedBindings.Add(new SealedAgentProfileSkillBinding
            {
                BindingId = binding.BindingId,
                ActivationMode = binding.ActivationMode,
                Skill = sealedSkill,
                RoutingPolicy = binding.RoutingPolicy?.Clone(),
            });
        }

        var snapshot = new AgentProfilePublishedSnapshot
        {
            Identity = normalizedIdentity,
            DisplayName = normalizedContent.DisplayName,
            Purpose = normalizedContent.Purpose,
            Instructions = normalizedContent.Instructions,
            ToolPolicy = normalizedContent.ToolPolicy.Clone(),
            RecoveryToolPolicy = normalizedContent.RecoveryToolPolicy.Clone(),
            PublishedRevision = 0,
            SourceDraftSha256 = AgentProfileDeterminism.ComputeSourceDraftSha256(normalizedContent),
        };
        snapshot.SkillBindings.Add(sealedBindings);
        snapshot.SnapshotSha256 = AgentProfileDeterminism.ComputeExecutionSnapshotSha256(snapshot);
        AddDiagnostics(
            diagnostics,
            AgentProfilePolicies.ValidatePublishedSnapshotHardLimits(snapshot));
        if (diagnostics.Count > 0)
            return AgentProfileSealingResult.Failed(diagnostics);

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
    }

    private void ValidateToolSetReferences(
        AgentProfileToolPolicy policy,
        string path,
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
                $"{path}.tool_set_refs[{index}]");
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

    private static void ValidateDeclaredToolNames(
        string bindingId,
        ResolvedOrnnSkillPackage package,
        List<AgentProfileSafeDiagnostic> diagnostics)
    {
        for (var index = 0; index < package.DeclaredToolNames.Count; index++)
        {
            if (Encoding.UTF8.GetByteCount(package.DeclaredToolNames[index]) <=
                AgentProfileValidationLimits.IdentifierMaxUtf8Bytes)
            {
                continue;
            }

            AddDiagnostic(
                diagnostics,
                "INVALID_DECLARED_TOOL_NAME",
                "Declared skill tool name exceeds the UTF-8 byte limit.",
                $"skill_bindings.{bindingId}.declared_tool_names[{index}]");
        }
    }

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
