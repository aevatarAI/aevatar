using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Abstractions.Evolution;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptEvolutionLifecycleService
    : IScriptEvolutionProposalPort
{
    private readonly RuntimeScriptActorAccessor _actorAccessor;
    private readonly IScriptEvolutionProjectionLifecyclePort _projectionLifecyclePort;
    private readonly IScriptEvolutionDecisionFallbackPort _decisionFallbackPort;
    private readonly IScriptingActorAddressResolver _addressResolver;
    private readonly TimeSpan _decisionTimeout;
    private readonly StartScriptEvolutionSessionActorRequestAdapter _startSessionAdapter = new();

    public RuntimeScriptEvolutionLifecycleService(
        RuntimeScriptActorAccessor actorAccessor,
        IScriptEvolutionProjectionLifecyclePort projectionLifecyclePort,
        IScriptEvolutionDecisionFallbackPort decisionFallbackPort,
        IScriptingActorAddressResolver addressResolver,
        IScriptingPortTimeouts timeouts)
    {
        _actorAccessor = actorAccessor ?? throw new ArgumentNullException(nameof(actorAccessor));
        _projectionLifecyclePort = projectionLifecyclePort ?? throw new ArgumentNullException(nameof(projectionLifecyclePort));
        _decisionFallbackPort = decisionFallbackPort ?? throw new ArgumentNullException(nameof(decisionFallbackPort));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
        _decisionTimeout = (timeouts ?? throw new ArgumentNullException(nameof(timeouts)))
            .GetEvolutionDecisionTimeout();
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

        _ = await _actorAccessor.GetOrCreateAsync<ScriptEvolutionManagerGAgent>(
            managerActorId,
            "Script evolution manager actor not found",
            ct);
        var sessionActorId = _addressResolver.GetEvolutionSessionActorId(normalizedProposalId);
        var sessionActor = await _actorAccessor.GetOrCreateAsync<ScriptEvolutionSessionGAgent>(
            sessionActorId,
            "Script evolution session actor not found",
            ct);

        var sink = new EventChannel<ScriptEvolutionSessionCompletedEvent>(capacity: 256);
        var projectionLease = await _projectionLifecyclePort.EnsureAndAttachAsync(
            token => _projectionLifecyclePort.EnsureActorProjectionAsync(
                sessionActorId,
                normalizedProposalId,
                token),
            sink,
            ct);
        if (projectionLease == null)
            throw new InvalidOperationException("Script evolution projection is disabled.");

        try
        {
            var completed = await WaitForSessionCompletionAsync(
                normalizedProposalId,
                sink,
                () => sessionActor.HandleEventAsync(
                    _startSessionAdapter.Map(
                        new StartScriptEvolutionSessionActorRequest(
                            ProposalId: normalizedProposal.ProposalId ?? string.Empty,
                            ScriptId: normalizedProposal.ScriptId ?? string.Empty,
                            BaseRevision: normalizedProposal.BaseRevision ?? string.Empty,
                            CandidateRevision: normalizedProposal.CandidateRevision ?? string.Empty,
                            CandidateSource: normalizedProposal.CandidateSource ?? string.Empty,
                            CandidateSourceHash: normalizedProposal.CandidateSourceHash ?? string.Empty,
                            Reason: normalizedProposal.Reason ?? string.Empty),
                        sessionActorId),
                    ct),
                ct);

            return MapDecision(normalizedProposal, completed);
        }
        catch (TimeoutException)
        {
            var fallback = await _decisionFallbackPort.TryResolveAsync(managerActorId, normalizedProposalId, ct);
            if (fallback != null)
                return fallback;

            throw;
        }
        finally
        {
            await _projectionLifecyclePort.DetachReleaseAndDisposeAsync(
                projectionLease,
                sink,
                onDetachedAsync: null,
                CancellationToken.None);
        }
    }

    private async Task<ScriptEvolutionSessionCompletedEvent> WaitForSessionCompletionAsync(
        string proposalId,
        IEventSink<ScriptEvolutionSessionCompletedEvent> sink,
        Func<Task> dispatchAsync,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(dispatchAsync);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_decisionTimeout);

        await dispatchAsync();
        try
        {
            await foreach (var evt in sink.ReadAllAsync(timeoutCts.Token))
            {
                if (string.Equals(evt.ProposalId, proposalId, StringComparison.Ordinal))
                    return evt;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"Timeout waiting for script evolution session completion. proposal_id={proposalId}");
        }

        throw new TimeoutException($"Timeout waiting for script evolution session completion. proposal_id={proposalId}");
    }

    private static ScriptPromotionDecision MapDecision(
        ScriptEvolutionProposal proposal,
        ScriptEvolutionSessionCompletedEvent completed)
    {
        var validation = new ScriptEvolutionValidationReport(
            IsSuccess: completed.Accepted,
            Diagnostics: completed.Diagnostics.ToArray());
        return new ScriptPromotionDecision(
            Accepted: completed.Accepted,
            ProposalId: proposal.ProposalId ?? string.Empty,
            ScriptId: proposal.ScriptId ?? string.Empty,
            BaseRevision: proposal.BaseRevision ?? string.Empty,
            CandidateRevision: proposal.CandidateRevision ?? string.Empty,
            Status: completed.Status ?? string.Empty,
            FailureReason: completed.FailureReason ?? string.Empty,
            DefinitionActorId: completed.DefinitionActorId ?? string.Empty,
            CatalogActorId: completed.CatalogActorId ?? string.Empty,
            ValidationReport: validation);
    }
}
