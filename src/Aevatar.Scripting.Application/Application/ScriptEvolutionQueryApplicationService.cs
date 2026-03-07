using Aevatar.Scripting.Abstractions.Queries;

namespace Aevatar.Scripting.Application;

public sealed class ScriptEvolutionQueryApplicationService : IScriptEvolutionQueryApplicationService
{
    private readonly IScriptEvolutionProjectionQueryPort _queryPort;

    public ScriptEvolutionQueryApplicationService(IScriptEvolutionProjectionQueryPort queryPort)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
    }

    public Task<ScriptEvolutionProposalSnapshot?> GetProposalSnapshotAsync(
        string proposalId,
        CancellationToken ct = default) =>
        _queryPort.GetProposalSnapshotAsync(proposalId, ct);
}
