using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Hosting.Ports;

public sealed class RuntimeScriptPolicyGatePort : IScriptPolicyGatePort
{
    public Task<ScriptPolicyGateDecision> EvaluateAsync(
        ScriptEvolutionProposal proposal,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(proposal.ScriptId))
            return Task.FromResult(ScriptPolicyGateDecision.Deny("ScriptId is required."));
        if (string.IsNullOrWhiteSpace(proposal.CandidateRevision))
            return Task.FromResult(ScriptPolicyGateDecision.Deny("CandidateRevision is required."));
        if (string.IsNullOrWhiteSpace(proposal.CandidateSource))
            return Task.FromResult(ScriptPolicyGateDecision.Deny("CandidateSource is required."));
        if (string.IsNullOrWhiteSpace(proposal.CandidateSourceHash))
            return Task.FromResult(ScriptPolicyGateDecision.Deny("CandidateSourceHash is required."));
        if (!string.IsNullOrWhiteSpace(proposal.BaseRevision) &&
            string.Equals(proposal.BaseRevision, proposal.CandidateRevision, StringComparison.Ordinal))
        {
            return Task.FromResult(ScriptPolicyGateDecision.Deny(
                "CandidateRevision must differ from BaseRevision."));
        }

        return Task.FromResult(ScriptPolicyGateDecision.Allow);
    }
}
