namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

public interface INyxIdRecommendedSkillRefCreator
{
    Task<NyxIdRecommendedSkillRefCreationResult> CreateRecommendedSkillRefsAsync(
        NyxIdServiceInstance instance,
        string documentAccessToken,
        CancellationToken ct);
}

public sealed record NyxIdRecommendedSkillRefCreationResult(
    IReadOnlyList<NyxIdRecommendedSkillRef> Refs,
    IReadOnlyList<NyxIdCreatedRecommendedSkill> CreatedSkills,
    NyxIdRecommendedSkillRefPersistenceStatus PersistenceStatus,
    string PersistenceFailureCode)
{
    public static NyxIdRecommendedSkillRefCreationResult Empty(
        NyxIdRecommendedSkillRefPersistenceStatus status = NyxIdRecommendedSkillRefPersistenceStatus.EmptyInput,
        string failureCode = "") =>
        new([], [], status, failureCode);
}

public sealed record NyxIdCreatedRecommendedSkill(
    NyxIdRecommendedSkillRef Ref,
    string Name,
    string PublisherId,
    string MainDocument);

public sealed class EmptyNyxIdRecommendedSkillRefCreator : INyxIdRecommendedSkillRefCreator
{
    public static EmptyNyxIdRecommendedSkillRefCreator Instance { get; } = new();

    public Task<NyxIdRecommendedSkillRefCreationResult> CreateRecommendedSkillRefsAsync(
        NyxIdServiceInstance instance,
        string documentAccessToken,
        CancellationToken ct) =>
        Task.FromResult(NyxIdRecommendedSkillRefCreationResult.Empty());
}
