using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf.WellKnownTypes;
using System.Security.Cryptography;
using System.Text;

namespace Aevatar.Scripting.Core.Runtime;

public sealed class ScriptEvolutionCapabilities : IScriptEvolutionCapabilities
{
    private readonly ScriptRuntimeCapabilityContext _context;
    private readonly IScriptLifecyclePort _lifecyclePort;

    public ScriptEvolutionCapabilities(
        ScriptRuntimeCapabilityContext context,
        IScriptLifecyclePort lifecyclePort)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _lifecyclePort = lifecyclePort ?? throw new ArgumentNullException(nameof(lifecyclePort));
    }

    public Task<ScriptPromotionDecision> ProposeScriptEvolutionAsync(
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

        return _lifecyclePort.ProposeAsync(normalized, ct);
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

    public Task RunScriptInstanceAsync(
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
            ct);

    private static string ComputeSourceHash(string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source ?? string.Empty);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
