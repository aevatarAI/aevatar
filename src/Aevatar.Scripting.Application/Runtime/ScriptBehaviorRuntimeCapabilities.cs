using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.AI;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Application.Runtime;

public sealed class ScriptBehaviorRuntimeCapabilities : IScriptBehaviorRuntimeCapabilities
{
    private readonly Func<IMessage, TopologyAudience, CancellationToken, Task> _publishAsync;
    private readonly Func<string, IMessage, CancellationToken, Task> _sendToAsync;
    private readonly Func<IMessage, CancellationToken, Task> _publishToSelfAsync;
    private readonly Func<string, TimeSpan, IMessage, CancellationToken, Task<RuntimeCallbackLease>> _scheduleSelfSignalAsync;
    private readonly Func<RuntimeCallbackLease, CancellationToken, Task> _cancelCallbackAsync;
    private readonly IAICapability _aiCapability;
    private readonly IActorRuntime _runtime;
    private readonly IScriptDefinitionSnapshotPort _definitionSnapshotPort;
    private readonly IScriptEvolutionProposalPort _proposalPort;
    private readonly IScriptDefinitionCommandPort _definitionCommandPort;
    private readonly IScriptRuntimeProvisioningPort _runtimeProvisioningPort;
    private readonly IScriptRuntimeCommandPort _runtimeCommandPort;
    private readonly IScriptCatalogCommandPort _catalogCommandPort;
    // Activation-local snapshot cache for the current runtime capability instance.
    // This cache is non-durable and only short-circuits immediate follow-up calls
    // that already carry write-side authority facts in the same interaction.
    private readonly Dictionary<string, ScriptDefinitionSnapshot> _activationLocalDefinitionSnapshots =
        new(StringComparer.Ordinal);
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
        IActorRuntime runtime,
        IScriptDefinitionSnapshotPort definitionSnapshotPort,
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
            runtime,
            definitionSnapshotPort,
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
        IActorRuntime runtime,
        IScriptDefinitionSnapshotPort definitionSnapshotPort,
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
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _definitionSnapshotPort = definitionSnapshotPort ?? throw new ArgumentNullException(nameof(definitionSnapshotPort));
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

    public async Task<string> CreateAgentAsync(
        string agentTypeAssemblyQualifiedName,
        string? actorId,
        CancellationToken ct)
    {
        var agentType = System.Type.GetType(agentTypeAssemblyQualifiedName, throwOnError: true)
            ?? throw new InvalidOperationException($"Agent type `{agentTypeAssemblyQualifiedName}` could not be resolved.");
        var actor = await _runtime.CreateAsync(agentType, actorId, ct);
        return actor.Id;
    }

    public Task DestroyAgentAsync(string actorId, CancellationToken ct) =>
        _runtime.DestroyAsync(actorId, ct);

    public Task LinkAgentsAsync(string parentActorId, string childActorId, CancellationToken ct) =>
        _runtime.LinkAsync(parentActorId, childActorId, ct);

    public Task UnlinkAgentAsync(string childActorId, CancellationToken ct) =>
        _runtime.UnlinkAsync(childActorId, ct);

    public Task<ScriptPromotionDecision> ProposeScriptEvolutionAsync(
        ScriptEvolutionProposal proposal,
        CancellationToken ct) =>
        ProposeAndRememberAsync(proposal, ct);

    public Task<string> UpsertScriptDefinitionAsync(
        string scriptId,
        string scriptRevision,
        string sourceText,
        string sourceHash,
        string? definitionActorId,
        CancellationToken ct) =>
        UpsertAndRememberAsync(scriptId, scriptRevision, sourceText, sourceHash, definitionActorId, ct);

    public async Task<string> SpawnScriptRuntimeAsync(
        string definitionActorId,
        string scriptRevision,
        string? runtimeActorId,
        CancellationToken ct)
    {
        var definitionSnapshot = await ResolveDefinitionSnapshotAsync(definitionActorId, scriptRevision, ct);
        var resolvedRuntimeActorId = await _runtimeProvisioningPort.EnsureRuntimeAsync(
            definitionActorId,
            scriptRevision,
            runtimeActorId,
            definitionSnapshot,
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

    private async Task<ScriptPromotionDecision> ProposeAndRememberAsync(
        ScriptEvolutionProposal proposal,
        CancellationToken ct)
    {
        var decision = await _proposalPort.ProposeAsync(proposal, ct);
        if (decision.Accepted && decision.DefinitionSnapshot != null)
        {
            RememberTransientDefinitionSnapshot(
                decision.DefinitionActorId,
                decision.CandidateRevision,
                decision.DefinitionSnapshot.ToSnapshot());
        }

        return decision;
    }

    private async Task<string> UpsertAndRememberAsync(
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
            sourceText,
            sourceHash,
            definitionActorId,
            _scopeId,
            ct);
        RememberTransientDefinitionSnapshot(result.ActorId, result.Snapshot.Revision, result.Snapshot);
        return result.ActorId;
    }

    private void RememberTransientDefinitionSnapshot(
        string definitionActorId,
        string scriptRevision,
        ScriptDefinitionSnapshot? snapshot)
    {
        // This cache is bounded to the capability lifetime and must not be treated
        // as cross-request or cross-node authority state.
        if (snapshot == null || string.IsNullOrWhiteSpace(definitionActorId))
            return;

        _activationLocalDefinitionSnapshots[BuildDefinitionSnapshotKey(definitionActorId, scriptRevision)] = snapshot;
    }

    private ScriptDefinitionSnapshot? TryGetTransientDefinitionSnapshot(
        string definitionActorId,
        string scriptRevision)
    {
        _activationLocalDefinitionSnapshots.TryGetValue(
            BuildDefinitionSnapshotKey(definitionActorId, scriptRevision),
            out var snapshot);
        return snapshot;
    }

    private static string BuildDefinitionSnapshotKey(
        string definitionActorId,
        string scriptRevision) =>
        string.Concat(
            definitionActorId ?? string.Empty,
            "::",
            string.IsNullOrWhiteSpace(scriptRevision) ? "latest" : scriptRevision);

    private async Task<ScriptDefinitionSnapshot> ResolveDefinitionSnapshotAsync(
        string definitionActorId,
        string scriptRevision,
        CancellationToken ct)
    {
        var snapshot = TryGetTransientDefinitionSnapshot(definitionActorId, scriptRevision);
        if (snapshot != null)
            return snapshot;

        snapshot = await _definitionSnapshotPort.GetRequiredAsync(definitionActorId, scriptRevision, ct);
        RememberTransientDefinitionSnapshot(definitionActorId, snapshot.Revision, snapshot);
        return snapshot;
    }
}
