using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Abstractions.Ports;
using Google.Protobuf;

namespace Aevatar.GAgentService.Application.AgentProfiles;

public sealed class AgentProfileDraftValidator
{
    private readonly IExactOrnnSkillResolver _resolver;
    private readonly IToolSetRegistry _toolSetRegistry;

    public AgentProfileDraftValidator(
        IExactOrnnSkillResolver resolver,
        IToolSetRegistry toolSetRegistry)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _toolSetRegistry = toolSetRegistry ?? throw new ArgumentNullException(nameof(toolSetRegistry));
    }

    public async Task<AgentProfileValidationReport> ValidateAsync(
        AgentProfileIdentity identity,
        AgentProfileContent draft,
        long draftRevision,
        ByteString draftSha256,
        string? nyxIdAccessToken,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(draftSha256);

        var capturingResolver = new CapturingExactOrnnSkillResolver(_resolver);
        var sealer = new AgentProfileSkillSealer(capturingResolver, _toolSetRegistry);
        var result = await sealer.ResolveAndSealAsync(
            identity,
            draft,
            nyxIdAccessToken,
            ct);
        var diagnostics = result.Diagnostics
            .Take(AgentProfileValidationLimits.DiagnosticMaxCount)
            .Select(AgentProfilePolicies.NormalizeDiagnostic)
            .ToArray();
        var resolvedSkills = CreateResolutionSummaries(draft, capturingResolver.Resolutions);

        return new AgentProfileValidationReport(
            result.IsSuccess,
            draftRevision,
            draftSha256,
            diagnostics,
            resolvedSkills);
    }

    private static IReadOnlyList<AgentProfileSkillResolutionSummary> CreateResolutionSummaries(
        AgentProfileContent draft,
        IReadOnlyList<CapturedSkillResolution?> resolutions)
    {
        if (resolutions.Count == 0)
            return [];

        var bindings = AgentProfileDeterminism.NormalizeContent(draft).SkillBindings;
        return bindings
            .Take(resolutions.Count)
            .Select((binding, index) => resolutions[index] is { } resolution
                ? new AgentProfileSkillResolutionSummary(
                    binding.BindingId,
                    resolution.ExactReference,
                    resolution.ContentSha256)
                : null)
            .Where(static summary => summary is not null)
            .Select(static summary => summary!)
            .ToArray();
    }

    private sealed class CapturingExactOrnnSkillResolver(
        IExactOrnnSkillResolver inner) : IExactOrnnSkillResolver
    {
        private readonly List<CapturedSkillResolution?> _resolutions = [];

        public IReadOnlyList<CapturedSkillResolution?> Resolutions => _resolutions;

        public async Task<ExactOrnnSkillResolutionResult> ResolveAsync(
            string nyxIdAccessToken,
            ExactOrnnSkillReference reference,
            CancellationToken ct = default)
        {
            var result = await inner.ResolveAsync(nyxIdAccessToken, reference, ct);
            _resolutions.Add(Capture(reference, result));
            return result;
        }

        private static CapturedSkillResolution? Capture(
            ExactOrnnSkillReference reference,
            ExactOrnnSkillResolutionResult result)
        {
            if (!result.IsSuccess || result.Package is null)
                return null;

            try
            {
                var skill = new SealedAgentProfileSkill
                {
                    ExactReference = AgentProfileDeterminism.NormalizeExactSkillReference(reference),
                    Package = AgentProfileDeterminism.NormalizeResolvedSkillPackage(result.Package),
                };
                return new CapturedSkillResolution(
                    skill.ExactReference,
                    AgentProfileDeterminism.ComputeSkillContentSha256(skill));
            }
            catch (AgentProfileContractValidationException)
            {
                return null;
            }
        }
    }

    private sealed record CapturedSkillResolution(
        ExactOrnnSkillReference ExactReference,
        ByteString ContentSha256);
}
