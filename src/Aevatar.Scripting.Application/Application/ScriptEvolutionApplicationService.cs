using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Ports;
using System.Security.Cryptography;
using System.Text;

namespace Aevatar.Scripting.Application;

public sealed class ScriptEvolutionApplicationService : IScriptEvolutionApplicationService
{
    private readonly IScriptEvolutionProposalPort _proposalPort;

    public ScriptEvolutionApplicationService(
        IScriptEvolutionProposalPort proposalPort)
    {
        _proposalPort = proposalPort ?? throw new ArgumentNullException(nameof(proposalPort));
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

        var normalizedScopeId = request.ScopeId?.Trim() ?? string.Empty;
        var normalizedScriptId = request.ScriptId;
        var normalizedProposalId = string.IsNullOrWhiteSpace(request.ProposalId)
            ? Guid.NewGuid().ToString("N")
            : request.ProposalId.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedScopeId) &&
            !normalizedProposalId.StartsWith($"{normalizedScopeId}:", StringComparison.Ordinal))
        {
            normalizedProposalId = $"{normalizedScopeId}:{normalizedProposalId}";
        }
        var normalizedSourceHash = string.IsNullOrWhiteSpace(request.CandidateSourceHash)
            ? ComputeSourceHash(request.CandidateSource)
            : request.CandidateSourceHash;

        var proposal = new ScriptEvolutionProposal(
            ProposalId: normalizedProposalId,
            ScriptId: normalizedScriptId,
            BaseRevision: request.BaseRevision ?? string.Empty,
            CandidateRevision: request.CandidateRevision,
            CandidateSource: request.CandidateSource,
            CandidateSourceHash: normalizedSourceHash,
            Reason: request.Reason ?? string.Empty,
            ScopeId: normalizedScopeId);

        return _proposalPort.ProposeAsync(proposal, ct);
    }

    private static string ComputeSourceHash(string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source ?? string.Empty);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
