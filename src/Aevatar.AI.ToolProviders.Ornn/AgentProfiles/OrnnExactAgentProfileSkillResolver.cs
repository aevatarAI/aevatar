using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Abstractions.Ports;

namespace Aevatar.AI.ToolProviders.Ornn.AgentProfiles;

public sealed class OrnnExactAgentProfileSkillResolver : IExactOrnnSkillResolver
{
    private readonly OrnnSkillClient _client;
    private readonly OrnnAgentProfileSkillPackageMapper _mapper;

    public OrnnExactAgentProfileSkillResolver(
        OrnnSkillClient client,
        OrnnAgentProfileSkillPackageMapper mapper)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
    }

    public async Task<ExactOrnnSkillResolutionResult> ResolveAsync(
        string nyxIdAccessToken,
        ExactOrnnSkillReference reference,
        CancellationToken ct = default)
    {
        var validation = AgentProfilePolicies.ValidateExactSkillReference(reference);
        if (validation.Count > 0)
        {
            var diagnostic = validation[0];
            return ExactOrnnSkillResolutionResult.Failed(
                diagnostic.Code,
                diagnostic.Message,
                diagnostic.Path);
        }

        if (string.IsNullOrWhiteSpace(nyxIdAccessToken))
        {
            return ExactOrnnSkillResolutionResult.Failed(
                "ORNN_ACCESS_TOKEN_REQUIRED",
                "An Ornn access token is required.");
        }

        var exactReference = AgentProfileDeterminism.NormalizeExactSkillReference(reference);
        var detailRead = await _client.GetExactSkillDetailAsync(
            nyxIdAccessToken,
            exactReference.SkillGuid,
            exactReference.LiteralVersion,
            ct);
        if (detailRead.Value is null)
            return MapReadFailure(detailRead);

        var jsonRead = await _client.GetExactSkillJsonAsync(
            nyxIdAccessToken,
            exactReference.SkillGuid,
            exactReference.LiteralVersion,
            ct);
        if (jsonRead.Value is null)
            return MapReadFailure(jsonRead);

        var detail = detailRead.Value;
        var json = jsonRead.Value;
        if (!string.Equals(detail.Guid, exactReference.SkillGuid, StringComparison.Ordinal) ||
            !string.Equals(json.Version, exactReference.LiteralVersion, StringComparison.Ordinal) ||
            !string.Equals(detail.Name, json.Name, StringComparison.Ordinal) ||
            !string.Equals(detail.Name, exactReference.ExpectedName, StringComparison.Ordinal))
        {
            return ExactOrnnSkillResolutionResult.Failed(
                "ORNN_SKILL_IDENTITY_MISMATCH",
                "Exact Ornn skill identity did not match the requested reference.");
        }

        if (!string.Equals(
                detail.CreatedBy,
                exactReference.ExpectedPublisherId,
                StringComparison.Ordinal))
        {
            return ExactOrnnSkillResolutionResult.Failed(
                "ORNN_SKILL_PUBLISHER_MISMATCH",
                "Exact Ornn skill publisher did not match the requested reference.");
        }

        if (string.IsNullOrWhiteSpace(detail.SkillHash))
        {
            return ExactOrnnSkillResolutionResult.Failed(
                "INVALID_SKILL_PACKAGE",
                "Exact Ornn skill package is invalid.",
                "skill_hash");
        }

        return await _mapper.MapAsync(detail, json, ct);
    }

    private static ExactOrnnSkillResolutionResult MapReadFailure<T>(
        OrnnExactSkillReadResult<T> read)
        where T : class
    {
        if (read.Failure == OrnnExactSkillReadFailure.ProxyFailure)
        {
            return read.ProxyStatus switch
            {
                403 => ExactOrnnSkillResolutionResult.Failed(
                    "ORNN_SKILL_ACCESS_DENIED",
                    "The exact Ornn skill is not accessible."),
                404 => ExactOrnnSkillResolutionResult.Failed(
                    "ORNN_SKILL_NOT_FOUND",
                    "The exact Ornn skill was not found."),
                _ => DependencyUnavailable(),
            };
        }

        return DependencyUnavailable();
    }

    private static ExactOrnnSkillResolutionResult DependencyUnavailable() =>
        ExactOrnnSkillResolutionResult.Failed(
            "ORNN_DEPENDENCY_UNAVAILABLE",
            "The exact Ornn skill dependency is unavailable.");
}
