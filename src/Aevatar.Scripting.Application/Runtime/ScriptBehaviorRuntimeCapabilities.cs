using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.AI;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Application.Runtime;

// Refactor (iter27/cluster-029-scripting-runtime-raw-actor-lifecycle):
//   Old pattern: Scripting behavior runtime exposes raw IActorRuntime lifecycle/topology by assembly-qualified type name and caller-supplied actor ids
//   New principle: Delete raw script-facing actor lifecycle/topology API; keep existing typed scripting ports (provisioning/command/definition/catalog/evolution)
// Refactor (iter113/cluster-113-scripting-runtime-definition-snapshot-side-read):
//   Old pattern: Scripting runtime side-read scripting definition snapshot via runtime readmodel (cache + factory injection).
//   New principle: Direct command-owned ScriptDefinitionSnapshot;delete runtime readmodel side-read/cache/factory injection;migrate script-facing API as in-scope migration risk(public API break in scope).
public sealed class ScriptBehaviorRuntimeCapabilities : IScriptBehaviorRuntimeCapabilities
{
    private readonly Func<IMessage, TopologyAudience, CancellationToken, Task> _publishAsync;
    private readonly Func<string, IMessage, CancellationToken, Task> _sendToAsync;
    private readonly Func<IMessage, CancellationToken, Task> _publishToSelfAsync;
    private readonly Func<string, TimeSpan, IMessage, CancellationToken, Task<RuntimeCallbackLease>> _scheduleSelfSignalAsync;
    private readonly Func<RuntimeCallbackLease, CancellationToken, Task> _cancelCallbackAsync;
    private readonly IAICapability _aiCapability;
    private readonly IScriptEvolutionProposalPort _proposalPort;
    private readonly IScriptDefinitionCommandPort _definitionCommandPort;
    private readonly IScriptRuntimeProvisioningPort _runtimeProvisioningPort;
    private readonly IScriptRuntimeCommandPort _runtimeCommandPort;
    private readonly IScriptCatalogCommandPort _catalogCommandPort;
    private readonly string _scopeId;
    private readonly string _runId;
    private readonly string _correlationId;

    public ScriptBehaviorRuntimeCapabilities(
        string runId,
        string correlationId,
        Func<IMessage, TopologyAudience, CancellationToken, Task> publishAsync,
        Func<string, IMessage, CancellationToken, Task> sendToAsync,
        Func<IMessage, CancellationToken, Task> publishToSelfAsync,
        Func<string, TimeSpan, IMessage, CancellationToken, Task<RuntimeCallbackLease>> scheduleSelfSignalAsync,
        Func<RuntimeCallbackLease, CancellationToken, Task> cancelCallbackAsync,
        IAICapability aiCapability,
        IScriptEvolutionProposalPort proposalPort,
        IScriptDefinitionCommandPort definitionCommandPort,
        IScriptRuntimeProvisioningPort runtimeProvisioningPort,
        IScriptRuntimeCommandPort runtimeCommandPort,
        IScriptCatalogCommandPort catalogCommandPort)
        : this(
            scopeId: string.Empty,
            runId,
            correlationId,
            publishAsync,
            sendToAsync,
            publishToSelfAsync,
            scheduleSelfSignalAsync,
            cancelCallbackAsync,
            aiCapability,
            proposalPort,
            definitionCommandPort,
            runtimeProvisioningPort,
            runtimeCommandPort,
            catalogCommandPort)
    {
    }

    public ScriptBehaviorRuntimeCapabilities(
        string scopeId,
        string runId,
        string correlationId,
        Func<IMessage, TopologyAudience, CancellationToken, Task> publishAsync,
        Func<string, IMessage, CancellationToken, Task> sendToAsync,
        Func<IMessage, CancellationToken, Task> publishToSelfAsync,
        Func<string, TimeSpan, IMessage, CancellationToken, Task<RuntimeCallbackLease>> scheduleSelfSignalAsync,
        Func<RuntimeCallbackLease, CancellationToken, Task> cancelCallbackAsync,
        IAICapability aiCapability,
        IScriptEvolutionProposalPort proposalPort,
        IScriptDefinitionCommandPort definitionCommandPort,
        IScriptRuntimeProvisioningPort runtimeProvisioningPort,
        IScriptRuntimeCommandPort runtimeCommandPort,
        IScriptCatalogCommandPort catalogCommandPort)
    {
        _scopeId = scopeId?.Trim() ?? string.Empty;
        _runId = runId ?? string.Empty;
        _correlationId = correlationId ?? string.Empty;
        _publishAsync = publishAsync ?? throw new ArgumentNullException(nameof(publishAsync));
        _sendToAsync = sendToAsync ?? throw new ArgumentNullException(nameof(sendToAsync));
        _publishToSelfAsync = publishToSelfAsync ?? throw new ArgumentNullException(nameof(publishToSelfAsync));
        _scheduleSelfSignalAsync = scheduleSelfSignalAsync ?? throw new ArgumentNullException(nameof(scheduleSelfSignalAsync));
        _cancelCallbackAsync = cancelCallbackAsync ?? throw new ArgumentNullException(nameof(cancelCallbackAsync));
        _aiCapability = aiCapability ?? throw new ArgumentNullException(nameof(aiCapability));
        _proposalPort = proposalPort ?? throw new ArgumentNullException(nameof(proposalPort));
        _definitionCommandPort = definitionCommandPort ?? throw new ArgumentNullException(nameof(definitionCommandPort));
        _runtimeProvisioningPort = runtimeProvisioningPort ?? throw new ArgumentNullException(nameof(runtimeProvisioningPort));
        _runtimeCommandPort = runtimeCommandPort ?? throw new ArgumentNullException(nameof(runtimeCommandPort));
        _catalogCommandPort = catalogCommandPort ?? throw new ArgumentNullException(nameof(catalogCommandPort));
    }

    public Task<string> AskAIAsync(string prompt, CancellationToken ct) =>
        _aiCapability.AskAsync(_runId, _correlationId, prompt, ct);

    public Task PublishAsync(IMessage eventPayload, TopologyAudience audience, CancellationToken ct) =>
        _publishAsync(eventPayload, audience, ct);

    public Task SendToAsync(string targetActorId, IMessage eventPayload, CancellationToken ct) =>
        _sendToAsync(targetActorId, eventPayload, ct);

    public Task PublishToSelfAsync(IMessage eventPayload, CancellationToken ct) =>
        _publishToSelfAsync(eventPayload, ct);

    public Task<RuntimeCallbackLease> ScheduleSelfDurableSignalAsync(
        string callbackId,
        TimeSpan dueTime,
        IMessage eventPayload,
        CancellationToken ct) =>
        _scheduleSelfSignalAsync(callbackId, dueTime, eventPayload, ct);

    public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct) =>
        _cancelCallbackAsync(lease, ct);

    public Task<ScriptPromotionDecision> ProposeScriptEvolutionAsync(
        ScriptEvolutionProposal proposal,
        CancellationToken ct) =>
        _proposalPort.ProposeAsync(proposal, ct);

    public async Task<Aevatar.Scripting.Abstractions.Behaviors.ScriptDefinitionUpsertResult> UpsertScriptDefinitionAsync(
        string scriptId,
        string scriptRevision,
        string sourceText,
        string sourceHash,
        string? definitionActorId,
        CancellationToken ct)
    {
        var result = await _definitionCommandPort.UpsertDefinitionWithSnapshotAsync(
            scriptId,
            scriptRevision,
            ScriptPackageSpecExtensions.CreateSingleSource(sourceText ?? string.Empty),
            definitionActorId,
            _scopeId,
            ct);
        return new Aevatar.Scripting.Abstractions.Behaviors.ScriptDefinitionUpsertResult(
            result.ActorId,
            result.Snapshot.ToBindingSpec());
    }

    public async Task<string> SpawnScriptRuntimeAsync(
        string definitionActorId,
        string scriptRevision,
        string? runtimeActorId,
        ScriptDefinitionBindingSpec definitionSnapshot,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definitionSnapshot);
        var snapshot = definitionSnapshot.ToSnapshot()
            ?? throw new InvalidOperationException("Script runtime provisioning requires a definition snapshot.");
        var resolvedRuntimeActorId = await _runtimeProvisioningPort.EnsureRuntimeAsync(
            definitionActorId,
            scriptRevision,
            runtimeActorId,
            snapshot,
            _scopeId,
            ct);
        return resolvedRuntimeActorId;
    }

    public async Task RunScriptInstanceAsync(
        string runtimeActorId,
        string runId,
        Any? inputPayload,
        string scriptRevision,
        string definitionActorId,
        string requestedEventType,
        CancellationToken ct)
    {
        await _runtimeCommandPort.RunRuntimeAsync(
            runtimeActorId,
            runId,
            inputPayload,
            scriptRevision,
            definitionActorId,
            requestedEventType,
            _scopeId,
            ct);
    }

    public Task PromoteRevisionAsync(
        string catalogActorId,
        string scriptId,
        string revision,
        string definitionActorId,
        string sourceHash,
        string proposalId,
        CancellationToken ct) =>
        _catalogCommandPort.PromoteCatalogRevisionAsync(
            string.IsNullOrWhiteSpace(catalogActorId) ? null : catalogActorId,
            scriptId,
            string.Empty,
            revision,
            definitionActorId,
            sourceHash,
            proposalId,
            _scopeId,
            ct);

    public Task RollbackRevisionAsync(
        string catalogActorId,
        string scriptId,
        string targetRevision,
        string reason,
        string proposalId,
        CancellationToken ct) =>
        _catalogCommandPort.RollbackCatalogRevisionAsync(
            string.IsNullOrWhiteSpace(catalogActorId) ? null : catalogActorId,
            scriptId,
            targetRevision,
            reason,
            proposalId,
            string.Empty,
            _scopeId,
            ct);

}
