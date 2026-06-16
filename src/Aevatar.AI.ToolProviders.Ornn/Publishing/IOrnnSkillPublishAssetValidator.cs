namespace Aevatar.AI.ToolProviders.Ornn.Publishing;

public interface IOrnnSkillPublishAssetValidator
{
    string AssetKind { get; }

    Task<IReadOnlyList<OrnnSkillPublishDiagnostic>> ValidateAsync(
        OrnnSkillPublishRequest request,
        CancellationToken ct = default);
}
