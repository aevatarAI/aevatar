using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptEvolutionPort : IScriptEvolutionPort
{
    private readonly IActorRuntime _runtime;
    private readonly IStreamProvider _streams;
    private readonly IScriptingActorAddressResolver _addressResolver;
    private readonly TimeSpan _decisionTimeout;
    private readonly ProposeScriptEvolutionActorRequestAdapter _commandAdapter = new();
    private readonly QueryScriptEvolutionDecisionRequestAdapter _queryAdapter = new();

    public RuntimeScriptEvolutionPort(
        IActorRuntime runtime,
        IStreamProvider streams,
        IScriptingActorAddressResolver addressResolver,
        IScriptingPortTimeouts timeouts)
    {
        _runtime = runtime;
        _streams = streams;
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
        _decisionTimeout = NormalizeTimeout(timeouts.EvolutionDecisionTimeout);
    }

    public async Task<ScriptPromotionDecision> ProposeAsync(
        ScriptEvolutionProposal proposal,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var managerActorId = _addressResolver.GetEvolutionManagerActorId();
        var normalizedProposalId = string.IsNullOrWhiteSpace(proposal.ProposalId)
            ? Guid.NewGuid().ToString("N")
            : proposal.ProposalId;
        var normalizedProposal = proposal with { ProposalId = normalizedProposalId };

        var actor = await GetOrCreateManagerAsync(managerActorId, ct);

        await actor.HandleEventAsync(
            _commandAdapter.Map(
                new ProposeScriptEvolutionActorRequest(
                    ProposalId: normalizedProposal.ProposalId ?? string.Empty,
                    ScriptId: normalizedProposal.ScriptId ?? string.Empty,
                    BaseRevision: normalizedProposal.BaseRevision ?? string.Empty,
                    CandidateRevision: normalizedProposal.CandidateRevision ?? string.Empty,
                    CandidateSource: normalizedProposal.CandidateSource ?? string.Empty,
                    CandidateSourceHash: normalizedProposal.CandidateSourceHash ?? string.Empty,
                    Reason: normalizedProposal.Reason ?? string.Empty),
                managerActorId),
            ct);

        var response = await QueryDecisionAsync(managerActorId, normalizedProposalId, ct);
        if (!response.Found)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(response.FailureReason)
                    ? $"Script evolution decision not found for proposal `{normalizedProposalId}`."
                    : response.FailureReason);
        }

        if (!string.Equals(response.ProposalId, normalizedProposalId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Script evolution decision proposal mismatch. expected=`{normalizedProposalId}` actual=`{response.ProposalId}`.");
        }

        return MapDecision(response);
    }

    private async Task<ScriptEvolutionDecisionRespondedEvent> QueryDecisionAsync(
        string managerActorId,
        string proposalId,
        CancellationToken ct)
    {
        var actor = await _runtime.GetAsync(managerActorId)
            ?? throw new InvalidOperationException($"Script evolution manager actor not found: {managerActorId}");

        return await ScriptQueryReplyAwaiter.QueryAsync<ScriptEvolutionDecisionRespondedEvent>(
            _streams,
            "scripting.query.evolution.reply",
            _decisionTimeout,
            (requestId, replyStreamId) => actor.HandleEventAsync(
                _queryAdapter.Map(managerActorId, requestId, replyStreamId, proposalId),
                ct),
            static (reply, requestId) => string.Equals(reply.RequestId, requestId, StringComparison.Ordinal),
            static requestId => $"Timeout waiting for script evolution decision response. request_id={requestId}",
            ct);
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

    private static ScriptPromotionDecision MapDecision(ScriptEvolutionDecisionRespondedEvent response)
    {
        var validation = new ScriptEvolutionValidationReport(
            IsSuccess: response.Accepted,
            Diagnostics: response.Diagnostics.ToArray());
        return new ScriptPromotionDecision(
            Accepted: response.Accepted,
            ProposalId: response.ProposalId ?? string.Empty,
            ScriptId: response.ScriptId ?? string.Empty,
            BaseRevision: response.BaseRevision ?? string.Empty,
            CandidateRevision: response.CandidateRevision ?? string.Empty,
            Status: response.Status ?? string.Empty,
            FailureReason: response.FailureReason ?? string.Empty,
            DefinitionActorId: response.DefinitionActorId ?? string.Empty,
            CatalogActorId: response.CatalogActorId ?? string.Empty,
            ValidationReport: validation);
    }

    private static TimeSpan NormalizeTimeout(TimeSpan timeout) =>
        timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(45);
}
