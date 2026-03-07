using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Abstractions.Evolution;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf.WellKnownTypes;
using System.Security.Cryptography;
using System.Text;

namespace Aevatar.Scripting.Core.Runtime;

public sealed class ScriptEvolutionCapabilities : IScriptEvolutionCapabilities
{
    private readonly ScriptRuntimeCapabilityContext _context;
    private readonly IScriptLifecyclePort _lifecyclePort;
    private readonly IScriptEvolutionProjectionLifecyclePort _projectionLifecyclePort;
    private readonly IScriptEvolutionProjectionQueryPort _queryPort;
    private readonly IScriptingActorAddressResolver _addressResolver;

    public ScriptEvolutionCapabilities(
        ScriptRuntimeCapabilityContext context,
        IScriptLifecyclePort lifecyclePort,
        IScriptEvolutionProjectionLifecyclePort projectionLifecyclePort,
        IScriptEvolutionProjectionQueryPort queryPort,
        IScriptingActorAddressResolver addressResolver)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _lifecyclePort = lifecyclePort ?? throw new ArgumentNullException(nameof(lifecyclePort));
        _projectionLifecyclePort = projectionLifecyclePort ?? throw new ArgumentNullException(nameof(projectionLifecyclePort));
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
    }

    public async Task<ScriptEvolutionDecision> ProposeScriptEvolutionAsync(
        ScriptEvolutionProposal proposal,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var normalized = proposal with
        {
            ProposalId = string.IsNullOrWhiteSpace(proposal.ProposalId)
                ? Guid.NewGuid().ToString("N")
                : proposal.ProposalId,
            ScriptId = string.IsNullOrWhiteSpace(proposal.ScriptId)
                ? _context.ScriptId
                : proposal.ScriptId,
            BaseRevision = string.IsNullOrWhiteSpace(proposal.BaseRevision)
                ? _context.CurrentRevision
                : proposal.BaseRevision,
            CandidateSourceHash = string.IsNullOrWhiteSpace(proposal.CandidateSourceHash)
                ? ComputeSourceHash(proposal.CandidateSource)
                : proposal.CandidateSourceHash,
        };

        var proposalId = normalized.ProposalId ?? string.Empty;
        var sessionActorId = _addressResolver.GetEvolutionSessionActorId(proposalId);
        var sink = new EventChannel<ScriptEvolutionSessionCompletedEvent>();
        IScriptEvolutionProjectionLease? lease = null;
        var sinkAttached = false;

        try
        {
            if (_projectionLifecyclePort.ProjectionEnabled)
            {
                lease = await _projectionLifecyclePort.EnsureAndAttachAsync(
                    token => _projectionLifecyclePort.EnsureActorProjectionAsync(sessionActorId, proposalId, token),
                    sink,
                    ct);
                sinkAttached = lease != null;
            }

            var accepted = await _lifecyclePort.ProposeAsync(normalized, ct);
            var terminalEvent = sinkAttached
                ? await ReadSingleAsync(sink, ct)
                : null;
            var snapshot = await _queryPort.GetProposalSnapshotAsync(accepted.ProposalId, ct);

            if (snapshot == null)
            {
                throw new InvalidOperationException(
                    $"Script evolution snapshot not found after completion. proposal_id={accepted.ProposalId}");
            }

            if (!snapshot.Completed && terminalEvent == null)
            {
                throw new InvalidOperationException(
                    $"Script evolution did not reach terminal state. proposal_id={accepted.ProposalId}");
            }

            return new ScriptEvolutionDecision(
                accepted.ProposalId,
                snapshot.ScriptId,
                accepted.SessionActorId,
                snapshot.Accepted,
                ResolveStatus(snapshot),
                snapshot.FailureReason,
                snapshot.DefinitionActorId,
                snapshot.CandidateRevision,
                snapshot.CatalogActorId);
        }
        finally
        {
            if (sinkAttached)
            {
                await _projectionLifecyclePort.DetachReleaseAndDisposeAsync(
                    lease,
                    sink,
                    null,
                    CancellationToken.None);
            }
            else
            {
                sink.Complete();
                await sink.DisposeAsync();
            }
        }
    }

    public Task<string> UpsertScriptDefinitionAsync(
        string scriptId,
        string scriptRevision,
        string sourceText,
        string sourceHash,
        string? definitionActorId,
        CancellationToken ct) =>
        _lifecyclePort.UpsertDefinitionAsync(
            scriptId,
            scriptRevision,
            sourceText,
            sourceHash,
            definitionActorId,
            ct);

    public Task<string> SpawnScriptRuntimeAsync(
        string definitionActorId,
        string scriptRevision,
        string? runtimeActorId,
        CancellationToken ct) =>
        _lifecyclePort.SpawnRuntimeAsync(definitionActorId, scriptRevision, runtimeActorId, ct);

    public Task<ScriptRuntimeRunAccepted> RunScriptInstanceAsync(
        string runtimeActorId,
        string runId,
        Any? inputPayload,
        string scriptRevision,
        string definitionActorId,
        string requestedEventType,
        CancellationToken ct) =>
        _lifecyclePort.RunRuntimeAsync(
            runtimeActorId,
            runId,
            inputPayload,
            scriptRevision,
            definitionActorId,
            requestedEventType,
            ct);

    public Task PromoteRevisionAsync(
        string catalogActorId,
        string scriptId,
        string revision,
        string definitionActorId,
        string sourceHash,
        string proposalId,
        CancellationToken ct) =>
        _lifecyclePort.PromoteCatalogRevisionAsync(
            string.IsNullOrWhiteSpace(catalogActorId) ? null : catalogActorId,
            scriptId,
            string.Empty,
            revision,
            definitionActorId,
            sourceHash,
            proposalId,
            ct);

    public Task RollbackRevisionAsync(
        string catalogActorId,
        string scriptId,
        string targetRevision,
        string reason,
        string proposalId,
        CancellationToken ct) =>
        _lifecyclePort.RollbackCatalogRevisionAsync(
            string.IsNullOrWhiteSpace(catalogActorId) ? null : catalogActorId,
            scriptId,
            targetRevision,
            reason,
            proposalId,
            string.Empty,
            ct);

    private static string ComputeSourceHash(string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source ?? string.Empty);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static async Task<ScriptEvolutionSessionCompletedEvent> ReadSingleAsync(
        IEventSink<ScriptEvolutionSessionCompletedEvent> sink,
        CancellationToken ct)
    {
        await foreach (var evt in sink.ReadAllAsync(ct))
            return evt;

        throw new InvalidOperationException("Script evolution live delivery ended before a terminal event was observed.");
    }

    private static string ResolveStatus(ScriptEvolutionProposalSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.PromotionStatus))
            return snapshot.PromotionStatus;

        if (!string.IsNullOrWhiteSpace(snapshot.ValidationStatus))
            return snapshot.ValidationStatus;

        return snapshot.Completed
            ? (snapshot.Accepted ? ScriptEvolutionStatuses.Promoted : ScriptEvolutionStatuses.Rejected)
            : ScriptEvolutionStatuses.Pending;
    }
}
