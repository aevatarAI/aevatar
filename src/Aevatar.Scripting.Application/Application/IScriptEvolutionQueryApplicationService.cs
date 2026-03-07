using Aevatar.Scripting.Abstractions.Queries;

namespace Aevatar.Scripting.Application;

public interface IScriptEvolutionQueryApplicationService
{
    Task<ScriptEvolutionProposalSnapshot?> GetProposalSnapshotAsync(
        string proposalId,
        CancellationToken ct = default);
}
