namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

public interface INyxIdRecommendedSkillRefCreator
{
    Task<IReadOnlyList<NyxIdRecommendedSkillRef>> CreateRecommendedSkillRefsAsync(
        NyxIdServiceInstance instance,
        CancellationToken ct);
}

public sealed class EmptyNyxIdRecommendedSkillRefCreator : INyxIdRecommendedSkillRefCreator
{
    public static EmptyNyxIdRecommendedSkillRefCreator Instance { get; } = new();

    public Task<IReadOnlyList<NyxIdRecommendedSkillRef>> CreateRecommendedSkillRefsAsync(
        NyxIdServiceInstance instance,
        CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<NyxIdRecommendedSkillRef>>([]);
}
