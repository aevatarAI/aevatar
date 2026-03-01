using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.AI;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Runtime;

namespace Aevatar.Scripting.Application.Runtime;

public sealed class ScriptRuntimeCapabilityComposer : IScriptRuntimeCapabilityComposer
{
    private readonly IAICapability _aiCapability;
    private readonly IGAgentEventRoutingPort _eventRoutingPort;
    private readonly IGAgentInvocationPort _invocationPort;
    private readonly IGAgentFactoryPort _factoryPort;
    private readonly IScriptEvolutionPort _evolutionPort;
    private readonly IScriptDefinitionLifecyclePort _definitionLifecyclePort;
    private readonly IScriptRuntimeLifecyclePort _runtimeLifecyclePort;
    private readonly IScriptCatalogPort _catalogPort;

    public ScriptRuntimeCapabilityComposer(
        IAICapability aiCapability,
        IGAgentEventRoutingPort eventRoutingPort,
        IGAgentInvocationPort invocationPort,
        IGAgentFactoryPort factoryPort,
        IScriptEvolutionPort evolutionPort,
        IScriptDefinitionLifecyclePort definitionLifecyclePort,
        IScriptRuntimeLifecyclePort runtimeLifecyclePort,
        IScriptCatalogPort catalogPort)
    {
        _aiCapability = aiCapability ?? throw new ArgumentNullException(nameof(aiCapability));
        _eventRoutingPort = eventRoutingPort ?? throw new ArgumentNullException(nameof(eventRoutingPort));
        _invocationPort = invocationPort ?? throw new ArgumentNullException(nameof(invocationPort));
        _factoryPort = factoryPort ?? throw new ArgumentNullException(nameof(factoryPort));
        _evolutionPort = evolutionPort ?? throw new ArgumentNullException(nameof(evolutionPort));
        _definitionLifecyclePort = definitionLifecyclePort ?? throw new ArgumentNullException(nameof(definitionLifecyclePort));
        _runtimeLifecyclePort = runtimeLifecyclePort ?? throw new ArgumentNullException(nameof(runtimeLifecyclePort));
        _catalogPort = catalogPort ?? throw new ArgumentNullException(nameof(catalogPort));
    }

    public IScriptRuntimeCapabilities Compose(ScriptRuntimeCapabilityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var interaction = new ScriptInteractionCapabilities(
            context.RuntimeActorId,
            context.RunId,
            context.CorrelationId,
            _aiCapability,
            _eventRoutingPort);
        var agentLifecycle = new ScriptAgentLifecycleCapabilities(
            context.RuntimeActorId,
            context.CorrelationId,
            _invocationPort,
            _factoryPort);
        var evolution = new ScriptEvolutionCapabilities(
            context,
            _evolutionPort,
            _definitionLifecyclePort,
            _runtimeLifecyclePort,
            _catalogPort);

        return new ScriptRuntimeCapabilities(interaction, agentLifecycle, evolution);
    }
}
