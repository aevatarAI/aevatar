using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.AI;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Runtime;

namespace Aevatar.Scripting.Application.Runtime;

public sealed class ScriptRuntimeCapabilityComposer : IScriptRuntimeCapabilityComposer
{
    private readonly IAICapability _aiCapability;
    private readonly IActorRuntime _runtime;
    private readonly IScriptEvolutionProposalPort _proposalPort;
    private readonly IScriptDefinitionCommandPort _definitionCommandPort;
    private readonly IScriptRuntimeProvisioningPort _runtimeProvisioningPort;
    private readonly IScriptRuntimeCommandPort _runtimeCommandPort;
    private readonly IScriptCatalogCommandPort _catalogCommandPort;

    public ScriptRuntimeCapabilityComposer(
        IAICapability aiCapability,
        IActorRuntime runtime,
        IScriptEvolutionProposalPort proposalPort,
        IScriptDefinitionCommandPort definitionCommandPort,
        IScriptRuntimeProvisioningPort runtimeProvisioningPort,
        IScriptRuntimeCommandPort runtimeCommandPort,
        IScriptCatalogCommandPort catalogCommandPort)
    {
        _aiCapability = aiCapability ?? throw new ArgumentNullException(nameof(aiCapability));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _proposalPort = proposalPort ?? throw new ArgumentNullException(nameof(proposalPort));
        _definitionCommandPort = definitionCommandPort ?? throw new ArgumentNullException(nameof(definitionCommandPort));
        _runtimeProvisioningPort = runtimeProvisioningPort ?? throw new ArgumentNullException(nameof(runtimeProvisioningPort));
        _runtimeCommandPort = runtimeCommandPort ?? throw new ArgumentNullException(nameof(runtimeCommandPort));
        _catalogCommandPort = catalogCommandPort ?? throw new ArgumentNullException(nameof(catalogCommandPort));
    }

    public IScriptRuntimeCapabilities Compose(ScriptRuntimeCapabilityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var interaction = new ScriptInteractionCapabilities(
            context.RunId,
            context.CorrelationId,
            _aiCapability,
            context.MessageContext);
        var agentLifecycle = new ScriptAgentLifecycleCapabilities(_runtime);
        var evolution = new ScriptEvolutionCapabilities(
            context,
            _proposalPort,
            _definitionCommandPort,
            _runtimeProvisioningPort,
            _runtimeCommandPort,
            _catalogCommandPort);

        return new ScriptRuntimeCapabilities(interaction, agentLifecycle, evolution);
    }
}
