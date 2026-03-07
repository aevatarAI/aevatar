using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptLifecyclePort : IScriptLifecyclePort
{
    private readonly RuntimeScriptEvolutionLifecycleService _evolutionLifecycleService;
    private readonly RuntimeScriptDefinitionLifecycleService _definitionLifecycleService;
    private readonly RuntimeScriptExecutionLifecycleService _executionLifecycleService;
    private readonly RuntimeScriptCatalogLifecycleService _catalogLifecycleService;

    public RuntimeScriptLifecyclePort(
        RuntimeScriptEvolutionLifecycleService evolutionLifecycleService,
        RuntimeScriptDefinitionLifecycleService definitionLifecycleService,
        RuntimeScriptExecutionLifecycleService executionLifecycleService,
        RuntimeScriptCatalogLifecycleService catalogLifecycleService)
    {
        _evolutionLifecycleService = evolutionLifecycleService ?? throw new ArgumentNullException(nameof(evolutionLifecycleService));
        _definitionLifecycleService = definitionLifecycleService ?? throw new ArgumentNullException(nameof(definitionLifecycleService));
        _executionLifecycleService = executionLifecycleService ?? throw new ArgumentNullException(nameof(executionLifecycleService));
        _catalogLifecycleService = catalogLifecycleService ?? throw new ArgumentNullException(nameof(catalogLifecycleService));
    }

    public Task<ScriptEvolutionCommandAccepted> ProposeAsync(
        ScriptEvolutionProposal proposal,
        CancellationToken ct) =>
        _evolutionLifecycleService.ProposeAsync(proposal, ct);

    public Task<string> UpsertDefinitionAsync(
        string scriptId,
        string scriptRevision,
        string sourceText,
        string sourceHash,
        string? definitionActorId,
        CancellationToken ct) =>
        _definitionLifecycleService.UpsertDefinitionAsync(
            scriptId,
            scriptRevision,
            sourceText,
            sourceHash,
            definitionActorId,
            ct);

    public Task<string> SpawnRuntimeAsync(
        string definitionActorId,
        string scriptRevision,
        string? runtimeActorId,
        CancellationToken ct) =>
        _executionLifecycleService.SpawnRuntimeAsync(
            definitionActorId,
            scriptRevision,
            runtimeActorId,
            ct);

    public Task<ScriptRuntimeRunAccepted> RunRuntimeAsync(
        string runtimeActorId,
        string runId,
        Any? inputPayload,
        string scriptRevision,
        string definitionActorId,
        string requestedEventType,
        CancellationToken ct) =>
        _executionLifecycleService.RunRuntimeAsync(
            runtimeActorId,
            runId,
            inputPayload,
            scriptRevision,
            definitionActorId,
            requestedEventType,
            ct);

    public Task PromoteCatalogRevisionAsync(
        string? catalogActorId,
        string scriptId,
        string expectedBaseRevision,
        string revision,
        string definitionActorId,
        string sourceHash,
        string proposalId,
        CancellationToken ct) =>
        _catalogLifecycleService.PromoteCatalogRevisionAsync(
            catalogActorId,
            scriptId,
            expectedBaseRevision,
            revision,
            definitionActorId,
            sourceHash,
            proposalId,
            ct);

    public Task RollbackCatalogRevisionAsync(
        string? catalogActorId,
        string scriptId,
        string targetRevision,
        string reason,
        string proposalId,
        string expectedCurrentRevision,
        CancellationToken ct) =>
        _catalogLifecycleService.RollbackCatalogRevisionAsync(
            catalogActorId,
            scriptId,
            targetRevision,
            reason,
            proposalId,
            expectedCurrentRevision,
            ct);

    public Task<ScriptCatalogEntrySnapshot?> GetCatalogEntryAsync(
        string? catalogActorId,
        string scriptId,
        CancellationToken ct) =>
        _catalogLifecycleService.GetCatalogEntryAsync(
            catalogActorId,
            scriptId,
            ct);
}
