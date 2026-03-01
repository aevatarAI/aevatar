using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf.WellKnownTypes;
using System.Security.Cryptography;
using System.Text;

namespace Aevatar.Scripting.Core.Runtime;

public sealed class ScriptEvolutionCapabilities : IScriptEvolutionCapabilities
{
    private readonly ScriptRuntimeCapabilityContext _context;
    private readonly IScriptEvolutionPort _evolutionPort;
    private readonly IScriptDefinitionLifecyclePort _definitionLifecyclePort;
    private readonly IScriptRuntimeLifecyclePort _runtimeLifecyclePort;
    private readonly IScriptCatalogPort _catalogPort;
    private readonly IScriptingActorAddressResolver _addressResolver;

    public ScriptEvolutionCapabilities(
        ScriptRuntimeCapabilityContext context,
        IScriptEvolutionPort evolutionPort,
        IScriptDefinitionLifecyclePort definitionLifecyclePort,
        IScriptRuntimeLifecyclePort runtimeLifecyclePort,
        IScriptCatalogPort catalogPort,
        IScriptingActorAddressResolver addressResolver)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _evolutionPort = evolutionPort ?? throw new ArgumentNullException(nameof(evolutionPort));
        _definitionLifecyclePort = definitionLifecyclePort ?? throw new ArgumentNullException(nameof(definitionLifecyclePort));
        _runtimeLifecyclePort = runtimeLifecyclePort ?? throw new ArgumentNullException(nameof(runtimeLifecyclePort));
        _catalogPort = catalogPort ?? throw new ArgumentNullException(nameof(catalogPort));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
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
            DefinitionActorId = string.IsNullOrWhiteSpace(proposal.DefinitionActorId)
                ? _context.DefinitionActorId
                : proposal.DefinitionActorId,
            CatalogActorId = string.IsNullOrWhiteSpace(proposal.CatalogActorId)
                ? _addressResolver.GetCatalogActorId()
                : proposal.CatalogActorId,
            RequestedByActorId = string.IsNullOrWhiteSpace(proposal.RequestedByActorId)
                ? _context.RuntimeActorId
                : proposal.RequestedByActorId,
        };

        return _evolutionPort.ProposeAsync(_addressResolver.GetEvolutionManagerActorId(), normalized, ct);
    }

    public Task<string> UpsertScriptDefinitionAsync(
        string scriptId,
        string scriptRevision,
        string sourceText,
        string sourceHash,
        string? definitionActorId,
        CancellationToken ct) =>
        _definitionLifecyclePort.UpsertAsync(
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
        _runtimeLifecyclePort.SpawnAsync(definitionActorId, scriptRevision, runtimeActorId, ct);

    public Task RunScriptInstanceAsync(
        string runtimeActorId,
        string runId,
        Any? inputPayload,
        string scriptRevision,
        string definitionActorId,
        string requestedEventType,
        CancellationToken ct) =>
        _runtimeLifecyclePort.RunAsync(
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
        _catalogPort.PromoteAsync(
            string.IsNullOrWhiteSpace(catalogActorId) ? _addressResolver.GetCatalogActorId() : catalogActorId,
            scriptId,
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
        _catalogPort.RollbackAsync(
            string.IsNullOrWhiteSpace(catalogActorId) ? _addressResolver.GetCatalogActorId() : catalogActorId,
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
