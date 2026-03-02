using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Hosting.Ports;

public sealed class RuntimeScriptEvolutionPort : IScriptEvolutionPort
{
    private static readonly TimeSpan DecisionTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DecisionRoundQueryTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DecisionProbeInterval = TimeSpan.FromMilliseconds(100);
    private const string StatusPromoted = "promoted";
    private const string StatusRejected = "rejected";

    private readonly IActorRuntime _runtime;
    private readonly IStreamProvider _streams;
    private readonly ProposeScriptEvolutionCommandAdapter _commandAdapter = new();
    private readonly QueryScriptEvolutionDecisionRequestAdapter _queryAdapter = new();

    public RuntimeScriptEvolutionPort(
        IActorRuntime runtime,
        IStreamProvider streams)
    {
        _runtime = runtime;
        _streams = streams;
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

        var response = await WaitForDecisionByEventAsync(actor, managerActorId, normalizedProposalId, ct);
        if (response == null)
            throw new InvalidOperationException(
                $"Script evolution decision not found for proposal `{normalizedProposalId}`.");

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

    private async Task<ScriptEvolutionDecisionRespondedEvent?> WaitForDecisionByEventAsync(
        IActor managerActor,
        string managerActorId,
        string proposalId,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + DecisionTimeout;
        ScriptEvolutionDecisionRespondedEvent? latestFound = null;

        while (DateTime.UtcNow <= deadline)
        {
            ct.ThrowIfCancellationRequested();

            var response = await TryQueryDecisionAsync(
                managerActor,
                managerActorId,
                proposalId,
                ct,
                DecisionRoundQueryTimeout);
            if (response?.Found == true)
            {
                latestFound = response;
                if (string.Equals(response.Status, StatusPromoted, StringComparison.Ordinal) ||
                    string.Equals(response.Status, StatusRejected, StringComparison.Ordinal))
                {
                    return response;
                }
            }

            await Task.Delay(DecisionProbeInterval, ct);
        }

        return latestFound;
    }

    private async Task<ScriptEvolutionDecisionRespondedEvent?> TryQueryDecisionAsync(
        IActor managerActor,
        string managerActorId,
        string proposalId,
        CancellationToken ct,
        TimeSpan roundTimeout)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var replyStreamId = $"scripting.query.evolution.reply:{requestId}";
        var responseTaskSource = new TaskCompletionSource<ScriptEvolutionDecisionRespondedEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await _streams
            .GetStream(replyStreamId)
            .SubscribeAsync<ScriptEvolutionDecisionRespondedEvent>(response =>
            {
                if (string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
                    responseTaskSource.TrySetResult(response);

                return Task.CompletedTask;
            }, ct);

        await managerActor.HandleEventAsync(
            _queryAdapter.Map(managerActorId, requestId, replyStreamId, proposalId),
            ct);

        var response = await WaitForResponseAsync(responseTaskSource.Task, requestId, ct, roundTimeout);
        if (response == null)
            return null;

        if (!string.Equals(response.ProposalId, proposalId, StringComparison.Ordinal))
            return null;

        return response;
    }

    private static ScriptPromotionDecision MapDecision(ScriptEvolutionDecisionRespondedEvent response)
    {
        var validation = new ScriptEvolutionValidationReport(
            IsSuccess: string.Equals(response.Status, StatusPromoted, StringComparison.Ordinal),
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

    private static async Task<ScriptEvolutionDecisionRespondedEvent?> WaitForResponseAsync(
        Task<ScriptEvolutionDecisionRespondedEvent> responseTask,
        string requestId,
        CancellationToken ct,
        TimeSpan timeout)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeoutTask = Task.Delay(timeout, timeoutCts.Token);
        var completed = await Task.WhenAny(responseTask, timeoutTask);
        if (!ReferenceEquals(completed, responseTask))
            return null;

        timeoutCts.Cancel();
        var response = await responseTask;
        if (!string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
            return null;

        return response;
    }
}
