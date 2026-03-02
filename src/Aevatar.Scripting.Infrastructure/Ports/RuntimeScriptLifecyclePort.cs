using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
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
    private readonly IScriptingActorAddressResolver _addressResolver;
    private readonly TimeSpan _decisionTimeout;
    private readonly TimeSpan _catalogQueryTimeout;

    private readonly ProposeScriptEvolutionActorRequestAdapter _proposeAdapter = new();
    private readonly QueryScriptEvolutionDecisionRequestAdapter _queryDecisionAdapter = new();
    private readonly UpsertScriptDefinitionActorRequestAdapter _upsertDefinitionAdapter = new();
    private readonly RunScriptActorRequestAdapter _runScriptAdapter = new();
    private readonly PromoteScriptRevisionActorRequestAdapter _promoteRevisionAdapter = new();
    private readonly RollbackScriptRevisionActorRequestAdapter _rollbackRevisionAdapter = new();
    private readonly QueryScriptCatalogEntryRequestAdapter _queryCatalogEntryAdapter = new();

    public RuntimeScriptLifecyclePort(
        IActorRuntime runtime,
        IStreamProvider streams,
        IScriptingActorAddressResolver addressResolver,
        IScriptingPortTimeouts timeouts)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _streams = streams ?? throw new ArgumentNullException(nameof(streams));
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

        var actor = await GetOrCreateManagerAsync(managerActorId, ct);

        await actor.HandleEventAsync(
            _proposeAdapter.Map(
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

        IActor actor;
        if (await _runtime.ExistsAsync(actorId))
        {
            actor = await _runtime.GetAsync(actorId)
                ?? throw new InvalidOperationException($"Script definition actor not found: {actorId}");
        }
        else
        {
            actor = await _runtime.CreateAsync<ScriptDefinitionGAgent>(actorId, ct);
        }

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
                    ProposalId: proposalId ?? string.Empty),
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
                _queryDecisionAdapter.Map(managerActorId, requestId, replyStreamId, proposalId),
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

    private async Task<IActor> GetOrCreateCatalogActorAsync(string catalogActorId, CancellationToken ct)
    {
        if (await _runtime.ExistsAsync(catalogActorId))
        {
            return await _runtime.GetAsync(catalogActorId)
                ?? throw new InvalidOperationException($"Script catalog actor not found: {catalogActorId}");
        }

        return await _runtime.CreateAsync<ScriptCatalogGAgent>(catalogActorId, ct);
    }

    private string ResolveCatalogActorId(string? catalogActorId) =>
        string.IsNullOrWhiteSpace(catalogActorId)
            ? _addressResolver.GetCatalogActorId()
            : catalogActorId;

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
