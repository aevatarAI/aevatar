using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Ports;
using System.Security.Cryptography;
using System.Text;

namespace Aevatar.Scripting.Application;

public sealed class ScriptEvolutionApplicationService : IScriptEvolutionApplicationService
{
    private const string DefaultManagerActorId = "script-evolution-manager";
    private const string DefaultCatalogActorId = "script-catalog";

    private readonly IScriptEvolutionPort _evolutionPort;

    public ScriptEvolutionApplicationService(IScriptEvolutionPort evolutionPort)
    {
        _evolutionPort = evolutionPort ?? throw new ArgumentNullException(nameof(evolutionPort));
    }

    public Task<ScriptPromotionDecision> ProposeAsync(
        ProposeScriptEvolutionRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ScriptId))
            throw new InvalidOperationException("ScriptId is required.");
        if (string.IsNullOrWhiteSpace(request.CandidateRevision))
            throw new InvalidOperationException("CandidateRevision is required.");
        if (string.IsNullOrWhiteSpace(request.CandidateSource))
            throw new InvalidOperationException("CandidateSource is required.");

        var normalizedScriptId = request.ScriptId;
        var normalizedProposalId = string.IsNullOrWhiteSpace(request.ProposalId)
            ? Guid.NewGuid().ToString("N")
            : request.ProposalId;
        var normalizedSourceHash = string.IsNullOrWhiteSpace(request.CandidateSourceHash)
            ? ComputeSourceHash(request.CandidateSource)
            : request.CandidateSourceHash;
        var definitionActorId = string.IsNullOrWhiteSpace(request.DefinitionActorId)
            ? $"script-definition:{normalizedScriptId}"
            : request.DefinitionActorId;
        var catalogActorId = string.IsNullOrWhiteSpace(request.CatalogActorId)
            ? DefaultCatalogActorId
            : request.CatalogActorId;
        var managerActorId = string.IsNullOrWhiteSpace(request.ManagerActorId)
            ? DefaultManagerActorId
            : request.ManagerActorId;

        var proposal = new ScriptEvolutionProposal(
            ProposalId: normalizedProposalId,
            ScriptId: normalizedScriptId,
            BaseRevision: request.BaseRevision ?? string.Empty,
            CandidateRevision: request.CandidateRevision,
            CandidateSource: request.CandidateSource,
            CandidateSourceHash: normalizedSourceHash,
            Reason: request.Reason ?? string.Empty,
            DefinitionActorId: definitionActorId,
            CatalogActorId: catalogActorId,
            RequestedByActorId: request.RequestedByActorId ?? string.Empty);

        return _evolutionPort.ProposeAsync(managerActorId, proposal, ct);
    }

    private static string ComputeSourceHash(string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source ?? string.Empty);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
