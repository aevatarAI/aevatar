using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.AI;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Scripting.Core.Runtime;

public sealed class ScriptRuntimeCapabilities : IScriptRuntimeCapabilities
{
    private readonly string _runtimeActorId;
    private readonly string _runId;
    private readonly string _correlationId;
    private readonly IServiceProvider _services;

    public ScriptRuntimeCapabilities(
        string runtimeActorId,
        string runId,
        string correlationId,
        IServiceProvider services)
    {
        _runtimeActorId = runtimeActorId ?? string.Empty;
        _runId = runId ?? string.Empty;
        _correlationId = correlationId ?? string.Empty;
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public Task<string> AskAIAsync(string prompt, CancellationToken ct)
    {
        var aiCapability = _services.GetService<IAICapability>();
        if (aiCapability == null)
            throw new InvalidOperationException("IAICapability is not registered for script runtime.");

        return aiCapability.AskAsync(_runId, _correlationId, prompt ?? string.Empty, ct);
    }

    public Task PublishAsync(IMessage eventPayload, EventDirection direction, CancellationToken ct)
    {
        var eventRoutingPort = _services.GetService<IGAgentEventRoutingPort>();
        if (eventRoutingPort == null)
            throw new InvalidOperationException("IGAgentEventRoutingPort is not registered for script runtime.");

        return eventRoutingPort.PublishAsync(_runtimeActorId, eventPayload, direction, _correlationId, ct);
    }

    public Task SendToAsync(string targetActorId, IMessage eventPayload, CancellationToken ct)
    {
        var eventRoutingPort = _services.GetService<IGAgentEventRoutingPort>();
        if (eventRoutingPort == null)
            throw new InvalidOperationException("IGAgentEventRoutingPort is not registered for script runtime.");

        return eventRoutingPort.SendToAsync(_runtimeActorId, targetActorId, eventPayload, _correlationId, ct);
    }

    public Task InvokeAgentAsync(string targetAgentId, IMessage eventPayload, CancellationToken ct)
    {
        var invocationPort = _services.GetService<IGAgentInvocationPort>();
        if (invocationPort == null)
            throw new InvalidOperationException("IGAgentInvocationPort is not registered for script runtime.");

        return invocationPort.InvokeAsync(targetAgentId, eventPayload, _correlationId, ct);
    }

    public Task<string> CreateAgentAsync(string agentTypeAssemblyQualifiedName, string? actorId, CancellationToken ct)
    {
        var factoryPort = _services.GetService<IGAgentFactoryPort>();
        if (factoryPort == null)
            throw new InvalidOperationException("IGAgentFactoryPort is not registered for script runtime.");

        return factoryPort.CreateAsync(agentTypeAssemblyQualifiedName, actorId, ct);
    }

    public Task DestroyAgentAsync(string actorId, CancellationToken ct)
    {
        var factoryPort = _services.GetService<IGAgentFactoryPort>();
        if (factoryPort == null)
            throw new InvalidOperationException("IGAgentFactoryPort is not registered for script runtime.");

        return factoryPort.DestroyAsync(actorId, ct);
    }

    public Task LinkAgentsAsync(string parentActorId, string childActorId, CancellationToken ct)
    {
        var factoryPort = _services.GetService<IGAgentFactoryPort>();
        if (factoryPort == null)
            throw new InvalidOperationException("IGAgentFactoryPort is not registered for script runtime.");

        return factoryPort.LinkAsync(parentActorId, childActorId, ct);
    }

    public Task UnlinkAgentAsync(string childActorId, CancellationToken ct)
    {
        var factoryPort = _services.GetService<IGAgentFactoryPort>();
        if (factoryPort == null)
            throw new InvalidOperationException("IGAgentFactoryPort is not registered for script runtime.");

        return factoryPort.UnlinkAsync(childActorId, ct);
    }
}
