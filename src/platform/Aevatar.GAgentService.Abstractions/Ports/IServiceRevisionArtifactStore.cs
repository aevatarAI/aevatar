namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IServiceRevisionArtifactStore
{
    Task SaveAsync(
        string serviceKey,
        string revisionId,
        PreparedServiceRevisionArtifact artifact,
        CancellationToken ct = default);

    Task<PreparedServiceRevisionArtifact?> GetAsync(
        string serviceKey,
        string revisionId,
        CancellationToken ct = default);
}
