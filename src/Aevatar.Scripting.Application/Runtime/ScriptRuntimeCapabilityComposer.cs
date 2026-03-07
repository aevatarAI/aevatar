using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Abstractions.Evolution;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Core.AI;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Runtime;

namespace Aevatar.Scripting.Application.Runtime;

public sealed class ScriptRuntimeCapabilityComposer : IScriptRuntimeCapabilityComposer
{
    private readonly IAICapability _aiCapability;
    private readonly IGAgentRuntimePort _agentRuntimePort;
    private readonly IScriptLifecyclePort _lifecyclePort;
    private readonly IScriptEvolutionProjectionLifecyclePort _projectionLifecyclePort;
    private readonly IScriptEvolutionProjectionQueryPort _evolutionQueryPort;
    private readonly IScriptingActorAddressResolver _addressResolver;

    public ScriptRuntimeCapabilityComposer(
        IAICapability aiCapability,
        IGAgentRuntimePort agentRuntimePort,
        IScriptLifecyclePort lifecyclePort,
        IScriptEvolutionProjectionLifecyclePort projectionLifecyclePort,
        IScriptEvolutionProjectionQueryPort evolutionQueryPort,
        IScriptingActorAddressResolver addressResolver)
    {
        _aiCapability = aiCapability ?? throw new ArgumentNullException(nameof(aiCapability));
        _agentRuntimePort = agentRuntimePort ?? throw new ArgumentNullException(nameof(agentRuntimePort));
        _lifecyclePort = lifecyclePort ?? throw new ArgumentNullException(nameof(lifecyclePort));
        _projectionLifecyclePort = projectionLifecyclePort ?? throw new ArgumentNullException(nameof(projectionLifecyclePort));
        _evolutionQueryPort = evolutionQueryPort ?? throw new ArgumentNullException(nameof(evolutionQueryPort));
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
            _lifecyclePort,
            _projectionLifecyclePort,
            _evolutionQueryPort,
            _addressResolver);

        return new ScriptRuntimeCapabilities(interaction, agentLifecycle, evolution);
    }
}
