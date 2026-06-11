using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Core.AI;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Runtime;
using Google.Protobuf;

namespace Aevatar.Scripting.Application.Runtime;

// Refactor (iter27/cluster-029-scripting-runtime-raw-actor-lifecycle):
//   Old pattern: Scripting behavior runtime exposes raw IActorRuntime lifecycle/topology by assembly-qualified type name and caller-supplied actor ids
//   New principle: Delete raw script-facing actor lifecycle/topology API; keep existing typed scripting ports (provisioning/command/definition/catalog/evolution)
// Refactor (iter113/cluster-113-scripting-runtime-definition-snapshot-side-read):
//   Old pattern: Scripting runtime side-read scripting definition snapshot via runtime readmodel (cache + factory injection).
//   New principle: Direct command-owned ScriptDefinitionSnapshot;delete runtime readmodel side-read/cache/factory injection;migrate script-facing API as in-scope migration risk(public API break in scope).
public sealed class ScriptBehaviorRuntimeCapabilityFactory : IScriptBehaviorRuntimeCapabilityFactory
{
    private readonly IAICapability _aiCapability;
    private readonly IScriptEvolutionProposalPort _proposalPort;
    private readonly IScriptDefinitionCommandPort _definitionCommandPort;
    private readonly IScriptRuntimeProvisioningPort _runtimeProvisioningPort;
    private readonly IScriptRuntimeCommandPort _runtimeCommandPort;
    private readonly IScriptCatalogCommandPort _catalogCommandPort;

    public ScriptBehaviorRuntimeCapabilityFactory(
        IAICapability aiCapability,
        IScriptEvolutionProposalPort proposalPort,
        IScriptDefinitionCommandPort definitionCommandPort,
        IScriptRuntimeProvisioningPort runtimeProvisioningPort,
        IScriptRuntimeCommandPort runtimeCommandPort,
        IScriptCatalogCommandPort catalogCommandPort)
    {
        _aiCapability = aiCapability ?? throw new ArgumentNullException(nameof(aiCapability));
        _proposalPort = proposalPort ?? throw new ArgumentNullException(nameof(proposalPort));
        _definitionCommandPort = definitionCommandPort ?? throw new ArgumentNullException(nameof(definitionCommandPort));
        _runtimeProvisioningPort = runtimeProvisioningPort ?? throw new ArgumentNullException(nameof(runtimeProvisioningPort));
        _runtimeCommandPort = runtimeCommandPort ?? throw new ArgumentNullException(nameof(runtimeCommandPort));
        _catalogCommandPort = catalogCommandPort ?? throw new ArgumentNullException(nameof(catalogCommandPort));
    }

    public IScriptBehaviorRuntimeCapabilities Create(
        ScriptBehaviorRuntimeCapabilityContext context,
        Func<IMessage, TopologyAudience, CancellationToken, Task> publishAsync,
        Func<string, IMessage, CancellationToken, Task> sendToAsync,
        Func<IMessage, CancellationToken, Task> publishToSelfAsync,
        Func<string, TimeSpan, IMessage, CancellationToken, Task<RuntimeCallbackLease>> scheduleSelfSignalAsync,
        Func<RuntimeCallbackLease, CancellationToken, Task> cancelCallbackAsync)
    {
        _ = context;

        return new ScriptBehaviorRuntimeCapabilities(
            context.ScopeId,
            context.RunId,
            context.CorrelationId,
            publishAsync,
            sendToAsync,
            publishToSelfAsync,
            scheduleSelfSignalAsync,
            cancelCallbackAsync,
            _aiCapability,
            _proposalPort,
            _definitionCommandPort,
            _runtimeProvisioningPort,
            _runtimeCommandPort,
            _catalogCommandPort);
    }
}
