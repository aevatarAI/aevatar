using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Hosting.Ports;

public sealed class RuntimeScriptEvolutionPort : IScriptEvolutionPort
{
    private readonly IActorRuntime _runtime;
    private readonly IStreamProvider _streams;
    private readonly TimeSpan _decisionTimeout;
    private readonly ProposeScriptEvolutionCommandAdapter _commandAdapter = new();

    public RuntimeScriptEvolutionPort(
        IActorRuntime runtime,
        IStreamProvider streams,
        IScriptingPortTimeouts timeouts)
    {
        _runtime = runtime;
        _streams = streams;
        _decisionTimeout = NormalizeTimeout(timeouts.EvolutionDecisionTimeout);
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
        var decisionRequestId = Guid.NewGuid().ToString("N");
        var decisionReplyStreamId = $"scripting.evolution.decision.reply:{decisionRequestId}";
        var responseTaskSource = new TaskCompletionSource<ScriptEvolutionDecisionRespondedEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await _streams
            .GetStream(decisionReplyStreamId)
            .SubscribeAsync<ScriptEvolutionDecisionRespondedEvent>(response =>
            {
                if (string.Equals(response.RequestId, decisionRequestId, StringComparison.Ordinal))
                    responseTaskSource.TrySetResult(response);

                return Task.CompletedTask;
            }, ct);

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
                    RequestedByActorId: normalizedProposal.RequestedByActorId ?? string.Empty,
                    DecisionRequestId: decisionRequestId,
                    DecisionReplyStreamId: decisionReplyStreamId),
                managerActorId),
            ct);

        var response = await WaitForDecisionResponseAsync(responseTaskSource.Task, decisionRequestId, ct);
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

    private async Task<ScriptEvolutionDecisionRespondedEvent> WaitForDecisionResponseAsync(
        Task<ScriptEvolutionDecisionRespondedEvent> responseTask,
        string decisionRequestId,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeoutTask = Task.Delay(_decisionTimeout, timeoutCts.Token);
        var completed = await Task.WhenAny(responseTask, timeoutTask);
        if (!ReferenceEquals(completed, responseTask))
            throw new TimeoutException($"Timeout waiting for script evolution decision response. request_id={decisionRequestId}");

        timeoutCts.Cancel();
        var response = await responseTask;
        if (!string.Equals(response.RequestId, decisionRequestId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Script evolution decision response request mismatch. expected=`{decisionRequestId}` actual=`{response.RequestId}`.");
        }

        return response;
    }

    private static TimeSpan NormalizeTimeout(TimeSpan timeout) =>
        timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(45);
}
