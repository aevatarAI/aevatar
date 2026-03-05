using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.AI;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Runtime;

namespace Aevatar.Scripting.Application.Runtime;

public sealed class ScriptRuntimeCapabilityComposer : IScriptRuntimeCapabilityComposer
{
    private readonly IAICapability _aiCapability;
    private readonly IGAgentRuntimePort _agentRuntimePort;
    private readonly IScriptLifecyclePort _lifecyclePort;

    public ScriptRuntimeCapabilityComposer(
        IAICapability aiCapability,
        IGAgentRuntimePort agentRuntimePort,
        IScriptLifecyclePort lifecyclePort)
    {
        _aiCapability = aiCapability ?? throw new ArgumentNullException(nameof(aiCapability));
        _agentRuntimePort = agentRuntimePort ?? throw new ArgumentNullException(nameof(agentRuntimePort));
        _lifecyclePort = lifecyclePort ?? throw new ArgumentNullException(nameof(lifecyclePort));
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
            _lifecyclePort);

        return new ScriptRuntimeCapabilities(interaction, agentLifecycle, evolution);
    }
}
