using Aevatar.Foundation.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Evolution;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptLifecyclePort : IScriptLifecyclePort
{
    private readonly IActorRuntime _runtime;
    private readonly IStreamProvider _streams;
    private readonly IScriptEvolutionProjectionLifecyclePort _projectionLifecyclePort;
    private readonly IScriptEvolutionDecisionFallbackPort _decisionFallbackPort;
    private readonly IScriptingActorAddressResolver _addressResolver;
    private readonly TimeSpan _decisionTimeout;
    private readonly TimeSpan _catalogQueryTimeout;

    private readonly StartScriptEvolutionSessionActorRequestAdapter _startSessionAdapter = new();
    private readonly UpsertScriptDefinitionActorRequestAdapter _upsertDefinitionAdapter = new();
    private readonly RunScriptActorRequestAdapter _runScriptAdapter = new();
    private readonly PromoteScriptRevisionActorRequestAdapter _promoteRevisionAdapter = new();
    private readonly RollbackScriptRevisionActorRequestAdapter _rollbackRevisionAdapter = new();
    private readonly QueryScriptCatalogEntryRequestAdapter _queryCatalogEntryAdapter = new();

    public RuntimeScriptLifecyclePort(
        IActorRuntime runtime,
        IStreamProvider streams,
        IScriptEvolutionProjectionLifecyclePort projectionLifecyclePort,
        IScriptEvolutionDecisionFallbackPort decisionFallbackPort,
        IScriptingActorAddressResolver addressResolver,
        IScriptingPortTimeouts timeouts)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _streams = streams ?? throw new ArgumentNullException(nameof(streams));
        _projectionLifecyclePort = projectionLifecyclePort ?? throw new ArgumentNullException(nameof(projectionLifecyclePort));
        _decisionFallbackPort = decisionFallbackPort ?? throw new ArgumentNullException(nameof(decisionFallbackPort));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
        _decisionTimeout = NormalizeTimeout(timeouts.EvolutionDecisionTimeout);
        _catalogQueryTimeout = NormalizeTimeout(timeouts.CatalogEntryQueryTimeout);
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

        _ = await GetOrCreateManagerAsync(managerActorId, ct);
        var sessionActorId = _addressResolver.GetEvolutionSessionActorId(normalizedProposalId);
        var sessionActor = await GetOrCreateSessionActorAsync(sessionActorId, ct);
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

    public async Task<string> UpsertDefinitionAsync(
        string scriptId,
        string scriptRevision,
        string sourceText,
        string sourceHash,
        string? definitionActorId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceText);

        var actorId = string.IsNullOrWhiteSpace(definitionActorId)
            ? _addressResolver.GetDefinitionActorId(scriptId)
            : definitionActorId;

        var actor = await GetOrCreateActorAsync<ScriptDefinitionGAgent>(
            actorId,
            "Script definition actor not found",
            ct);

        await actor.HandleEventAsync(
            _upsertDefinitionAdapter.Map(
                new UpsertScriptDefinitionActorRequest(
                    ScriptId: scriptId,
                    ScriptRevision: scriptRevision,
                    SourceText: sourceText,
                    SourceHash: sourceHash ?? string.Empty),
                actorId),
            ct);

        return actorId;
    }

    public async Task<string> SpawnRuntimeAsync(
        string definitionActorId,
        string scriptRevision,
        string? runtimeActorId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionActorId);

        var normalizedRevision = string.IsNullOrWhiteSpace(scriptRevision)
            ? "latest"
            : scriptRevision;
        var actorId = string.IsNullOrWhiteSpace(runtimeActorId)
            ? $"script-runtime:{definitionActorId}:{normalizedRevision}:{Guid.NewGuid():N}"
            : runtimeActorId;

        if (await _runtime.ExistsAsync(actorId))
            return actorId;

        _ = await _runtime.CreateAsync<ScriptRuntimeGAgent>(actorId, ct);
        return actorId;
    }

    public async Task RunRuntimeAsync(
        string runtimeActorId,
        string runId,
        Any? inputPayload,
        string scriptRevision,
        string definitionActorId,
        string requestedEventType,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var runtimeActor = await _runtime.GetAsync(runtimeActorId)
            ?? throw new InvalidOperationException($"Script runtime actor not found: {runtimeActorId}");

        await runtimeActor.HandleEventAsync(
            _runScriptAdapter.Map(
                new RunScriptActorRequest(
                    RunId: runId,
                    InputPayload: inputPayload?.Clone(),
                    ScriptRevision: scriptRevision ?? string.Empty,
                    DefinitionActorId: definitionActorId ?? string.Empty,
                    RequestedEventType: requestedEventType ?? string.Empty),
                runtimeActorId),
            ct);
    }

    public async Task PromoteCatalogRevisionAsync(
        string? catalogActorId,
        string scriptId,
        string expectedBaseRevision,
        string revision,
        string definitionActorId,
        string sourceHash,
        string proposalId,
        CancellationToken ct)
    {
        var resolvedCatalogActorId = ResolveCatalogActorId(catalogActorId);
        var actor = await GetOrCreateCatalogActorAsync(resolvedCatalogActorId, ct);
        await actor.HandleEventAsync(
            _promoteRevisionAdapter.Map(
                new PromoteScriptRevisionActorRequest(
                    ScriptId: scriptId ?? string.Empty,
                    Revision: revision ?? string.Empty,
                    DefinitionActorId: definitionActorId ?? string.Empty,
                    SourceHash: sourceHash ?? string.Empty,
                    ProposalId: proposalId ?? string.Empty,
                    ExpectedBaseRevision: expectedBaseRevision ?? string.Empty),
                resolvedCatalogActorId),
            ct);
    }

    public async Task RollbackCatalogRevisionAsync(
        string? catalogActorId,
        string scriptId,
        string targetRevision,
        string reason,
        string proposalId,
        string expectedCurrentRevision,
        CancellationToken ct)
    {
        var resolvedCatalogActorId = ResolveCatalogActorId(catalogActorId);
        var actor = await GetOrCreateCatalogActorAsync(resolvedCatalogActorId, ct);
        await actor.HandleEventAsync(
            _rollbackRevisionAdapter.Map(
                new RollbackScriptRevisionActorRequest(
                    ScriptId: scriptId ?? string.Empty,
                    TargetRevision: targetRevision ?? string.Empty,
                    Reason: reason ?? string.Empty,
                    ProposalId: proposalId ?? string.Empty,
                    ExpectedCurrentRevision: expectedCurrentRevision ?? string.Empty),
                resolvedCatalogActorId),
            ct);
    }

    public async Task<ScriptCatalogEntrySnapshot?> GetCatalogEntryAsync(
        string? catalogActorId,
        string scriptId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scriptId))
            return null;

        var resolvedCatalogActorId = ResolveCatalogActorId(catalogActorId);
        var actor = await _runtime.GetAsync(resolvedCatalogActorId);
        if (actor == null)
            return null;

        var response = await ScriptQueryReplyAwaiter.QueryAsync<ScriptCatalogEntryRespondedEvent>(
            _streams,
            "scripting.query.catalog.reply",
            _catalogQueryTimeout,
            (requestId, replyStreamId) => actor.HandleEventAsync(
                _queryCatalogEntryAdapter.Map(resolvedCatalogActorId, requestId, replyStreamId, scriptId),
                ct),
            static (reply, requestId) => string.Equals(reply.RequestId, requestId, StringComparison.Ordinal),
            static requestId => $"Timeout waiting for script catalog entry query response. request_id={requestId}",
            ct);
        if (!response.Found)
            return null;

        return new ScriptCatalogEntrySnapshot(
            ScriptId: response.ScriptId ?? string.Empty,
            ActiveRevision: response.ActiveRevision ?? string.Empty,
            ActiveDefinitionActorId: response.ActiveDefinitionActorId ?? string.Empty,
            ActiveSourceHash: response.ActiveSourceHash ?? string.Empty,
            PreviousRevision: response.PreviousRevision ?? string.Empty,
            RevisionHistory: response.RevisionHistory.ToArray(),
            LastProposalId: response.LastProposalId ?? string.Empty);
    }

    private Task<IActor> GetOrCreateManagerAsync(string managerActorId, CancellationToken ct) =>
        GetOrCreateActorAsync<ScriptEvolutionManagerGAgent>(
            managerActorId,
            "Script evolution manager actor not found",
            ct);

    private Task<IActor> GetOrCreateSessionActorAsync(string sessionActorId, CancellationToken ct) =>
        GetOrCreateActorAsync<ScriptEvolutionSessionGAgent>(
            sessionActorId,
            "Script evolution session actor not found",
            ct);

    private Task<IActor> GetOrCreateCatalogActorAsync(string catalogActorId, CancellationToken ct) =>
        GetOrCreateActorAsync<ScriptCatalogGAgent>(
            catalogActorId,
            "Script catalog actor not found",
            ct);

    private async Task<IActor> GetOrCreateActorAsync<TAgent>(
        string actorId,
        string notFoundMessage,
        CancellationToken ct)
        where TAgent : IAgent
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(notFoundMessage);

        if (!await _runtime.ExistsAsync(actorId))
            return await _runtime.CreateAsync<TAgent>(actorId, ct);

        return await _runtime.GetAsync(actorId)
            ?? throw new InvalidOperationException($"{notFoundMessage}: {actorId}");
    }

    private string ResolveCatalogActorId(string? catalogActorId) =>
        string.IsNullOrWhiteSpace(catalogActorId)
            ? _addressResolver.GetCatalogActorId()
            : catalogActorId;

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

    private static TimeSpan NormalizeTimeout(TimeSpan timeout) =>
        timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(45);
}
