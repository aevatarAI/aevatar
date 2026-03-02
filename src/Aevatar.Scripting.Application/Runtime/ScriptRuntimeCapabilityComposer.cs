using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.AI;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Runtime;

namespace Aevatar.Scripting.Application.Runtime;

public sealed class ScriptRuntimeCapabilityComposer : IScriptRuntimeCapabilityComposer
{
    private readonly IAICapability _aiCapability;
    private readonly IGAgentRuntimePort _agentRuntimePort;
    private readonly IScriptEvolutionPort _evolutionPort;
    private readonly IScriptDefinitionLifecyclePort _definitionLifecyclePort;
    private readonly IScriptRuntimeLifecyclePort _runtimeLifecyclePort;
    private readonly IScriptCatalogPort _catalogPort;
    private readonly IScriptingActorAddressResolver _addressResolver;

    public ScriptRuntimeCapabilityComposer(
        IAICapability aiCapability,
        IGAgentRuntimePort agentRuntimePort,
        IScriptEvolutionPort evolutionPort,
        IScriptDefinitionLifecyclePort definitionLifecyclePort,
        IScriptRuntimeLifecyclePort runtimeLifecyclePort,
        IScriptCatalogPort catalogPort,
        IScriptingActorAddressResolver addressResolver)
    {
        _aiCapability = aiCapability ?? throw new ArgumentNullException(nameof(aiCapability));
        _agentRuntimePort = agentRuntimePort ?? throw new ArgumentNullException(nameof(agentRuntimePort));
        _evolutionPort = evolutionPort ?? throw new ArgumentNullException(nameof(evolutionPort));
        _definitionLifecyclePort = definitionLifecyclePort ?? throw new ArgumentNullException(nameof(definitionLifecyclePort));
        _runtimeLifecyclePort = runtimeLifecyclePort ?? throw new ArgumentNullException(nameof(runtimeLifecyclePort));
        _catalogPort = catalogPort ?? throw new ArgumentNullException(nameof(catalogPort));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
    }

    public IScriptRuntimeCapabilities Compose(ScriptRuntimeCapabilityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var interaction = new ScriptInteractionCapabilities(
            context.RuntimeActorId,
            context.RunId,
            context.CorrelationId,
            _aiCapability,
            _agentRuntimePort);
        var agentLifecycle = new ScriptAgentLifecycleCapabilities(
            context.CorrelationId,
            _agentRuntimePort);
        var evolution = new ScriptEvolutionCapabilities(
            context,
            _evolutionPort,
            _definitionLifecyclePort,
            _runtimeLifecyclePort,
            _catalogPort,
            _addressResolver);

        return new ScriptRuntimeCapabilities(interaction, agentLifecycle, evolution);
    }
}
