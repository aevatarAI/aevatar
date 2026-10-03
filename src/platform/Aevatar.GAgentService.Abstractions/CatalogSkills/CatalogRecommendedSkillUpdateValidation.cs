using System.Text.RegularExpressions;

namespace Aevatar.GAgentService.Abstractions.CatalogSkills;

public static partial class CatalogRecommendedSkillUpdateValidation
{
    public static void Validate(CatalogRecommendedSkillUpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Guid.TryParseExact(request.OperationId, "D", out _))
            throw InvalidRequest("A canonical operation ID is required.");
        Required(request.AdministratorId, "administratorId");
        Required(request.CatalogServiceId, "catalogServiceId");
        Required(request.SkillId, "skillId");
        ValidateVersions(request.ExpectedVersion, request.NewVersion);
    }

    public static void ValidateVersions(string expectedVersion, string newVersion)
    {
        if (!VersionPattern().IsMatch(expectedVersion) || !VersionPattern().IsMatch(newVersion) ||
            !Version.TryParse(expectedVersion, out var expected) || !Version.TryParse(newVersion, out var next) ||
            next.CompareTo(expected) <= 0)
        {
            throw new CatalogRecommendedSkillUpdateException(
                CatalogRecommendedSkillUpdateErrorCode.InvalidVersion,
                CatalogRecommendedSkillUpdateStage.ValidateVersion,
                "Versions must be canonical major.minor values and newVersion must be greater than expectedVersion.");
        }
    }

    private static void Required(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw InvalidRequest($"{name} is required.");
    }

    private static CatalogRecommendedSkillUpdateException InvalidRequest(string message) =>
        new(CatalogRecommendedSkillUpdateErrorCode.InvalidRequest,
            CatalogRecommendedSkillUpdateStage.ValidateVersion, message);

    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
