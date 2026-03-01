using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Hosting.Ports;

public sealed class RuntimeScriptEvolutionPort : IScriptEvolutionPort
{
    private readonly IActorRuntime _runtime;
    private readonly ProposeScriptEvolutionCommandAdapter _commandAdapter = new();

    public RuntimeScriptEvolutionPort(IActorRuntime runtime)
    {
        _runtime = runtime;
    }

    public async Task<ScriptPromotionDecision> ProposeAsync(
        string managerActorId,
        ScriptEvolutionProposal proposal,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managerActorId);
        ArgumentNullException.ThrowIfNull(proposal);

        var normalizedProposalId = string.IsNullOrWhiteSpace(proposal.ProposalId)
            ? Guid.NewGuid().ToString("N")
            : proposal.ProposalId;
        var normalizedProposal = proposal with { ProposalId = normalizedProposalId };

        var actor = await GetOrCreateManagerAsync(managerActorId, ct);
        await actor.HandleEventAsync(
            _commandAdapter.Map(
                new ProposeScriptEvolutionCommand(
                    ProposalId: normalizedProposal.ProposalId ?? string.Empty,
                    ScriptId: normalizedProposal.ScriptId ?? string.Empty,
                    BaseRevision: normalizedProposal.BaseRevision ?? string.Empty,
                    CandidateRevision: normalizedProposal.CandidateRevision ?? string.Empty,
                    CandidateSource: normalizedProposal.CandidateSource ?? string.Empty,
                    CandidateSourceHash: normalizedProposal.CandidateSourceHash ?? string.Empty,
                    Reason: normalizedProposal.Reason ?? string.Empty,
                    DefinitionActorId: normalizedProposal.DefinitionActorId ?? string.Empty,
                    CatalogActorId: normalizedProposal.CatalogActorId ?? string.Empty,
                    RequestedByActorId: normalizedProposal.RequestedByActorId ?? string.Empty),
                managerActorId),
            ct);

        if (actor.Agent is not IScriptEvolutionDecisionSource source)
            throw new InvalidOperationException(
                $"Actor `{managerActorId}` does not implement IScriptEvolutionDecisionSource.");

        return source.GetDecision(normalizedProposalId)
            ?? throw new InvalidOperationException(
                $"Script evolution decision not found for proposal `{normalizedProposalId}`.");
    }

    private async Task<IActor> GetOrCreateManagerAsync(string managerActorId, CancellationToken ct)
    {
        if (await _runtime.ExistsAsync(managerActorId))
        {
            return await _runtime.GetAsync(managerActorId)
                ?? throw new InvalidOperationException($"Script evolution manager actor not found: {managerActorId}");
        }

        return await _runtime.CreateAsync<ScriptEvolutionManagerGAgent>(managerActorId, ct);
    }
}
