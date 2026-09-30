namespace Aevatar.AI.Abstractions.Skills;

// Caller credentials are per invocation, never retained in service state or protobuf data.
public interface ISkillServiceDiscoverySource
{
    Task<SkillServiceDiscoveryInput> ReadAsync(
        string accessToken, string skillName, CancellationToken ct = default);
}

public interface ISkillServiceRecommendationService
{
    Task<SkillServiceRecommendations> RecommendAsync(
        string accessToken, string skillName, CancellationToken ct = default);
}

public sealed class SkillServiceDiscoveryException : Exception
{
    public SkillServiceDiscoveryException()
        : base("Skill service discovery is unavailable.") { }
}
